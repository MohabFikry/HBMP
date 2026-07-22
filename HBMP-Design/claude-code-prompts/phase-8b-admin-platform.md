# Phase 8b — Admin & Platform Management (Identity, Master-Data, Tenant/Provider Governance)

**Goal:** Give Org Admin, Super Admin, and Network Team the administrative surface the platform needs in production — user & role management with RBAC/ABAC policy administration and Segregation-of-Duties, master-data administration, notification-template and system configuration, tenant/provider administration, **break-glass** grant/monitoring, and **access-review** consoles — all immutably audited, with reads-of-admin also audited. Release **R5**.

Back to master list: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

> Root `CLAUDE.md` already defines stack, conventions, security, audit, testing, and Definition of Done. This file adds phase-8b scope only.

---

## Skills to activate
> Activate `healthcare-business-rules-engine`, `ngo-healthcare-operations` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management`. Skill files: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first

- [`../07-functional-requirements.md`](../07-functional-requirements.md) — **§11 Admin, Identity & Access (FR-IAM-001…010)** and §12 Master Data (FR-MDM-007/008) are authoritative; §10 Reporting (FR-RPT-004 export audit) for the review dashboards.
- [`../10-role-matrix.md`](../10-role-matrix.md) — §3.15 Org Admin (manages *who can access*, not the data), §3.16 Super Admin (`global` config; PHI only via break-glass), §7 assignment model, guardrails, and the **SoD conflict matrix**.
- [`../11-permission-matrix.md`](../11-permission-matrix.md) — the six hard rules and the policy-as-code bundle that admin edits must not break.
- [`../18-security-model.md`](../18-security-model.md) — **§3.3–3.5 MFA / conditional access / device & IP**, **§9 session & timeout policy**, **§11 break-glass** (request → dual-control → time-boxed → step-up → loud audit → no SoD bypass), §4 enforcement pipeline.
- [`../19-audit-strategy.md`](../19-audit-strategy.md) — immutable hash-chained audit, read-of-PHI/admin logging, and **§7 periodic access review**.
- Reference: [`../32-user-stories.md`](../32-user-stories.md) US-070 (SSO+MFA), US-071 (role-scoped access), **US-074 (manage users/roles & master data, SoD enforced)**; masterdata-service (phase 0b); notification-service (phase 8).

---

## THE INVARIANTS (read before writing any code)

1. **Admin manages access, not content.** Org Admin/Super Admin administer users, roles, policy, config, tenants — they are NOT routine readers of beneficiary PHI/financial content. Any such data access is **break-glass only** (time-boxed, dual-control, step-up, loudly audited).
2. **No privilege escalation / SoD enforced.** The policy engine evaluates SoD at **assignment time** (block an incompatible grant, e.g., Payment-Initiate + Payment-Release; Org Admin granting itself Super Admin) and at **decision time**. No admin can self-elevate.
3. **Every admin action is immutably audited — and so are admin reads.** Grants, revocations, policy/config/master-data changes, break-glass grants, and access-review decisions are hash-chained audit events; reading admin/audit consoles is itself audited (who saw the access matrix).
4. **Least privilege by default** — deny-by-default; no standing high privilege (JIT/PIM for T4/global); de-provisioning revokes access across all portals immediately.

---

## Prompts

### 8b.1 — User & role management, RBAC/ABAC policy admin, SoD, session & device policy, access-review console

```text
Build the admin surface in identity-service (and an admin-service facade if cleaner). .NET 8, REST /api/v1 + OpenAPI 3.1. Org Admin (tenant-scoped) and Super Admin (global) audiences.

READ FIRST: ../07-functional-requirements.md FR-IAM-002/004/005/006/007/010, ../10-role-matrix.md §7 (assignment + SoD matrix), ../18-security-model.md §3.3–3.5 and §9, ../19-audit-strategy.md §7.

Capabilities:
- User & role management: list/assign/revoke role bindings (Keycloak group membership -> app-role claim). Org Admin within tenant:own; Super Admin cross-tenant. Assignment requires a justification; de-provisioning (FR-IAM-010) immediately revokes across ALL portals (token/session revocation + Keycloak session end).
- RBAC/ABAC policy administration: view and stage changes to the OPA/Cerbos policy bundle (versioned); changes go through the audited CI path (peer-review by Security + DPO) — the admin UI proposes/diffs, it does NOT hot-patch live policy. Deploying a bundle is an audited event.
- Segregation-of-Duties checks: enforce the ../10 §7 conflict matrix at ASSIGNMENT time — reject an incompatible grant (e.g., Payment-Initiate + Payment-Release; Doctor-who-authored + Approver-of-that-case; Provider Admin self-granting clinical; Org Admin -> Super Admin) with a clear conflict reason; surface SoD violations to reviewers as high-severity.
- Device management & IP allow-lists: manage per-role/per-tenant device-compliance requirements and IP allow-lists that feed Conditional Access (../18 §3.4–3.5) for admin/Finance/break-glass paths.
- Session policy: configure access-token lifetime, idle timeout, absolute cap, concurrent-session limits, and step-up triggers per role tier (../18 §9).
- Access-review console (periodic recertification, FR-IAM-007 / ../19 §7): generate quarterly review campaigns for T3/T4-reading roles; a reviewer confirms or revokes each grant (need-to-know); stale/unconfirmed grants auto-expire; every review decision is audited and linked to the grant.

Guardrails: deny-by-default; JIT/PIM for T4/global (no standing global grant); admin actions AND admin reads are immutable hash-chained audit_events; assigning a clinical role does NOT grant the admin clinical read.

Acceptance criteria (US-074, US-071, FR-IAM-005/007/010):
- Given an incompatible pair, When an admin tries to grant both to one user, Then the grant is rejected with the SoD reason and audited.
- Given a user is de-provisioned, When they call any portal/API, Then access is denied everywhere immediately.
- Given a quarterly access review, When a reviewer does not recertify a grant, Then it auto-expires and the decision is audited.
- Given an admin views the access matrix, Then that read is itself audited.

Tests: SoD unit tests for every conflict pair in ../10 §7; integration (assign/revoke/de-provision + session revocation); access-review lifecycle test (campaign -> recertify/revoke -> auto-expire); audit test (grant/revoke/review and admin reads all hash-chained). NO privilege-escalation path (negative test: Org Admin cannot grant Super Admin).
```

### 8b.2 — Master-data administration UI/API, notification templates, system configuration

```text
Add master-data and configuration administration over masterdata-service (phase 0b) and notification-service (phase 8). .NET 8, REST /api/v1. Restricted to authorized governance roles (FR-MDM-008).

READ FIRST: ../07-functional-requirements.md §12 (FR-MDM-007/008/009) and FR-NOT-005, ../19-audit-strategy.md (change audit).

Capabilities:
- Master-data administration: manage ICD-10, CPT, LOINC, Drug master (with ATC), drug-drug interactions, and allergens held by masterdata-service. Support effective-dated / versioned updates (FR-MDM-007) that never break historical records (append a new version, don't mutate the old). Formulary + substitution-rule management (FR-MDM-009) for pharmacy. All edits restricted to clinical-governance/Super Admin roles (FR-MDM-008) and audited.
- Notification templates: manage bilingual AR/EN templates for the phase-8 events (appointment, approval decision, result ready, prescription ready), with data-minimization guardrails (no diagnoses/clinical detail in SMS/email bodies) enforced by a template linter; quiet-hours/rate-limit config (FR-NOT-005).
- System configuration: manage tenant-level and platform-level config (feature flags, thresholds like high-cost approval trigger, reminder lead-times) with typed, validated settings and effective-dating.

Guardrails: every master-data / template / config change is an immutable hash-chained audit_event (actor, before/after, effective-date, justification); changes are effective-dated so historical orders/prescriptions still resolve to the version in force at their time; SoD — the editor of a clinical code cannot be its sole approver where governance requires dual-control; template linter blocks PHI in outbound channels.

Acceptance criteria (US-074, FR-MDM-007/008):
- Given a governance admin updates an ICD-10 or Drug entry, Then a new effective-dated version is created, historical records still resolve correctly, and the change is audited.
- Given a non-authorized role attempts a master-data edit, Then it is denied (FR-MDM-008) and audited.
- Given an AR/EN template containing a diagnosis field bound to an SMS body, When it is saved, Then the linter rejects it (data minimization).

Tests: effective-dating test (old records resolve to old version); authz test (only governance/Super Admin may edit); template-linter test (PHI-in-SMS rejected, AR+EN parity required); audit test (before/after captured).
```

### 8b.3 — Tenant & provider administration, break-glass administration, audit & access-review dashboards

```text
Build the platform-governance surface for Super Admin (global) and Network Team (provider metadata), plus break-glass administration and the monitoring dashboards. .NET 8, REST /api/v1.

READ FIRST: ../07-functional-requirements.md FR-IAM-008/009, ../18-security-model.md §8 (tenant/provider isolation) and §11 (break-glass), ../19-audit-strategy.md §7.

Capabilities:
- Tenant administration (FR-IAM-008): Super Admin manages tenants (Mersal = tenant 0; future orgs/donors as tenants) and platform-wide config; every tenant carries tenant_id and NO cross-tenant data leakage is possible (RLS-enforced). Provider administration links to phase-2b provider-service (Network Team scope) — this prompt adds the platform-admin oversight view, not a second provider store.
- Break-glass administration (FR-IAM-009 / ../18 §11): implement the full flow —
  1. Request: user invokes break-glass with a mandatory reason code + free-text justification, targeting specific resource(s).
  2. Dual control: a second authorized approver (!= requester; SoD-enforced) must approve.
  3. Time-boxed + scoped: grant limited to the named resource(s) and a short auto-expiring window.
  4. Step-up + hardware/step-up MFA required to activate.
  5. Loud audit: every access under an active grant emits a high-severity break_glass.* audit event; near-real-time alert to Security + DPO; mandatory post-hoc review.
  6. No SoD bypass: break-glass never enables self-approval nor disables field-level denies beyond its explicit scope.
- Audit & access-review dashboards: read-only consoles over the append-only audit store — break-glass grants + their reviews, SoD violations, admin action feed, and the periodic access-review status (../19 §7). Multi-tenant scoping: a tenant admin sees only their tenant; Super Admin sees the global roll-up. Viewing these dashboards is itself audited.

Guardrails: break-glass grants and every access under them are immutably audited and reviewed; dual-control + step-up mandatory; time-box auto-expires; no privilege escalation and no field-deny bypass beyond scope; tenant/provider isolation holds on every dashboard query (RLS).

Acceptance criteria (FR-IAM-008/009; ../18 §11):
- Given a break-glass request, When the requester tries to self-approve, Then it is rejected (dual-control/SoD) and audited.
- Given an approved break-glass grant, When the window expires, Then access auto-revokes and any further access is denied; every access during the window emitted a high-severity break_glass.* event and alerted Security/DPO.
- Given a tenant admin opens the audit/access-review dashboard, Then they see only their tenant's events, and their view is audited.
- Given break-glass is active, When the user attempts an action outside the granted scope or a self-approval, Then it is still denied (no SoD/field-deny bypass).

Tests: break-glass lifecycle test (request -> dual-approve -> step-up -> scoped access -> auto-expire); negative tests (self-approve denied, out-of-scope access denied, field-deny not bypassed); tenant-isolation test on dashboards; audit test (break_glass.* high-severity events + alerts + dashboard-read auditing).
```

---

## Guardrails

- **Admin manages access, not content** — Org/Super Admin are not routine PHI/financial readers; any such access is break-glass only (dual-control, time-boxed, step-up, loud audit, no SoD/field-deny bypass).
- **SoD enforced at assignment AND decision time** — every conflict pair in ../10 §7 blocked; no self-elevation; Org Admin cannot grant Super Admin.
- **Least privilege / deny-by-default** — JIT/PIM for T4/global, no standing high privilege; de-provisioning revokes across all portals immediately.
- **Immutable hash-chained audit on admin writes AND reads** — grants, revocations, policy/config/master-data changes, break-glass, access-review decisions, and console views are all recorded (../19).
- **Effective-dated master data** — versioned, never mutate historical; historical orders/prescriptions resolve to the version in force at their time.
- **Policy-as-code stays reviewed** — the admin UI proposes/diffs bundles; it does not hot-patch live ABAC; deployment goes through the audited CI path (Security + DPO review).
- **Tenant/provider isolation** on every admin/dashboard query (RLS `tenant_id` / `provider_id`); no cross-tenant leakage.
- **Data minimization in notifications** — template linter blocks PHI in SMS/email; AR/EN parity required.

## Done when

- Org Admin and Super Admin can **manage users and roles** with SoD blocking every incompatible grant, no privilege-escalation path exists, de-provisioning revokes access everywhere immediately, session/device/IP policy is configurable, and a **periodic access-review** campaign recertifies or auto-expires T3/T4 grants — all audited (FR-IAM-005/006/007/010; US-074).
- Governance admins **manage master data** (ICD/CPT/LOINC/Drug/ATC/interactions/allergens/formulary), **notification templates** (AR/EN, PHI-safe), and **system configuration** with effective-dating and full audit; unauthorized edits are denied (FR-MDM-007/008/009).
- **Tenants and providers** are administrable with proven isolation; **break-glass** is grantable with dual-control + step-up + time-box, every access under it is loudly audited and alerted, it cannot be self-approved or exceed scope, and **audit & access-review dashboards** show grants/reviews/SoD violations tenant-scoped and audit their own reads (FR-IAM-008/009; ../18 §11).
- SoD, no-escalation, access-review lifecycle, break-glass lifecycle, effective-dating, template-linter, and tenant-isolation tests green; OpenAPI + READMEs updated. Global Definition of Done (root `CLAUDE.md`) met.
