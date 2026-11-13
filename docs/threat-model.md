# Threat model

Scope: the kvkk-dsar API, its workflow database (`dsar`), and the source databases it reads and changes (the
synthetic `clinic`). All data in this repository is synthetic.

## Assets

| Asset | Why it matters |
|---|---|
| Source data about people (clinic records) | The subject of the requests; unauthorised erasure or disclosure is a breach |
| Applicant identifiers (TCKN, phone, e-mail) of open requests | Personal data in the workflow store |
| Access exports | A full copy of one person's data |
| Erasure plans and approvals | Decide what is irreversibly destroyed |
| Audit chain and evidence packs | The controller's proof towards the Board (KVKK Art. 14, Erasure Regulation Art. 7) |
| Keys: fingerprint key, OTP pepper, subject seal key, evidence signing key, operator API keys | Compromise defeats the controls above |

## Actors

- **Impostor applicant**: wants someone else's data (access) or wants it destroyed (erasure).
- **Curious or malicious operator**: insider with an API key and one or more roles.
- **Database reader**: someone with a dump or read access to the workflow database or a backup.
- **Network attacker**: between operators and the API.
- **Evidence forger**: wants to present a pack that was never produced, or alter one.

## Threats and mitigations (STRIDE)

| # | Threat | Mitigation | Where tested |
|---|---|---|---|
| S1 | Impostor applies with a victim's TCKN and their own phone | OTP goes only to a contact **on file** for that TCKN, never to the one in the application | `ApiTests` (OTP via demo inbox), `DsarService.ChallengeAsync` |
| S2 | Impostor uses their own TCKN, adds the victim's e-mail, verifies with their own phone, and receives the victim's portal account in "their" export | Discovery searches only **bound** identifiers; unbound ones are logged as ignored (ADR 0001) | `An_email_that_is_not_on_file_for_the_verified_person_is_never_searched` |
| S3 | OTP guessing | 10^6 codes, 10 min, 5 attempts counted atomically in SQL, then manual review | `OtpTests`, `Five_wrong_codes_send_the_request_to_manual_review` |
| S4 | Operator impersonation | API key per operator (SHA-256 configured, constant-time compare); every step records the operator | `Planner_cannot_approve_and_unknown_keys_are_refused` |
| T1 | One operator plans and approves a destructive plan alone | Four eyes enforced by the pure workflow (planner ≠ approver) and roles | FsCheck property `No_command_sequence_bypasses_the_gates` |
| T2 | Plan swapped after approval | Approval names the plan SHA-256; executor recomputes the stored plan hash and refuses a mismatch | `DsarWorkflow.Approve`, `DsarService.ExecuteAsync` |
| T3 | Source changes between approval and execution (new rows erased unreviewed, or rows missed) | Row locks + in-transaction re-discovery; drift rolls back and voids the approval | `A_new_row_after_planning_is_drift_and_nothing_changes` |
| T4 | Partial erasure on failure | One transaction per source, row counts checked per statement | `A_failure_before_commit_rolls_back_every_statement` |
| T5 | Double execution (two operators, retries, two API instances) | Workflow row lock + per-request advisory lock; source row locks make a second run see drift | `Parallel_execute_calls_run_the_plan_once`, `Two_executors_racing_on_one_plan_apply_it_exactly_once` |
| T6 | Audit events edited or deleted afterwards | Hash chain; trigger refuses UPDATE/DELETE/TRUNCATE even for the owner; service role lacks the privilege | `Audit_chain_is_append_only_even_for_the_schema_owner`, compose demo CI step |
| T7 | Evidence pack altered or forged | ECDSA P-256 signed manifest, per-file SHA-256, chain verified; trusted key id defeats re-signing | `EvidenceTests`, demo tamper check |
| R1 | Controller cannot prove it answered in time, or what it did | Deadline, response date, plan, execution log and letters in the pack; monitor records at-risk/overdue once | `Deadline_monitor_marks_an_old_open_request_overdue_once` |
| I1 | Workflow database dump leaks identifiers | Identifiers sealed with AES-256-GCM while open, purged on close (check constraint); fingerprints are keyed HMACs; discovery stores row addresses only; exports never stored | `Access_and_erasure_need_the_export_first_and_the_export_is_not_stored`, end-to-end test asserts no TCKN/phone/e-mail in the pack |
| I2 | Access export shows other people's data (a relative's note) | Mentions in other people's rows export only "your identifier appears here", never the record | `AccessExport.Build` |
| I3 | Logs leak identifiers | Requests are logged by id and reference; problem details carry no values | `DsarExceptionHandler` |
| I4 | SQL injection through identifiers | All values are parameters; identifiers come from the catalog and are quoted | `PostgresSourceReader`, `PlanExecutor` |
| D1 | Discovery causes full scans of large tables per request | Index advisor (`EXPLAIN`) proposes FK, expression and trigram indexes; lock and statement timeouts | `Index_advisor_flags_unindexed_foreign_keys_and_normalised_lookups`, benchmarks |
| E1 | Discovery role used to change data | `dsar_finder` has SELECT only; `dsar_eraser` has no INSERT or DDL; workflow role has no DELETE | `Finder_can_only_read_and_eraser_cannot_insert` |

## Out of scope / residual risk

- Validating qualified electronic signatures or KEP messages: a verifier records the check.
- Real OTP delivery (SMS/e-mail provider) and its own security; the demo inbox must stay disabled outside demos.
- Key management: keys come from configuration; no HSM/KMS, no rotation of the seal key for open requests.
- Transport security: run behind TLS termination; the compose demo binds to 127.0.0.1 only.
- Re-identification risk of anonymised rows (no k-anonymity assessment), see README limitations.
- Relationships not declared as foreign keys and tables without single-column primary keys are not searched (they
  are reported as unreachable when the inventory lists them).
