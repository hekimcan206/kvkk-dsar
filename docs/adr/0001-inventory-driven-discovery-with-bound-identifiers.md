# 0001. Inventory-driven discovery over bound identifiers and declared foreign keys

- Status: accepted
- Date: 2026-11-13

## Context

KVKK Art. 11 gives a data subject the right to learn whether and which personal data is processed, and to ask for
its erasure; Art. 13 obliges the controller to answer within 30 days. Both start with the same question: *where is
everything about this person?* In a real system the answer is spread over tables that carry a TCKN, tables that carry
only a patient id, portal accounts keyed by an e-mail in a different spelling, and free-text notes.

The same author's [kvkk-data-inventory](https://github.com/hekimcan206/kvkk-data-inventory) already produces a
classification of every personal-data column (`classification.json`: table, column, PII type, VERBİS category,
special-category flag). The question is how a DSAR tool should use it, and with which identifiers it may search.

## Options considered

1. **Re-detect at request time** (scan values for the person's identifiers in every column). No inventory needed, but
   a full scan per request, and the answer would disagree with the VERBİS register whenever detection differs.
2. **Hand-written per-system queries** ("find patient by TCKN, then appointments by patient id ..."). Precise, but
   rots as soon as the schema changes and does not generalise to the next system.
3. **Inventory keys + catalog foreign keys** (chosen). The inventory says which columns are identifiers (TCKN, e-mail,
   phone) and which hold personal data; the database catalog says how rows reference each other. Discovery searches
   the key columns, then walks declared foreign keys breadth-first from the rows found.

On the identifiers, the options were to search everything the applicant typed, or only what verification proved.

## Decision

- Search keys are the inventory columns typed `NationalId`, `Email` or `Phone`, minus findings the policy rejects
  (`excludeKeys`, e.g. the branches' business phone numbers that kvkk-data-inventory reports as phone data).
- Comparisons are canonical and identical in SQL and in the in-memory reference: phone = last 10 digits
  (`right(regexp_replace(col, '\D', '', 'g'), 10)`), e-mail = `lower(btrim(col))`, TCKN = the bare column (keeps the
  application's own index usable). Unbounded `text` columns are free text: an SQL pre-filter on digits, then a C#
  matcher with digit boundaries, so a phone number embedded in a longer reference number is not a hit.
- **Ownership vs mention.** A key column is *owner* (the row belongs to the person) or *mention* (the person appears
  in someone else's row: an emergency contact, a relative in a note). Free text is always mention; other columns are
  declared mention in the policy. Only owner rows are expanded through foreign keys and can be erased.
- **Bound identifiers.** Discovery uses only identifiers verification tied to the person: the TCKN whose on-file
  contact received the OTP (or that a qualified signature carries), and contacts that are on file for that TCKN. An
  e-mail typed into the form but not on file for the verified person is recorded as ignored and never searched.
- Tables in the inventory that no key and no foreign-key path reaches are reported as *unreachable* (here
  `audit_log`, keyed by user name), both to the operator and in the answer letter, instead of being silently skipped.
- Discovery results hold row addresses and how each row was found, never values.

## Consequences

- The PostgreSQL reader and the in-memory reader must agree; an integration test checks byte-identical discovery
  fingerprints for all 400 synthetic people in both full and naive mode.
- Measured on 5,000 synthetic people ([benchmarks](../benchmarks.md)): without foreign keys recall of owned rows drops
  to 0.187; without canonical comparison mention recall drops to 0.346; treating mentions as ownership claims someone
  else's rows for 1,399 people. The full mode finds everything **in a generator the author wrote**; real schemas will
  have links no rule here knows.
- Without the bound-identifier rule, an applicant could verify with their own phone and pull a stranger's portal
  account into their access export (integration test `An_email_that_is_not_on_file_for_the_verified_person_is_never_searched`).
- Relationships that are not declared as foreign keys (common in legacy schemas) are invisible; the unreachable list
  makes that visible but does not fix it. Composite keys are not supported.
- PostgreSQL does not index foreign-key columns, and normalised comparisons cannot use a plain column index; the
  index advisor proposes both kinds of index from `EXPLAIN` (measured effect in the benchmarks).

## Sources

- KVKK Law No. 6698, Art. 11 and 13; Communiqué on the Procedures and Principles of Application to the Data Controller
  (RG 10.03.2018, No. 30356): <https://www.lexpera.com.tr/mevzuat/tebligler/veri-sorumlusuna-basvuru-usul-ve-esaslari-hakkinda-teblig>
- KVKK Authority, "İlgili kişiler tarafından yapılan başvuruların cevaplanması yükümlülüğü":
  <https://www.kvkk.gov.tr/Icerik/2046/Ilgili-Kisiler-Tarafindan-Yapilan-Basvurularin-Cevaplanmasi-Yukumlulugu>
- PostgreSQL 17 documentation, foreign keys (referencing columns are not indexed automatically):
  <https://www.postgresql.org/docs/17/ddl-constraints.html#DDL-CONSTRAINTS-FK>
- python-stdnum `stdnum.tr.tckimlik` (reference TCKN algorithm): <https://arthurdejong.org/python-stdnum/doc/stable/stdnum.tr.tckimlik>
