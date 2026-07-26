# 11. Runtime as `hbmp_app` (NOBYPASSRLS) + platform-wide tenant RLS

Date: 2026-07-26
Status: Accepted
Phase: 16.4 (Audit remediation — H1)

## Context

The audit (H1) found RLS was inert: every runtime connection used the superuser role `hbmp` (which
bypasses RLS), the GUC-binding interceptor existed only in provider-service, and — critically — the PHI
schemas had **no tenant column at all**. patient/emr/document and most tables were single-tenant by
omission; row isolation for PHI lived only in the ABAC layer (provider-ownership, treating-relationship,
branch scope). "Defense in depth at the datastore" was claimed but not present.

The chosen remediation (over a hybrid that would only RLS the columns that already existed) was the **full
multi-tenant retrofit**: give every domain table a real `tenant_id` and enforce isolation at the datastore.

## Decision

1. **Runtime role split.** All 13 tenant-scoped services connect as `hbmp_app` — `LOGIN NOSUPERUSER
   NOBYPASSRLS` — in every runtime connection string (compose + appsettings). The owner/superuser `hbmp` is
   used **only** by the migration path (applied out-of-band as the schema owner). A superuser silently
   ignores RLS, so the runtime role must be non-superuser for the policies to bite.
2. **`tenant_id` on every domain table.** `text NOT NULL DEFAULT` the sole Mersal tenant
   (`11111111-…-111111111111`), added additively (`ADD COLUMN IF NOT EXISTS`) so existing rows backfill and
   any raw insert stays valid. ~45 tables across 12 schemas gained the column; history-twin triggers
   (patient/policy/emr) were updated to carry the tenant onto the append-only snapshot.
3. **`ENABLE` + `FORCE ROW LEVEL SECURITY`** with one policy per table:
   `USING (tenant_id = current_setting('app.tenant_id', true))`. FORCE so even the table owner is subject;
   the policy's insert check (USING, absent a separate WITH CHECK) rejects a row whose tenant ≠ the GUC.
4. **Shared binder in `libs/data`** (lifted from provider-service, which now consumes it):
   - `RlsConnectionInterceptor` sets `app.tenant_id` / `app.provider_id` GUCs on each pooled connection open
     via `set_config` (parameterized, never interpolation).
   - `TenantStampingInterceptor` (a `SaveChangesInterceptor`, metadata-driven) stamps `tenant_id` from the
     request's `RlsContext` onto every inserted entity mapping a `TenantId` column — so no create path can
     forget it and the inserted value always equals the GUC. No marker interface ⇒ Domain projects stay
     dependency-light.
   - `UseHbmpRls()` middleware binds `RlsContext` from the authenticated principal after `UseAuthentication`.
5. **Background consumers bind the GUC themselves.** eligibility-service's `EventConsumer` has no HTTP
   principal, so it sets `RlsContext.TenantId` in each per-message scope (single tenant ⇒ the sole tenant),
   else its FORCE-RLS projection writes would be denied.
6. **Infra tables stay RLS-free:** `outbox_message`, `processed_request`, `processed_event`, `*_seq` — they
   are drained/written by relays and consumers outside a tenant context, and carry no PHI. The append-only
   `approvals.authorization_decision` keeps its `REVOKE UPDATE, DELETE FROM hbmp_app` (re-applied after the
   blanket grant) so RLS never weakened the insert-only ledger.

## Fail-closed behaviour

No principal ⇒ empty tenant GUC ⇒ **zero rows** and rejected writes. This is the default-deny the audit
requires. Proven per service by an env-gated `RlsIsolationTests` in the provider style: under tenant A's GUC
the app role sees only A, under B only B, with no GUC nothing; migrations still apply as the owner.

## Consequences

- Tenant isolation is now an **independent datastore guarantee**, not just an application predicate — a bug
  in an ABAC filter can no longer leak another tenant's rows.
- The tenant is currently sourced from the column DEFAULT / a hardcoded constant in the single background
  consumer, because the deployment is single-tenant. When a second tenant is onboarded, the DEFAULT is
  dropped and inserts/consumers source the tenant live from the principal / event claim — the stamping
  interceptor already reads it from `RlsContext`, so that change is localized.
- Existing DB-integration tests connect as the superuser `*_TEST_DB` and therefore bypass RLS (unchanged,
  green); the new isolation tests use a **separate `hbmp_app` connection** so they exercise the real policy.
- Running containers must be rebuilt to pick up the interceptors + the `hbmp_app` connection strings.
