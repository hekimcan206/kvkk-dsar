# 0005. Proportionate identity verification: channel first, OTP only to a contact already on file

- Status: accepted
- Date: 2026-11-13

## Context

The Communiqué on Applications (Art. 5) lets a data subject apply in writing, by KEP (registered e-mail), secure
electronic signature, mobile signature, from an e-mail address previously given to the controller and on file, or
through an application the controller provides. Erasing or disclosing someone's data to an impostor is a breach;
demanding too much is unlawful too: the Board held (decision 2019/296) that extra identity information may only be
asked for where there is reasonable doubt, and verification must not make the right impractical.

## Options considered

1. Always ask for an ID copy: heavy, collects more data, and a copy is easy to forge.
2. OTP to the phone in the application: proves only that the applicant controls *some* phone.
3. **Assurance levels by request type, satisfied by the channel where possible, stepped up with an OTP to a contact
   that is already on file for the identifier** (chosen).

## Decision

- Required assurance: erasure and rectification `High`, access `Substantial`, objection and compensation `Low` (they
  start verified and are decided by people).
- Factors: `QualifiedChannel` (KEP / e-signature / mobile signature, recorded by a verifier with the message or
  signature reference; a factor claimed for a web form changes nothing), `RegisteredEmail` (the sender address is an
  e-mail on file for the person, checked by the system), `SignedLetter`, `OtpToContactOnFile`, `KnowledgeMatch` (the
  birth date given equals one on file). Two independent factors make `High`; a qualified channel alone makes `High`
  and is never asked for an OTP.
- The OTP goes to a phone (else e-mail) found in the records that carry the applicant's TCKN, never to a number only
  given in the application. Six digits, 10-minute lifetime, HMAC(pepper, request id ‖ code) stored, constant-time
  comparison, attempts counted atomically in the database, five wrong codes lock the challenge and the request goes to
  manual review.
- Verification also decides what discovery may use (ADR 0001): the identifiers bound to the verified person.
- Operators authenticate with API keys whose SHA-256 is configured (roles: intake, verifier, dpo, approver, executor,
  auditor); every step records the operator, which makes four-eyes approval checkable.

## Consequences

- The common case (an existing patient with a phone on file) verifies in one round trip; nobody is asked for a
  document copy.
- A person with no contact on file goes to manual review (signed letter or qualified channel); the API answers 409
  with that instruction.
- The signature or KEP check itself is done by a person and only recorded here; the service does not validate
  qualified certificates. SMS / e-mail delivery is an interface; the demo uses an in-memory inbox that must never be
  enabled in production.
- API keys are a demo-sized scheme; production would put OIDC in front and map groups to the same roles.

## Sources

- Communiqué on the Procedures and Principles of Application to the Data Controller, Art. 5:
  <https://www.lexpera.com.tr/mevzuat/tebligler/veri-sorumlusuna-basvuru-usul-ve-esaslari-hakkinda-teblig>
- Board decision 2019/296: <https://www.kvkk.gov.tr/Icerik/6557/2019-296>
- NIST SP 800-63B (authenticator assurance levels, OTP lifetime and throttling) as the model for levels and rate limits.
