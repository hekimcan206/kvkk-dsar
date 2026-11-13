# Security policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting instead:
**Security → Report a vulnerability** on this repository. You can expect an acknowledgement within 5 working days. If
the report is confirmed, a fix or mitigation plan follows within 30 days.

Please include the affected version or commit, steps to reproduce and the impact you observed.

## Security-relevant design

See [docs/threat-model.md](docs/threat-model.md) for assets, threats and mitigations. In short:

| Area | Default |
|---|---|
| Identity of applicants | Assurance per request type; OTP only to a contact already on file; 5 attempts; qualified channels need no OTP |
| What is searched | Only identifiers bound to the verified person; unbound ones are logged as ignored |
| Destructive changes | Per-row plan, approved by a second person by its SHA-256; one transaction per source with row locks and a drift check |
| Database access | Workflow: `dsar_app` (no DELETE, INSERT/SELECT on the audit chain). Source: `dsar_finder` (SELECT), `dsar_eraser` (SELECT/UPDATE/DELETE, no INSERT/DDL). Migrations and seeding run as separate owner roles |
| Personal data at rest | Identifiers of open requests sealed with AES-256-GCM and purged on close; keyed HMAC fingerprints; access exports never stored |
| Audit | Per-request hash chain; append-only trigger also stops the table owner |
| Evidence | ZIP with SHA-256 manifest signed with ECDSA P-256; verifier checks signature, trusted key id, files and chain |
| Operators | API keys, only SHA-256 configured, constant-time comparison; roles intake / verifier / dpo / approver / executor / auditor |
| Secrets | None in the repository. `.env.example` and compose hold clearly marked development-only values |
| Demo inbox | `/demo/inbox` exposes one-time codes and exists only when `Demo:Inbox=true`; never enable it in production |
