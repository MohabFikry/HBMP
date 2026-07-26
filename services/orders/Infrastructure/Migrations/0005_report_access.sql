-- orders-service — 0005 sensitive-result release requests + grants (phase 14.7, design 37 §6). ADDITIVE.
-- The justified release workflow: a request routes to the authoring/ordering doctor (or a Medical Director);
-- an approval mints a TIME-BOXED, SINGLE-RESULT, NON-TRANSFERABLE grant. Every read under a grant is audited
-- separately (SensitiveResultReadUnderGrant). Requests/grants are append-mostly (revoke/expire stamp
-- metadata; nothing is hard-deleted).

CREATE TABLE IF NOT EXISTS orders.report_access_request (
    request_id        uuid PRIMARY KEY,
    order_id          uuid NOT NULL,
    order_line_id     uuid NOT NULL,
    beneficiary_id    uuid NOT NULL,
    requested_by      text NOT NULL,
    requested_for_role text,
    purpose_code      varchar(24) NOT NULL CHECK (purpose_code IN ('ContinuityOfCare','AuthorizationDecision','ClinicalReview','Complaint','Legal','Other')),
    justification     text NOT NULL,
    requested_ttl_hours integer NOT NULL DEFAULT 0,
    status            varchar(16) NOT NULL DEFAULT 'Requested' CHECK (status IN ('Requested','UnderReview','InfoRequested','Approved','Denied','Expired','Revoked')),
    decided_by        text,
    decided_by_role   text,
    decided_at        timestamptz,
    decision_reason   text,
    created_at        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_rar_line   ON orders.report_access_request (order_line_id);
CREATE INDEX IF NOT EXISTS ix_rar_status ON orders.report_access_request (status);

CREATE TABLE IF NOT EXISTS orders.report_access_grant (
    grant_id        uuid PRIMARY KEY,
    request_id      uuid NOT NULL REFERENCES orders.report_access_request (request_id),
    grantee_user_id text NOT NULL,
    order_line_id   uuid NOT NULL,
    purpose_code    varchar(24) NOT NULL,
    granted_at      timestamptz NOT NULL DEFAULT now(),
    expires_at      timestamptz NOT NULL,
    revoked_at      timestamptz,
    revoked_by      text
);
-- Fast active-grant lookup (grantee + result) while the grant is live.
CREATE INDEX IF NOT EXISTS ix_rag_active ON orders.report_access_grant (grantee_user_id, order_line_id) WHERE revoked_at IS NULL;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE ON orders.report_access_request, orders.report_access_grant TO hbmp_app;
    END IF;
END $$;
