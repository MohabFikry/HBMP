-- policy-service — 0011 the policy/member change timeline (phase 19.3c, design 38 §5c). Additive + idempotent.
--
-- ============================================================================================================
-- ONE SOURCE OF TRUTH. This is a PROJECTION, not a log.
-- ============================================================================================================
-- Nothing writes to this table as part of doing its work. Entries are derived from the hash-chained
-- audit_event stream and the domain events already emitted by 19.2/19.3/19.3b, and can be thrown away and
-- rebuilt at any time.
--
-- The alternative — asking every writer to also append "what happened" — produces a second log that drifts
-- from the audit trail the moment one path forgets, and a history that disagrees with the audit trail is
-- worse than no history: it looks authoritative and is quietly wrong. Nobody notices, because the only way to
-- notice is to compare two things nobody compares.
--
-- WHY IT LIVES IN policy-service AND NOT reporting-service (the choice ADR-0022 records):
--   * The scope refs are policy_id and enrollment_id, which this service owns. Elsewhere they are opaque.
--   * The diff projection reuses the visibility classes and rules already here (19.3 notes, 19.3b documents).
--     Reimplementing that redaction in another service is precisely the second-redaction-path the design
--     forbids.
--   * reporting-service is a DE-IDENTIFIED AGGREGATE read model by design — its financial_fact has no
--     diagnosis column and a test asserts so against information_schema. Per-member, class-projected diffs
--     are the opposite of that property, and putting them there would quietly make a de-identified store
--     identifiable.
-- ============================================================================================================

CREATE TABLE IF NOT EXISTS policy.entity_timeline (
    entry_id         uuid PRIMARY KEY,
    tenant_id        text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',

    scope            varchar(10) NOT NULL CHECK (scope IN ('Policy','Member')),
    scope_ref        uuid NOT NULL,

    occurred_at      timestamptz NOT NULL,
    event_type       varchar(64) NOT NULL,
    event_category   varchar(20) NOT NULL CHECK (event_category IN
                       ('Lifecycle','Coverage','Plan','Enrolment','Note','Document','Utilization',
                        'Authorization','Claim','Access','BulkOperation','Administrative')),

    -- The actor, SNAPSHOTTED — same discipline as notes and documents. History has to stay readable after a
    -- user is renamed or de-provisioned, and a join would rewrite or lose the name on exactly the entries a
    -- review cares about.
    actor_user_id    uuid,
    actor_username   varchar(128),
    actor_display    varchar(200),

    -- Human-readable, in BOTH locales. A timeline an Arabic-speaking officer cannot read is not a timeline.
    summary_en       text NOT NULL,
    summary_ar       text NOT NULL,

    -- MINIMIZED before/after — only the fields that changed, and withheld entirely from operational roles
    -- when the entry's class is Clinical or Restricted. Projected at READ time by the same rules notes use,
    -- because a stored-redacted diff would need re-storing every time a role's entitlement changed.
    change_diff      jsonb,
    visibility_class varchar(20) NOT NULL DEFAULT 'Administrative'
                       CHECK (visibility_class IN ('Administrative','Financial','Clinical','Restricted')),

    source_service   varchar(40) NOT NULL,
    correlation_id   varchar(64),
    source_event_id  uuid NOT NULL,

    -- Deep link: which entity this entry is ABOUT, so a row can navigate to the note/document/claim it names.
    target_ref       uuid,
    target_kind      varchar(40),

    created_at       timestamptz NOT NULL DEFAULT now(),

    -- IDEMPOTENCY, and the whole reason a replay is safe. Re-projecting the same source event cannot create a
    -- second entry, so a rebuild is a no-op where history already exists and a repair where it does not.
    CONSTRAINT uq_entity_timeline_source UNIQUE (source_event_id)
);
CREATE INDEX IF NOT EXISTS ix_timeline_scope ON policy.entity_timeline (scope, scope_ref, occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_timeline_category ON policy.entity_timeline (event_category);
CREATE INDEX IF NOT EXISTS ix_timeline_actor ON policy.entity_timeline (actor_user_id);
CREATE INDEX IF NOT EXISTS ix_timeline_occurred ON policy.entity_timeline (occurred_at DESC);

-- ---- Append-only, enforced -----------------------------------------------------------------------------------
-- A timeline entry is never edited. A correction is a NEW entry referencing the original — the same rule notes
-- follow, for the same reason: a history that can be rewritten is not a history.
--
-- DELETE is permitted ONLY inside a declared REBUILD, signalled by the session GUC app.timeline_rebuild='on'.
-- The asymmetry is deliberate: discarding ALL derived data and re-projecting it is safe in a way that quietly
-- removing one inconvenient line is not. Requiring an explicit session flag means a rebuild is a decision
-- somebody made, visible in the connection's own state, rather than something a stray DELETE achieves.
CREATE OR REPLACE FUNCTION policy.guard_entity_timeline_append_only()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF coalesce(current_setting('app.timeline_rebuild', true), 'off') = 'on' THEN
            RETURN OLD;   -- a declared rebuild: the projection is derived, so it may be discarded wholesale
        END IF;
        RAISE EXCEPTION 'entity_timeline is append-only: rebuild the whole projection, never delete one entry'
            USING ERRCODE = 'raise_exception';
    END IF;
    RAISE EXCEPTION 'entity_timeline entry % is immutable: a correction is a NEW entry referencing it', OLD.entry_id
        USING ERRCODE = 'raise_exception';
END $$;
DROP TRIGGER IF EXISTS trg_entity_timeline_append_only ON policy.entity_timeline;
CREATE TRIGGER trg_entity_timeline_append_only BEFORE UPDATE OR DELETE ON policy.entity_timeline
    FOR EACH ROW EXECUTE FUNCTION policy.guard_entity_timeline_append_only();

-- ---- Grants + tenant RLS (ADR-0011) ---------------------------------------------------------------------------
-- DELETE is granted because the rebuild path needs it, but the trigger above still refuses every delete that
-- is not inside a declared rebuild — so the privilege alone buys nothing.
GRANT SELECT, INSERT, DELETE ON policy.entity_timeline TO hbmp_app;

ALTER TABLE policy.entity_timeline ENABLE ROW LEVEL SECURITY;
ALTER TABLE policy.entity_timeline FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_entity_timeline ON policy.entity_timeline;
CREATE POLICY rls_entity_timeline ON policy.entity_timeline USING (
    tenant_id = current_setting('app.tenant_id', true)
);
