-- approvals-service — Phase 7.3 break-glass. Emergency approval, director override, and manual authorization are
-- the specially-audited exceptions (19-audit-strategy): each writes a break_glass decision row (justification is
-- mandatory — already CHECK-constrained in 0001) and flags the authorization for RETROSPECTIVE REVIEW so it lands
-- in a post-hoc review queue (23-state-machines §5 "retrospective review required").

ALTER TABLE approvals.authorization
    ADD COLUMN IF NOT EXISTS retrospective_review_required boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS retrospective_reviewed        boolean NOT NULL DEFAULT false;

-- The post-hoc break-glass review queue: outstanding retrospective reviews, newest first.
CREATE INDEX IF NOT EXISTS ix_auth_retrospective ON approvals.authorization (decided_at)
    WHERE retrospective_review_required AND NOT retrospective_reviewed;
