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

## Programme enablement (phase 21.4/21.6, design 40 §4)

The **third, orthogonal gate**: checked *after* authorization and *before* execution, so a fully authorized
principal can still be refused. Per adaptation **A4** this is not commercial entitlement — Mersal is a
charity, its tenants are partner NGOs and clinics, and the switches say "this organisation has been
onboarded onto the claims programme". The refusal's remedy is "contact Mersal programme administration",
never "upgrade"; `program-not-enabled` and `program-limit-reached` are deliberately DISTINCT problem types
from a permission denial, because the three send people to three different places.

- `GET /programs/{tenantId}` — every known feature and cap, present or not. Absent rows return as
  disabled/unconfigured and unlimited rather than being omitted: a screen listing only configured keys
  cannot configure the others, and "off" must be distinguishable from "never set up".
- `PUT /programs/{tenantId}/features/{key}` and `PUT /programs/{tenantId}/limits/{key}` — **platform
  administration only** (`super_admin`; `AdminGate` alone would let an Org Admin holding
  `admin:manage-tenant` enable programmes for their own tenant, which is not a gate at all). Reason
  mandatory, history row + audit event on every change.
- **Caps are counted live, never stored.** `TenantProgramStore.CheckLimitAsync` recounts inside the
  mutating transaction under a per-(tenant, limit) advisory lock; the screen's `currentUsage` is advisory.
  A `null` usage means *this service does not own that count* (extracts and storage belong to reporting- and
  document-service) — it is not zero, and must never render as zero.
- **Enablement never grants.** A switched-on module still requires the permission; a switched-off one hides
  nothing retroactively from audit, and a cap set below current usage removes nothing — it refuses the next
  addition only.

> **Outstanding (carried out of phase 21):** no service calls the gate yet. The mechanism and its admin
> surface are complete and tested, but `ProgramEnablement` has no production call site, so nothing currently
> returns `program-not-enabled`. Wiring the check into the feature-owning services is open work.

## Governance — master data / templates / config (phase 8b.2)

All governance edits are **effective-dated**: a change appends a new version and closes the prior version's window,
so a historical order/prescription resolves the version in force at **its** time (FR-MDM-007) — history is never
mutated.

- **Master data** (`POST /master-data`, `GET /master-data/{system}/{code}/as-of?at=`) — versioned edits to
  ICD/CPT/LOINC/ATC/Drug/interactions/allergens/formulary, held as a JSON attribute snapshot per version.
  Restricted to **clinical governance (Medical Director) + Super Admin** (FR-MDM-008); Org Admin is denied. The
  as-of resolver returns the version in force at a date (null if not yet existing or retired then).
- **Notification templates** (`POST /templates`) — bilingual AR/EN versions, **linted before save**
  (`TemplateLinter`): a template bound to an outbound channel (SMS/email/WhatsApp) with a clinical/PHI token in its
  subject or body is **rejected** (data minimization), and AR/EN parity is required (no English-only outbound).
  In-app templates may carry a clinical token but still need parity.
- **System configuration** (`PUT /system-config`) — typed (`Text/Whole/Number/Boolean/Duration`), validated, and
  effective-dated; tenant-scoped or platform-level (`tenant_id = "*"`). A malformed value is rejected before store.

## Tenant / provider governance, break-glass & dashboards (phase 8b.3)

- **Tenant administration** (`PUT/GET /tenants`) — Super Admin manages platform tenants (Mersal = tenant 0; future
  orgs/donors); every domain row carries `tenant_id` and RLS prevents cross-tenant leakage (FR-IAM-008). Provider
  metadata stays owned by phase-2b provider-service; this is the platform-admin oversight view, not a second store.
- **Break-glass** (`/break-glass`, FR-IAM-009 / `18-security-model §11`) — the full flow: **request** (mandatory
  reason code + justification + scoped resource types/ids) → **dual-control approve** (approver ≠ requester —
  a self-approval is rejected `409` and audited) → **step-up MFA activate** (requester + `stepUpSatisfied`, else
  denied) → **scoped, auto-expiring window** → **access** (in-scope `200`; out-of-scope `403` — **no field-deny
  bypass beyond scope**) → **sweep auto-expiry**. Every access emits a **HIGH-severity** `break_glass` audit event
  (loud audit + Security/DPO alert seam) and is surfaced for mandatory post-hoc review. An active grant maps to the
  runtime `libs/authz/BreakGlassGrant` a downstream engine consults (live cross-service wiring deferred to the bus).
- **Governance dashboards** (`/dashboards/break-glass|access-review|sod-violations`) — read-only, **tenant-scoped**
  (a tenant admin sees only their tenant; Super Admin passes the tenant it inspects), and **viewing is itself
  audited** (`19-audit-strategy §7`). The SoD dashboard re-evaluates a tenant's active bindings against the §7
  matrix to surface any latent conflict (defense in depth).

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

- `GovernanceUnitTests` (pure) — the template linter blocks PHI-in-SMS and requires AR/EN parity (in-app exempt
  from PHI rule, not parity); config values are typed-validated; effective-dating picks the version in force at a
  date.
- `GovernanceIntegrationTests` (env-gated) — a master-data edit **appends** a version and a historical date resolves
  the **old** one (FR-MDM-007); a PHI-in-SMS template save is rejected + audited; a config change is typed +
  versioned (one in-force row).

- `BreakGlassPolicyTests` (pure) — dual control (no self-approval), scope check (out-of-scope + empty-scope
  fail-closed, id-scoping), bounded window, live-only-while-active.
- `BreakGlassIntegrationTests` (env-gated) — full lifecycle (request → dual-approve → step-up → in-scope access →
  auto-expire); self-approval rejected; out-of-scope access denied; every access is a HIGH-severity break_glass
  event; dashboards are tenant-scoped and audit their own reads; the SoD dashboard surfaces a latent conflict.

Serialized via the `admin-db` collection. Total: 67 admin tests; full solution 543 green.
