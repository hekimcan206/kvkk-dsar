# 0003. Erasure as an approved plan: retention first, anonymise what is referenced, one drift-checked transaction

- Status: accepted
- Date: 2026-11-13

## Context

An erasure request under KVKK Art. 11(1)(e) is not "DELETE everything we found". Art. 7 allows erasure only when the
processing conditions are gone; the Regulation on Erasure, Destruction or Anonymisation (Art. 12) lets the controller
refuse, with reasons, the part it must keep. A clinic must keep patient records and invoices for statutory periods,
an employer must keep personnel files after termination. Rows also reference each other: an invoice kept for ten
years references a patient file whose own period may have ended. And between planning and executing, the source keeps
changing (a new appointment is booked).

## Options considered

1. Delete with `ON DELETE CASCADE`: erases rows the law requires to keep, and cascades nobody reviewed.
2. Soft-delete flags: the data is still there; it is not erasure.
3. A **per-row plan** from the discovery and the controller's policy, approved by a second person, executed in one
   transaction after a drift check (chosen).

## Decision

**Planning** (`ErasurePlanner`, pure, deterministic, golden- and property-tested):

- Each owned row gets exactly one action: `Erase`, `Anonymise`, `Retain` (with release date and legal basis) or, for
  someone else's row, `RedactMention`.
- Retention first: if the policy gives the table a retention rule and `anchor + years > today`, the row is kept and only
  the columns the retention purpose does not need are cleared (`clearWhileRetained`: the patient's e-mail, the card
  number on an invoice, the fingerprint template of a still-employed person). An empty anchor (no termination date)
  means the period has not started.
- Referential integrity: decisions are taken children first (reverse topological order of the declared foreign keys);
  a row whose children are not all erased is anonymised instead of deleted, and so is any row in a foreign-key cycle.
- Anonymisation clears every inventory personal-data column of the row: `NULL`, or a typed placeholder for `NOT NULL`
  columns, with per-column overrides (keep the birth *year*, keep gender).
- Mentions clear only the mentioning column (and named companions such as the emergency contact's name); free-text
  notes have the identifier cut out and keep the rest.
- The plan names the recipients to notify (Art. 11(1)(f)) and the delete order. Its SHA-256 is what the approver
  approves; the planner cannot approve their own plan (four eyes, enforced by the workflow and tested as a property).

**Execution** (`PlanExecutor`, as the `dsar_eraser` role that may only SELECT/UPDATE/DELETE):

1. One transaction; `SELECT … FOR UPDATE` on every planned row in a fixed order. That also blocks new child rows,
   because inserting a child takes `FOR KEY SHARE` on its parent.
2. Re-run discovery inside the transaction and compare its fingerprint with the plan's. Any difference is drift: roll
   back, send the request back to discovery. The approver approved *that* set of rows, not today's.
3. Updates, then deletes children before parents; every statement's row count must equal the plan's or everything
   rolls back. A half-erased person is worse than an unerased one, because nobody can tell what is left.
4. Post-check: discovery afterwards may only find rows the plan kept; anything else is residual and the request is not
   marked executed.

Across sources there is no distributed transaction. Each source commits on its own; a crash after a source committed
is resumed by re-running `execute`: a source whose current state already equals the applied plan is recorded as
done, one that matches the original discovery is executed, anything else is drift. A per-request PostgreSQL advisory
lock keeps a second executor out, also across API instances.

## Consequences

- Measured: with drift detection two racing executors apply a plan exactly once; a failure before commit leaves every
  row checksum unchanged; for twelve people executed in a row, rows outside each person's plan keep identical MD5s.
- "Anonymised" here means the row no longer carries the person's direct identifiers. Whether the remaining attributes
  allow re-identification (k-anonymity, l-diversity, as the Board's anonymisation guide asks) is **not** assessed;
  an anonymised patient file under kept invoices is closer to pseudonymised data. This is called out in the README.
- Retention periods and legal bases in `samples/dsar-policy.json` are illustrative and must come from the controller's
  own retention and destruction policy.
- The periodic destruction of retained rows when their period ends is out of scope (the plan records the release date).

## Sources

- KVKK Art. 7 and 11; Regulation on Erasure, Destruction or Anonymisation, Art. 7 (records kept at least three years)
  and Art. 12: <https://www.kvkk.gov.tr/Icerik/5441/KISISEL-VERILERIN-SILINMESI-YOK-EDILMESI-VEYA-ANONIM-HALE-GETIRILMESI-HAKKINDA-YONETMELIK>
- KVKK sample retention and destruction policy: <https://www.kvkk.gov.tr/Icerik/5386/KVKK-KISISEL-VERI-SAKLAMA-ve-IMHA-POLITIKASI>
- PostgreSQL 17, explicit locking (row-level lock conflicts, `FOR KEY SHARE` taken by foreign-key checks):
  <https://www.postgresql.org/docs/17/explicit-locking.html#LOCKING-ROWS>
