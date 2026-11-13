# Contributing

Thanks for taking the time to improve kvkk-dsar.

## Prerequisites

- .NET SDK 10.0.400 or later (`global.json` rolls forward to the latest feature band)
- Docker (the integration tests start PostgreSQL 17 through Testcontainers; `docker compose up` runs the demo)

## Workflow

1. Open an issue first for anything larger than a small fix, so we can agree on the approach.
2. Branch from `main` and keep each pull request to one concern.
3. Before pushing:

   ```bash
   dotnet build                 # must be warning-free: warnings are errors
   dotnet test                  # unit, property, golden and integration tests (Docker running)
   dotnet format --verify-no-changes
   ```

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) are required: `feat:`, `fix:`, `docs:`, `test:`,
`build:`, `ci:`, `refactor:`, `perf:`, `chore:`; `feat!:` or a `BREAKING CHANGE:` footer for breaking changes. The
CHANGELOG is written by hand from them at release time.

## Changing discovery

`InMemorySource.Matches` and `MatchSql.For` define the same comparison twice, once in C# and once in SQL. Change both
together: the integration test `Postgres_discovery_equals_the_in_memory_reference_for_every_person_in_both_modes`
compares their fingerprints for every synthetic person, and the index advisor proposes indexes on exactly the SQL
expressions.

## Changing the workflow or the planner

- `DsarWorkflow` is a pure function; keep it that way. A new command needs a scenario test and must keep the FsCheck
  property `No_command_sequence_bypasses_the_gates` green.
- `ErasurePlanner` output is hashed and approved. A change that alters plans for unchanged input changes plan hashes:
  pending approvals become invalid, which is intended. Keep `Every_plan_is_referentially_safe_and_covers_every_row_once`
  green.

## Golden files

The answer letters are golden-tested (`tests/KvkkDsar.UnitTests/Golden`). Regenerate with `UPDATE_GOLDEN=1 dotnet test`
only after reviewing why the text changed; letters are legal documents.

## Architecture decisions

A change to a decision recorded in `docs/adr/` needs a new ADR that supersedes the old one. Do not edit the old ADR in
place. Use the existing files as the format (MADR: context, options considered, decision, consequences).

## Database migrations

Migrations are numbered SQL files in `src/KvkkDsar.Api/Persistence/Migrations/` (`NNNN_name.sql`), embedded and applied
by `--migrate-only` as the owner role. Never edit a migration that has been applied anywhere: the runner compares
checksums and refuses to continue. Add a new, higher-numbered file instead.

## Data

Synthetic data only. Never add real personal data, real schemas of an employer or customer, or screenshots of them.
