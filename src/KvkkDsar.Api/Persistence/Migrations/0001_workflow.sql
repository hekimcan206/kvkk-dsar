-- Workflow store for data-subject requests. Runs as dsar_migrator (schema owner); the service connects as dsar_app.
-- Personal data is kept out of this database on purpose: identifiers are sealed (AES-256-GCM) and purged when the
-- request is answered; discovery results are row addresses; the access export is never stored.

CREATE TABLE dsar.requests (
    id                  uuid        PRIMARY KEY,
    reference           text        NOT NULL UNIQUE,
    types               text[]      NOT NULL,
    channel             text        NOT NULL,
    status              text        NOT NULL,
    version             integer     NOT NULL DEFAULT 1,
    state               jsonb       NOT NULL,
    received_at         timestamptz NOT NULL,
    due_on              date        NOT NULL,
    due_by              timestamptz NOT NULL,
    deadline_policy     text        NOT NULL,
    deadline_stage      text        NOT NULL DEFAULT 'OnTrack',
    subject_fingerprint text        NOT NULL,
    subject_masked      jsonb       NOT NULL,
    subject_name        text,
    subject_sealed      bytea,
    channel_evidence    text,
    created_by          text        NOT NULL,
    responded_at        timestamptz,
    closed_at           timestamptz,
    CONSTRAINT requests_types_not_empty CHECK (cardinality(types) > 0),
    -- Answered or closed requests must not keep the identifiers.
    CONSTRAINT requests_purged_when_closed CHECK (closed_at IS NULL OR subject_sealed IS NULL)
);

CREATE INDEX requests_open_due_idx ON dsar.requests (due_by) WHERE closed_at IS NULL;
CREATE INDEX requests_fingerprint_idx ON dsar.requests (subject_fingerprint);

CREATE SEQUENCE dsar.request_reference_seq;

CREATE TABLE dsar.otp_challenges (
    request_id uuid        PRIMARY KEY REFERENCES dsar.requests (id),
    code_hash  text        NOT NULL,
    expires_at timestamptz NOT NULL,
    attempts   integer     NOT NULL DEFAULT 0,
    sent_to    text        NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Discovery maps, plans, execution logs, post-checks and letters. Latest per (kind, source); history lives in the
-- audit chain, whose events carry each artifact's SHA-256.
CREATE TABLE dsar.artifacts (
    request_id uuid        NOT NULL REFERENCES dsar.requests (id),
    kind       text        NOT NULL,
    source     text        NOT NULL DEFAULT '',
    content    text        NOT NULL,
    sha256     text        NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (request_id, kind, source)
);

-- Per-request hash chain. data is text, not jsonb: jsonb would reorder keys and break the hashes.
CREATE TABLE dsar.audit_events (
    request_id uuid        NOT NULL REFERENCES dsar.requests (id),
    seq        integer     NOT NULL,
    at         timestamptz NOT NULL,
    actor      text        NOT NULL,
    event      text        NOT NULL,
    data       text        NOT NULL,
    prev_hash  text        NOT NULL,
    hash       text        NOT NULL,
    PRIMARY KEY (request_id, seq)
);

-- Append-only, even for the table owner: the evidence of an erasure must outlive the data.
CREATE FUNCTION dsar.forbid_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'dsar.audit_events is append-only (% refused)', TG_OP USING ERRCODE = 'insufficient_privilege';
END $$;

CREATE TRIGGER audit_events_append_only BEFORE UPDATE OR DELETE ON dsar.audit_events
    FOR EACH ROW EXECUTE FUNCTION dsar.forbid_change();
CREATE TRIGGER audit_events_no_truncate BEFORE TRUNCATE ON dsar.audit_events
    FOR EACH STATEMENT EXECUTE FUNCTION dsar.forbid_change();

GRANT USAGE ON SCHEMA dsar TO dsar_app;
GRANT SELECT, INSERT, UPDATE ON dsar.requests, dsar.artifacts TO dsar_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON dsar.otp_challenges TO dsar_app;
GRANT SELECT, INSERT ON dsar.audit_events TO dsar_app;
GRANT USAGE ON SEQUENCE dsar.request_reference_seq TO dsar_app;
