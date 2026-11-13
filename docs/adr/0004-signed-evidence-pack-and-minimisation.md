# 0004. A signed evidence pack built from a hash-chained audit log, without the personal data

- Status: accepted
- Date: 2026-11-13

## Context

The Regulation on Erasure, Destruction or Anonymisation (Art. 7(3)) requires every erasure, destruction and
anonymisation to be recorded and the records kept for at least three years. When a data subject complains to the
Board (KVKK Art. 14), the controller must show what was asked, how identity was checked, what was found, what was done
and when it was answered. Such a record easily becomes the largest copy of the very data that was erased.

## Options considered

1. Application logs: mutable, incomplete, full of identifiers.
2. Store everything (requests, discovered values, the export) in the workflow database: proof by hoarding.
3. **Records of decisions and row addresses, a per-request hash chain, and a signed ZIP for export** (chosen).

## Decision

- Every state change and side event (OTP attempt, export, deadline stage, artifact recorded) appends an event to
  `dsar.audit_events`: `hash = SHA-256(prev_hash ‖ seq ‖ at ‖ actor ‖ event ‖ data)`. `data` is stored as text, not
  `jsonb` (which reorders keys and would break the hashes). A trigger refuses UPDATE, DELETE and TRUNCATE even for the
  table owner, and the service role has no UPDATE/DELETE privilege on it.
- Identifiers of an open request are sealed with AES-256-GCM (request id as associated data) and **purged** when the
  request closes; a check constraint forbids a closed request that still holds them. What stays is masked forms
  (`59*******34`), a keyed HMAC fingerprint (an unkeyed hash of a TCKN is reversible: there are only about 10^9 valid
  ones), the letters, and row addresses.
- The access export is generated from the source when asked and never stored; only its SHA-256 is kept and cited in
  the letter.
- The evidence pack is a ZIP: request, verification, deadline, discovery map, plan, execution log with SQL templates
  and row counts, post-check, both letters, and the audit chain as JSON lines, plus `manifest.json` (SHA-256 of every
  file, chain head) signed with ECDSA P-256. The verifier checks the signature, the key id (against a trusted key id
  when given, since anyone can re-sign with their own key), every file hash, unlisted files, and the chain.

## Consequences

- An outsider can check a pack offline. Tests cover an edited file, an added file, an edited audit event, a deleted or
  reordered event and a pack re-signed with another key.
- Without `Evidence:SigningKeyPem` the service signs with an ephemeral key and says so in the log; packs from earlier
  processes then verify only against their embedded key. Key management (HSM/KMS, rotation, publishing key ids) is out
  of scope.
- Purging identifiers means a closed request cannot be re-run; a new request is needed, by design.
- The letter carries the subject's name (it is the answer that must be kept); everything else in the pack is free of
  values. An integration test asserts the pack contains neither the TCKN, the phone nor the e-mail.

## Sources

- Regulation on Erasure, Destruction or Anonymisation (RG 28.10.2017, No. 30224), Art. 7:
  <https://www.resmigazete.gov.tr/eskiler/2017/10/20171028-10.htm>
- KVKK Art. 12 (data security) and Art. 14 (complaints to the Board).
- RFC 6962 style hash chaining and NIST FIPS 186-5 (ECDSA P-256) as the primitives.
