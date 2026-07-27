-- callcentre-service — Phase 20.3b: the call SUMMARY, separate from the agent's notes (design 39 §5b).
--
-- WHY A SECOND TEXT COLUMN RATHER THAN REUSING `notes`.
--
-- Phase 20 widens the audience for call history: a coordinator, an approver and a clinician can now read what
-- happened on a call. `notes` is the agent's working text — typed mid-call, unedited, sometimes half a sentence,
-- written under the reasonable assumption that only the call centre would ever read it. Promoting that column to
-- the new audience would be a silent, retroactive disclosure of years of it.
--
-- So the audience is widened for a NEW field that was written to be read by others, and `notes` stays exactly
-- where it was. Splitting them is what makes "call history is now visible to more roles" a decision about call
-- history rather than a decision about whatever an agent typed in 2024.
--
-- Additive and idempotent; no data change. No new PII: the phase-15 CRITICAL PRIVACY RULE still holds — this
-- schema stores identifier TYPES, never values, and a summary must not be used to smuggle one in.

ALTER TABLE callcentre.call_interaction
    ADD COLUMN IF NOT EXISTS summary          varchar(500),
    ADD COLUMN IF NOT EXISTS summary_edited_at timestamptz,
    ADD COLUMN IF NOT EXISTS summary_edited_by text;

COMMENT ON COLUMN callcentre.call_interaction.summary IS
    'Operational account of the call, written at wrap-up and READ BY OTHER ROLES via the patient profile '
    '(design 39 §5b). Required at close unless the outcome is Abandoned. Capped at 500 chars so it stays a '
    'summary. Clinical content does not belong here — agents are not clinicians, and a summary reading '
    '"complained of chest pain" creates an unreviewed clinical record in an operational store.';

COMMENT ON COLUMN callcentre.call_interaction.notes IS
    'The AGENT''S working text. Deliberately NOT promoted to other roles — see summary.';

-- Summary corrections are edits WITH HISTORY and a visible "edited" marker, never silent overwrites. A summary
-- other roles rely on, that can be rewritten without trace, is worse than no summary: it reads as a record.
CREATE TABLE IF NOT EXISTS callcentre.call_summary_revision (
    revision_id     uuid PRIMARY KEY,
    interaction_id  uuid NOT NULL REFERENCES callcentre.call_interaction(interaction_id),
    tenant_id       text NOT NULL,
    previous_value  varchar(500),
    new_value       varchar(500),
    edited_by       text,
    edited_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_csr_interaction
    ON callcentre.call_summary_revision (interaction_id, edited_at DESC);

-- The call-history section is read BY BENEFICIARY, not by agent — a coordinator asks "what calls has this member
-- had", which the phase-15 indexes (keyed on agent, or on beneficiary alone) answer by scanning.
CREATE INDEX IF NOT EXISTS ix_ci_beneficiary_started
    ON callcentre.call_interaction (tenant_id, beneficiary_id, started_at DESC);

-- Tenant RLS, matching 0003. Fail-CLOSED: an unset app.tenant_id matches nothing.
GRANT SELECT, INSERT, UPDATE ON callcentre.call_summary_revision TO hbmp_app;

ALTER TABLE callcentre.call_summary_revision ENABLE ROW LEVEL SECURITY;
ALTER TABLE callcentre.call_summary_revision FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_call_summary_revision ON callcentre.call_summary_revision;
CREATE POLICY rls_call_summary_revision ON callcentre.call_summary_revision
    USING (tenant_id = current_setting('app.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));
