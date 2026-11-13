# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-11-13

### Added

- KVKK data-subject request workflow as a pure state machine: intake, identity verification, discovery, plan,
  approval by a second person, execution, written answer, rejection and withdrawal (KVKK Art. 11, 13).
- Proportionate identity verification: assurance per request type; qualified channels (KEP, e-signature, mobile
  signature), registered sender e-mail, signed letters; one-time codes only to a phone or e-mail already on file,
  hashed with a pepper, five attempts, then manual review.
- 30-day deadline in Türkiye local time (receipt day not counted), strict by default, optional roll to the next
  working day with the 2025-2027 official holiday table; deadline monitor with at-risk / overdue audit events and a
  metric of open requests by stage.
- Discovery driven by the kvkk-data-inventory `classification.json`: canonical TCKN / phone / e-mail matching (same
  semantics in SQL and in memory), breadth-first walk of declared foreign keys, owner vs mention key columns, free-text
  matching with digit boundaries, unreachable-table report, search restricted to identifiers bound by verification.
- Erasure planner: retention first (with release date and legal basis), columns cleared while retained, anonymisation
  instead of deletion for rows that kept rows reference, mention redaction, recipients to notify (Art. 11(1)(f)),
  SHA-256 plan hash for approval.
- PostgreSQL executor: one transaction per source, `FOR UPDATE` row locks, in-transaction drift check, per-statement
  row-count checks, post-check of residual data; resumable after a crash between source commit and workflow record;
  per-request advisory lock against double execution.
- Access export generated from the source on demand and never stored (only its SHA-256).
- Answer letters in Turkish and English, golden-tested.
- Evidence pack: per-request hash-chained audit log (append-only trigger), ZIP with SHA-256 manifest signed with
  ECDSA P-256, offline verifier and `/api/v1/evidence/verify`.
- Index advisor based on `EXPLAIN`: foreign-key, expression and trigram indexes for discovery queries.
- Synthetic clinic + HR + portal dataset with per-person ground truth, built on kvkk-data-inventory's schema names.
- Minimal API with role-based operator keys, OpenAPI + Scalar, health checks, Serilog JSON logs, OpenTelemetry.
- `docker compose up` demo, Testcontainers integration tests, benchmarks, ADRs and threat model.
