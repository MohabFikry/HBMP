-- orders-service — 0010 CONTRACT: order_type stops accepting 'Imaging'. DEFERRED.
--
-- ⚠ NOT applied by tools/ci/apply-migrations.sh — it globs Migrations/*.sql at maxdepth 1 and this file is a
-- level down. See services/identity/Infrastructure/Migrations/deferred/0033_radiology_role_contract.sql for
-- the full rationale and docs/runbooks/radiology-rename.md for the preconditions.
--
-- Apply with: tools/ci/apply-deferred-migrations.sh
--
-- Narrowing the CHECK is only safe once nothing can still WRITE the old value — i.e. after the switch deploy
-- has fully rolled out to every replica of orders-service, not merely started. A narrowed CHECK against a
-- replica still emitting 'Imaging' is a constraint violation on order placement, which surfaces as a 500 to a
-- doctor mid-encounter.

BEGIN;

-- Belt and braces: the backfill (0009) asserted this, but it ran on an earlier deploy and rows could have
-- been written since by a replica that had not yet switched.
UPDATE orders.investigation_order SET order_type = 'Radiology' WHERE order_type = 'Imaging';

ALTER TABLE orders.investigation_order DROP CONSTRAINT IF EXISTS ck_investigation_order_order_type;
ALTER TABLE orders.investigation_order
    ADD CONSTRAINT ck_investigation_order_order_type
    CHECK (order_type IN ('Lab','Radiology','Procedure'));

COMMIT;

-- ALSO REMOVE, in the same change:
--   * services/orders/Domain/Entities.cs — the OrderType.Imaging enum value and OrderTypes.Canonical's
--     collapse of it (TryParse keeps its fail-closed behaviour, it just stops knowing the old name)
--   * the `OrderType.Imaging or OrderType.Radiology` arms in Orders.cs, ExtendValidity.cs and
--     InvestigationChecks.cs — they become plain `OrderType.Radiology`
--   * apps/web/src/api/HttpApiClient.ts — the `rawType === "imaging"` read acceptance
--   * libs/contracts/src/investigations.ts — "Imaging" from zInvestigationOrderType
--
-- NOT the reporting consumer. services/reporting/Infrastructure/ProjectionMapping.cs must keep translating
-- 'Imaging' → Radiology FOREVER: it maps HISTORICAL events, and the projection is replayed from the event
-- log. Dropping it would silently split years of radiology volume across two dimension values.
