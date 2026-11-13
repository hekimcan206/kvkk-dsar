# Benchmarks

All numbers below were measured on 2026-11-13 with the commands shown; raw output is in
[`benchmark-results/`](../benchmark-results). Medians unless stated otherwise; percentiles are nearest-rank (always an
observed sample).

## Environment

| | |
|---|---|
| CPU | Intel64 family 6 model 186 (13th-gen Core laptop part), 16 logical cores |
| OS / runtime | Windows 11 (10.0.26200), .NET 10.0.12, Release build |
| Docker | Docker Desktop 29.9 on WSL2, VM with 7.6 GB RAM |
| PostgreSQL | `postgres:17-alpine`; in the discovery scenario limited to **2 CPUs, 2 GiB**, `shared_buffers=512MB` |
| Host load | Not idle: 6 containers of an unrelated project were running throughout, more started during the PostgreSQL scenario, and another agent was building and testing its own project on the same machine. Treat differences under ~20 % as noise. |

An earlier run of the discovery scenario at 10,000 people (same code, quieter host) gave 141 ms / 25.7 ms median
discovery before / after the index advice, against 195 ms / 46.5 ms in the run reported below: that is the size of
the host-load effect.

## 1. Discovery accuracy and ablations (in memory)

`PEOPLE=5000 dotnet run -c Release --project benchmarks/KvkkDsar.Benchmarks -- accuracy`

5,000 synthetic people, 29,378 rows; ground truth: 28,873 owned rows and 870 mentions in other people's rows. The
in-memory source has the same semantics as the PostgreSQL reader (asserted for 400 people by an integration test).

| Mode | Owned rows precision | Owned rows recall | Mention recall | People with all rows found | People with someone else's rows claimed |
|---|---:|---:|---:|---:|---:|
| Full (canonical match, FK walk, mentions) | 1.000 | 1.000 | 1.000 | 4,035 / 4,035 | 0 |
| No canonical comparison (raw strings) | 1.000 | 0.973 | 0.346 | 3,245 / 4,035 | 0 |
| No foreign-key walk | 1.000 | 0.187 | 1.000 | 532 / 4,035 | 0 |
| Mentions treated as ownership | 0.939 | 1.000 | 0.000 | 4,035 / 4,035 | 1,399 |
| Naive (raw exact match, no FK, no mentions) | 0.830 | 0.180 | 0.000 | 322 / 4,035 | 952 |

Read this as an ablation, not as an accuracy claim: the generator and the rules were written by the same person, so
the full mode finding everything says that the rules cover the cases the generator plants (spelling variants,
portal accounts under other e-mails, emergency contacts, relatives in notes, digit-embedded decoys), nothing more.
What the table does show:

- 81 % of a person's rows are reachable only through a foreign key (appointments, diagnoses, prescriptions and
  invoices carry a patient id, not a TCKN).
- Raw comparison loses portal accounts and two thirds of the phone mentions in notes (other spellings).
- Without the owner / mention distinction, 1,399 people would have someone else's rows (an employee file, another
  patient's appointment) planned for erasure; the naive search also misses 82 % of the person's own rows.

## 2. Discovery and erasure latency on PostgreSQL, before and after the index advice

`SIZES=10000,100000 SUBJECTS=200 dotnet run -c Release --project benchmarks/KvkkDsar.Benchmarks -- discovery`

Testcontainers PostgreSQL 17 (2 CPUs, 2 GiB). The synthetic clinic is loaded with binary `COPY`, then 200 random
subjects with data are discovered sequentially (10 warm-up runs first). 30 other subjects are fully processed
(discover, plan, execute in one transaction with the drift check). Then the index advisor's DDL is applied, `ANALYZE`
runs, and the same 200 subjects and 30 new ones are measured again. Median discovery issues 16 queries and finds 7-8 rows.

| People (rows) | Advice applied | Index build | Discovery median | Discovery p95 | Plan + execute median | p95 |
|---|---|---:|---:|---:|---:|---:|
| 10,000 (58,751) | no | | 195 ms | 238 ms | 437 ms | 538 ms |
| 10,000 (58,751) | yes: 4 FK indexes, 1 trigram | 0.8 s | **46.5 ms** | 67.9 ms | **124 ms** | 154 ms |
| 100,000 (586,449) | no | | 1,871 ms | 2,618 ms | 4,597 ms | 6,533 ms |
| 100,000 (586,449) | yes: 4 FK, 4 expression, 1 trigram | 6.9 s | **51.0 ms** | 178 ms | **107 ms** | 651 ms |

- Without the advice, discovery grows linearly with the data (10x rows, 9.6x time): every foreign-key step and every
  normalised phone / e-mail comparison is a sequential scan, and the free-text phone search runs `regexp_replace` over
  every appointment note. With the advice the median stays flat (46.5 ms vs 51.0 ms for 10x the data); the higher p95
  at 100,000 people coincided with other containers starting on the host.
- At 10,000 people the advisor proposes no expression indexes: the advisor only reports tables of at least 10,000 rows
  (`customers` and `hastalar` were below that), which is also why the gain there is smaller.
- The application's own index on `hastalar.tc_kimlik_no` is used as is; the advisor confirms it and proposes nothing
  for it.
- Execution includes the in-transaction re-discovery, so it benefits from the same indexes; 0 drifts in 120 runs.

## 3. End to end through the HTTP API (docker compose)

`docker compose up -d`, then `REQUESTS=40 dotnet run -c Release --project benchmarks/KvkkDsar.Benchmarks -- http`

The compose stack (no CPU limits, 2,000 people, **without** the advised indexes) and a client on the host run 40
erasure requests in a row, each through every step: intake, OTP challenge, reading the demo inbox, OTP confirm,
discovery, plan, approval by a second operator, execution, answer letter; then the evidence pack is downloaded. One of
the 40 subjects had neither phone nor e-mail on file and was correctly sent to manual review; 39 completed.

| Stage | Median | p95 |
|---|---:|---:|
| create | 14.1 ms | 31.5 ms |
| OTP challenge (contact lookup + code) | 18.4 ms | 54.4 ms |
| OTP confirm (+ binding identifiers) | 30.6 ms | 62.4 ms |
| discovery | 81.3 ms | 116 ms |
| plan | 22.5 ms | 49.1 ms |
| approve | 14.6 ms | 43.6 ms |
| execute (3 discoveries: before, in-transaction, post-check) | 224 ms | 321 ms |
| respond (letters, purge) | 21.9 ms | 48.4 ms |
| **whole request, excluding the evidence download** | **449 ms** | 604 ms |
| evidence pack download | 11.4 ms | 33.1 ms |

Evidence packs for these requests were 8.9 KB median (8.2-9.7 KB). The compose demo itself (one access + erasure
request including the export and the tamper check) completed 18 s after `docker compose up -d` on an already built
image, most of it PostgreSQL start-up and seeding.

## 4. Evidence pack build and verify

`ITERATIONS=300 dotnet run -c Release --project benchmarks/KvkkDsar.Benchmarks -- evidence`

Pack for the synthetic person with the most rows (15 owned rows, 16 plan actions) and 20 audit events, 4.6 KB:
build + ECDSA P-256 sign **0.93 ms** median (p95 1.46 ms); full verify (signature, hashes, chain) **1.39 ms** median
(p95 2.21 ms). Signing is not a cost worth optimising.

## What was not measured

- Real legacy schemas and real inventories: accuracy on them is unknown (see the limitations in the README).
- Concurrency throughput (many requests at once): DSAR volumes are low (requests per day, not per second), so the
  benchmark measures latency of one request at a time; correctness under concurrency is covered by tests instead.
- Sizes beyond 100,000 people: the unindexed run already takes ~2 s per discovery at that size; the indexed curve
  is flat between 10,000 and 100,000 people, and nothing larger was run on this shared machine.
