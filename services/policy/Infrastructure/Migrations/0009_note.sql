-- policy-service — 0009 notes on policy and member (phase 19.3, design 38 §5). Additive + idempotent.
--
-- THE NOTES REQUIREMENT. Design 38 §5 is the specification and this follows it exactly. Four properties carry
-- the whole feature, and each is enforced structurally rather than by convention:
--
--   APPEND-ONLY   A note's body is NEVER updated and NEVER deleted. The only permitted mutation is
--                 Active→Cancelled. A correction is a NEW note that may point at the one it supersedes.
--                 Enforced by trg_note_append_only below, not only by the API returning 409.
--
--   SIGNED        authored_by_username and authored_by_display are SNAPSHOTS taken from the token principal
--                 at write time — never a join to identity. A note about a beneficiary written in 2026 must
--                 still show who wrote it after that person is renamed, moves team, or is de-provisioned.
--                 A join would quietly rewrite the signature, or lose it entirely.
--
--   TIMESTAMPED   authored_at is UTC. The API returns UTC and the UI renders Africa/Cairo (38 §5.3).
--
--   CANCELLABLE   Cancelling requires a reason and keeps the note VISIBLE, struck through. Hiding it would
--                 make the record unreadable: "there was a note here and it was withdrawn, by X, on Y,
--                 because Z" is information; a gap is not.
--
-- MINIMUM-NECESSARY is enforced at the SERVICE by visibility_class, not by the UI hiding a field. Finance and
-- the Call Centre never receive a Clinical or Restricted BODY — they receive the note's existence (type, date,
-- author, status). That projection lives in NoteProjection.cs and is proven by reflection over the serialized
-- payload, because "the screen does not show it" is not a control.

CREATE TABLE IF NOT EXISTS policy.note (
    note_id                uuid PRIMARY KEY,
    tenant_id              text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',

    -- A note hangs off a policy or a member. scope_ref is policy_id or enrollment_id — a VALUE rather than two
    -- nullable FKs, so a third scope (a group, a payer) does not need a schema change to arrive.
    scope                  varchar(10) NOT NULL CHECK (scope IN ('Policy','Member')),
    scope_ref              uuid NOT NULL,

    note_type              varchar(20) NOT NULL CHECK (note_type IN
                             ('General','Eligibility','Exception','Approval','Complaint','Financial',
                              'Clinical','Administrative')),
    body                   text NOT NULL CHECK (length(btrim(body)) > 0),

    -- What the body is ABOUT, which decides who may read it. Deliberately separate from note_type: an
    -- Exception note can carry clinical reasoning, and a Clinical note can be purely administrative in content.
    visibility_class       varchar(20) NOT NULL CHECK (visibility_class IN
                             ('Administrative','Financial','Clinical','Restricted')),

    -- The signature, snapshotted. See the header note on why this is not a join.
    authored_by_user_id    uuid NOT NULL,
    authored_by_username   varchar(128) NOT NULL,
    authored_by_display    varchar(200) NOT NULL,
    authored_at            timestamptz NOT NULL,

    status                 varchar(12) NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Cancelled')),
    cancelled_by_user_id   uuid,
    cancelled_by_username  varchar(128),
    cancelled_at           timestamptz,
    cancellation_reason    text,

    -- A correction is a new note; this links it back so the thread reads in order.
    supersedes_note_id     uuid REFERENCES policy.note(note_id),
    pinned                 boolean NOT NULL DEFAULT false,

    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_at             timestamptz NOT NULL DEFAULT now(),

    -- A cancellation without who, when and why is not a cancellation — it is a note that changed state for
    -- reasons nobody recorded, on the one surface most likely to be read back in a dispute.
    CONSTRAINT ck_note_cancellation_complete CHECK (
        status <> 'Cancelled'
        OR (cancelled_by_user_id IS NOT NULL AND cancelled_at IS NOT NULL
            AND cancellation_reason IS NOT NULL AND length(btrim(cancellation_reason)) > 0)
    ),
    -- And the converse: an Active note must not carry cancellation fields, which would render as withdrawn.
    CONSTRAINT ck_note_active_is_clean CHECK (
        status <> 'Active'
        OR (cancelled_by_user_id IS NULL AND cancelled_at IS NULL AND cancellation_reason IS NULL)
    )
);
CREATE INDEX IF NOT EXISTS ix_note_scope ON policy.note (scope, scope_ref, authored_at DESC);
CREATE INDEX IF NOT EXISTS ix_note_status ON policy.note (status);
CREATE INDEX IF NOT EXISTS ix_note_pinned ON policy.note (pinned) WHERE pinned;
CREATE INDEX IF NOT EXISTS ix_note_author ON policy.note (authored_by_user_id);

-- ---- Append-only, enforced by the database ------------------------------------------------------------------
-- The API answers a body edit with 409, but that is not the invariant: a repair script, a future endpoint or a
-- psql session walks straight past it. This trigger makes the rule structural, so a note's body and signature
-- cannot be rewritten by ANY path.
--
-- The ONE permitted mutation is Active→Cancelled (plus pin/unpin, which changes no content). Everything that
-- says WHAT the note is and WHO wrote it is frozen from the moment it is written.
CREATE OR REPLACE FUNCTION policy.guard_note_append_only()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'notes are append-only: cancel note % instead of deleting it', OLD.note_id
            USING ERRCODE = 'raise_exception';
    END IF;

    IF OLD.body IS DISTINCT FROM NEW.body THEN
        RAISE EXCEPTION 'note % is append-only: its body can never be edited — write a new note that supersedes it', OLD.note_id
            USING ERRCODE = 'raise_exception';
    END IF;

    IF OLD.authored_by_user_id IS DISTINCT FROM NEW.authored_by_user_id
       OR OLD.authored_by_username IS DISTINCT FROM NEW.authored_by_username
       OR OLD.authored_by_display IS DISTINCT FROM NEW.authored_by_display
       OR OLD.authored_at IS DISTINCT FROM NEW.authored_at THEN
        RAISE EXCEPTION 'note % is signed: its author and timestamp can never be changed', OLD.note_id
            USING ERRCODE = 'raise_exception';
    END IF;

    IF OLD.scope IS DISTINCT FROM NEW.scope
       OR OLD.scope_ref IS DISTINCT FROM NEW.scope_ref
       OR OLD.note_type IS DISTINCT FROM NEW.note_type THEN
        RAISE EXCEPTION 'note % is append-only: what it is about cannot be reassigned', OLD.note_id
            USING ERRCODE = 'raise_exception';
    END IF;

    -- Visibility may only be RAISED, never lowered. Lowering it would retroactively expose a clinical body to
    -- roles that were correctly denied it — the same discipline documents follow in 19.3b.
    IF OLD.visibility_class IS DISTINCT FROM NEW.visibility_class
       AND policy.note_visibility_rank(NEW.visibility_class) < policy.note_visibility_rank(OLD.visibility_class) THEN
        RAISE EXCEPTION 'note % visibility can be raised but never lowered (% → %)',
            OLD.note_id, OLD.visibility_class, NEW.visibility_class
            USING ERRCODE = 'raise_exception';
    END IF;

    IF OLD.status = 'Cancelled' AND NEW.status <> 'Cancelled' THEN
        RAISE EXCEPTION 'note % is cancelled and cannot be reinstated — write a new note', OLD.note_id
            USING ERRCODE = 'raise_exception';
    END IF;

    RETURN NEW;
END $$;

CREATE OR REPLACE FUNCTION policy.note_visibility_rank(v text)
RETURNS int LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE v
        WHEN 'Administrative' THEN 1
        WHEN 'Financial'      THEN 2
        WHEN 'Clinical'       THEN 3
        WHEN 'Restricted'     THEN 4
        ELSE 0 END
$$;

DROP TRIGGER IF EXISTS trg_note_append_only ON policy.note;
CREATE TRIGGER trg_note_append_only BEFORE UPDATE OR DELETE ON policy.note
    FOR EACH ROW EXECUTE FUNCTION policy.guard_note_append_only();

-- ---- Grants + tenant RLS (ADR-0011) -------------------------------------------------------------------------
-- No DELETE grant: the trigger refuses it anyway, and withholding the privilege means a bug cannot attempt it.
GRANT SELECT, INSERT, UPDATE ON policy.note TO hbmp_app;

ALTER TABLE policy.note ENABLE ROW LEVEL SECURITY;
ALTER TABLE policy.note FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_note ON policy.note;
CREATE POLICY rls_note ON policy.note USING (
    tenant_id = current_setting('app.tenant_id', true)
);
