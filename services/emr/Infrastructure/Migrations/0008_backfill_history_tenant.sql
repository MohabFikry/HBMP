-- emr-service — 0011 backfill the appointment-history rows that carry a BLANK tenant (Phase 18.F3).
-- Additive + idempotent.
--
-- FOUND BY THE TENANT-ISOLATION FUZZER (tools/ci/check-tenant-isolation.py), not by the R2 audit.
--
-- 105 rows in emr.appointment_history had tenant_id = '' — written by the history trigger from appointments
-- that predate tenant stamping, and accepted because the column is NOT NULL but not non-blank.
--
-- Why that was a disclosure and not just untidy data: the RLS policy is
-- `tenant_id = current_setting('app.tenant_id', true)`, which is correctly fail-closed against a NULL but
-- NOT against ''. And UseHbmpRls bound the empty string for any request whose principal had no tenant claim.
-- So `'' = ''` matched, and an unauthenticated or claim-less caller could read those rows — from an
-- append-only CLINICAL history table. The fuzzer surfaced it as "105 rows visible with NO app.tenant_id
-- bound"; no hand-written test covered this table, which is exactly why the fuzzer enumerates from
-- information_schema instead of from a list someone maintains.
--
-- Two fixes, deliberately both:
--   * libs/data now binds a sentinel no row can equal when the tenant is blank, so the CLASS is closed for
--     every table including ones that acquire a blank-tenant row later.
--   * this migration repairs the DATA, recovering each history row's real tenant from its parent appointment
--     — which is authoritative, since the trigger's whole job was to copy it.

UPDATE emr.appointment_history h
SET tenant_id = a.tenant_id
FROM emr.appointment a
WHERE h.appointment_id = a.appointment_id
  AND (h.tenant_id IS NULL OR btrim(h.tenant_id) = '')
  AND btrim(a.tenant_id) <> '';

-- Any row whose parent appointment is ALSO blank (or gone) cannot have its tenant recovered. Rather than
-- guessing, park it on the sole tenant (ADR-0011 — the platform is single-tenant, so this is the only
-- possible answer today) and leave a trail. Guessing a tenant on a multi-tenant platform would be the
-- 18.B2 mistake; recording the assumption is what makes it reviewable when a second tenant is onboarded.
UPDATE emr.appointment_history
SET tenant_id = '11111111-1111-1111-1111-111111111111'
WHERE tenant_id IS NULL OR btrim(tenant_id) = '';

-- Stop it recurring at the source. A blank tenant is not a tenant; the column should have said so from the
-- start. NOT VALID would let existing bad rows survive — there are none left, so this validates immediately
-- and any future blank INSERT fails loudly at the point it is made instead of silently becoming readable.
ALTER TABLE emr.appointment_history
    DROP CONSTRAINT IF EXISTS ck_appointment_history_tenant_not_blank;  -- migrate-compat: contract-ok (idempotent drop-then-readd of a constraint this migration itself adds two lines below; nothing pre-existing is relaxed)
ALTER TABLE emr.appointment_history
    ADD CONSTRAINT ck_appointment_history_tenant_not_blank CHECK (btrim(tenant_id) <> '');

ALTER TABLE emr.appointment
    DROP CONSTRAINT IF EXISTS ck_appointment_tenant_not_blank;  -- migrate-compat: contract-ok (idempotent drop-then-readd of a constraint this migration itself adds two lines below; nothing pre-existing is relaxed)
ALTER TABLE emr.appointment
    ADD CONSTRAINT ck_appointment_tenant_not_blank CHECK (btrim(tenant_id) <> '');
