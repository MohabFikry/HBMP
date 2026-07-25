# admin-service

Identity & access administration (Release R5, Phase 8b.1 — US-074 / FR-IAM-002/005/006/007/010). Owns the `admin`
schema. Org Admin (`tenant:own`) and Super Admin (`global`) administer **who can access** the platform — role
bindings, session/device/IP policy, access-review campaigns, and staged ABAC policy proposals. **Admin manages
access, not content:** these audiences are never routine readers of beneficiary PHI/financial data (that is
break-glass only, phase 8b.3). Every admin action **and** every admin read is immutably audited.

> Phase 8b.1 complete: SoD-checked role assignment, de-provisioning that revokes access everywhere immediately,
> effective-dated session/device policy, quarterly access-review campaigns with auto-expiry, and policy-bundle
> proposals (propose/diff only — never a live hot-patch).

## Segregation of Duties (the crux)

The SoD conflict matrix (`10-role-matrix §7`) lives in `libs/authz/SegregationOfDuties.cs` so it is reusable and
evaluated at **assignment time** here (and re-checked at **decision time** by the deciding services). Every named
incompatible pair is expanded to concrete role/capability tokens and blocked when a user would hold both:

- Doctor ↔ Medical Approval / Medical Director (self-approval of own request)
- Finance Payment-Initiate ↔ Payment-Release (single-actor fraud)
- Beneficiary create/merge ↔ merge-approver
- **Org Admin ↔ Super Admin (no privilege escalation)** · Provider Admin ↔ any clinical role (no PHI self-elevation)
- Network Team ↔ Payment-Release · claims submitter ↔ claims officer/reviewer · claims officer ↔ reviewer (dual
  control) · claims adjudication / settlement-issuer ↔ finance payment halves · any provider-affiliated role ↔
  claims officer/reviewer.

A coarse `finance` grant is treated as covering **both** payment halves, so a coarse+coarse assignment is still
caught. A grant that would breach SoD is rejected `409` with the conflict reason and audited **high-severity**.

## Capabilities & APIs (`/api/v1/admin`)

- **Role bindings** — `POST /role-bindings` (grant, SoD-checked, justification-required), `POST
  /role-bindings/revoke`, `POST /users/deprovision` (FR-IAM-010: revokes **every** active binding and blocks the
  subject so any portal/API denies immediately). Bindings are soft-lifecycle (revoke stamps metadata, never
  deletes).
- **Access matrix** — `GET /access-matrix` (audited admin read: who saw it), `GET /users/{id}/effective-roles`
  (the seam other services consult — empty ⇒ de-provisioned/no grant), `GET /sod-matrix` (the expanded conflict
  reference).
- **Access review** (`/access-reviews`) — `POST /` opens a campaign and snapshots active **T3/T4** grants as
  Pending items; `POST /items/{id}/recertify` and `/revoke`; `POST /{id}/sweep` auto-expires items unconfirmed past
  the deadline (revoking the binding) and closes the campaign. Every decision is audited and linked to the grant.
- **Session / device policy** — `PUT /session-policy` (token TTL, idle + absolute cap, concurrent limit, step-up
  per role tier — `18-security-model §9`), `PUT /device-policy` (managed-device requirement + IP allow-list —
  §3.4–3.5). Both are **effective-dated** (append a new row, never rewrite history).
- **Policy proposals** — `POST /policy-proposals` (Super Admin only) **stages** a versioned bundle diff with a
  rationale; it never hot-patches live ABAC — deployment goes through the audited CI path (Security + DPO review).

## Authorization (`libs/authz/AdminPolicies`, v8b.1)

Org Admin + Super Admin only; every action is `Sensitive` → the allow is audited. Org Admin is pinned to its own
tenant (TenantMatch); the gate leaves the resource tenant null for a Super-Admin caller so it acts globally without
widening Org Admin. `admin:propose-policy` is Super-Admin only.

## Domain & data

- `role_binding` (soft-lifecycle, one active grant per tenant/subject/role) · `deprovisioned_user` ·
  `access_review_campaign` / `access_review_item` · `session_policy` · `device_policy` (effective-dated) ·
  `policy_proposal` (global, staged). No `DELETE` grant anywhere (auditable history); tenant-scoped tables are RLS
  isolated on `tenant_id` (enforced under the `hbmp_app` NOBYPASSRLS role).
- `Infrastructure/Migrations/0001_admin.sql` — schema, tables, indexes, RLS, app-role grants. Applied to host PG
  (:55432).

## Tests

- `SegregationOfDutiesTests` (pure) — **every** §7 conflict pair is blocked (theory), no Org-Admin→Super-Admin
  escalation, coarse-finance covers both halves, clean grants pass, a pre-existing conflict isn't re-flagged.
- `RoleAssignmentTests` (pure) — unknown/duplicate rejected, SoD reason surfaced, T3/T4 review deadlines.
- `AdminAuthzTests` (pure, real engine) — Org/Super Admin allowed, Org Admin denied cross-tenant, Super Admin
  global, non-admin default-denied, only Super Admin proposes policy, access-matrix read is an audited allow.
- `AdminIntegrationTests` (env-gated `ADMIN_TEST_DB`, live PG) — SoD-incompatible grant rejected + audited
  high-severity; de-provisioned user has **no** effective roles anywhere; an unconfirmed T3 grant **auto-expires**
  at the review deadline (binding revoked) while a recertified one is kept; low-tier grants stay out of a T3
  campaign.

Serialized via the `admin-db` collection. Total: 40 admin tests; full solution 516 green.
