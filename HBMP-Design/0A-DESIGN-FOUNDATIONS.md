# 0A — Design Foundations
### Shared vocabulary, technology stack, brand system & conventions

> This document is the **single source of shared truth** for the whole design set. Every other deliverable inherits its terms, service names, status taxonomy, and palette from here. If a decision changes, change it here first.

---

## 1. Platform positioning

Per the sponsor's guidance, the platform is designed as a **Healthcare Benefit Management Platform (HBMP)** — a service-oriented benefit administration core — rather than a single-clinic management system. The clinical/EMR workflows sit *on top of* reusable benefit-management services so that Mersal can later add claims, capitation, inventory, PBM, and third-party integrations (UNHCR, government, insurers) without re-platforming.

**Reusable core domains (the "spine"):** Beneficiaries · Eligibility · Coverage/Policy · Provider Network · Authorizations/Approvals · Orders · Prescriptions.
**Clinical & operational domains (on the spine):** EMR/Clinical · Appointments · Lab & Imaging · Pharmacy · Notifications · Reporting · Documents · Audit.

---

## 2. Glossary & canonical terms

| Term | Definition |
|------|------------|
| **Beneficiary** | A refugee/eligible individual receiving care. Canonical subject of the record. (Never "patient" in data model; "patient" is a UI-facing synonym in clinical contexts.) |
| **Member** | A beneficiary in their benefit/coverage capacity (has a Policy, limits, member number). |
| **Policy** | The benefit contract/plan attached to a member: coverage rules, limits, validity window. |
| **Eligibility** | The real-time answer to "can this person receive this service now?" Derived from Policy + Status + limits. |
| **Coverage** | What services/categories a policy pays for and up to what limit. |
| **Provider** | Contracted external entity: Clinic, Doctor, Laboratory, Imaging Center, Pharmacy. |
| **Order** | A clinician-created request for a service (investigation, radiology). Has a lifecycle & is "consumed" by a provider. |
| **Prescription** | A clinician-created medication order, dispensed (fully/partially) by a pharmacy. |
| **Authorization / Approval** | A pre-service decision permitting a high-cost/controlled service. |
| **Encounter / Visit** | A single interaction (walk-in, scheduled, referral, follow-up) generating clinical + benefit activity. |
| **EMR** | Electronic Medical Record — the longitudinal clinical record (SOAP notes, diagnoses, vitals, meds, allergies). |
| **TAT** | Turnaround Time (e.g., approval TAT). |
| **PBM** | Pharmacy Benefit Management — formulary, drug rules, interactions, generic substitution. |
| **TPA** | Third-Party Administrator — the benefit-administration role Mersal effectively plays. |
| **Tenant** | An isolated organizational boundary (Mersal is tenant 0; future orgs/donors can be additional tenants). |
| **Consume (an order)** | The atomic act of a provider claiming a line of an order so it cannot be reused. |

**Minimum-necessary principle:** every screen and API response exposes only the data the role needs for the task. This is a first-class design constraint, not an afterthought — see [11-permission-matrix.md](11-permission-matrix.md) and [18-security-model.md](18-security-model.md).

---

## 3. Identifier & naming conventions

- **Beneficiary identity** can be established by any of: National ID, Passport, Refugee ID, UNHCR Number, Organization Member Number. Internally, every beneficiary has one immutable surrogate `beneficiary_id` (UUID v7) and 0..n `beneficiary_identifier` rows (type + value + issuing authority + verification status).
- **Human-readable business keys** are prefixed & checksummed:
  - Member No: `MRS-M-<10 digits>`
  - Encounter: `ENC-<yyyymmdd>-<seq>`
  - Investigation Order: `ORD-<yyyy>-<base32(8)>`
  - Prescription: `RX-<yyyy>-<base32(8)>`
  - Authorization: `AUTH-<yyyy>-<base32(8)>`
  - Referral: `REF-<yyyy>-<base32(8)>`
- **Services** are named `<domain>-service` (e.g., `patient-service`, `orders-service`). **Databases** are `<domain>_db`. **Events** are `<Domain><PastTenseVerb>` (e.g., `OrderConsumed`, `PrescriptionDispensed`).
- **API resources** are plural nouns, kebab where multi-word: `/beneficiaries`, `/investigation-orders`, `/authorizations`.
- **All timestamps** are stored UTC (`timestamptz`), displayed in `Africa/Cairo` by default; user-selectable.

---

## 4. Technology stack (authoritative — open-source, on-prem-first, cloud-ready)

**Zero software-licensing cost.** Mersal is a charity, so the stack is fully **open-source and self-hostable** — it runs on a single on-prem server (or cheap VPS) at $0 licensing cost, and stays **cloud-ready** (containers + Kubernetes + open standards) so it can lift-and-shift to any cloud later without code changes. Full rationale, the Azure→OSS mapping, security parity, and the deployment tiers are in **[0C-OPEN-SOURCE-STACK.md](0C-OPEN-SOURCE-STACK.md)** (authoritative). The application layer below is unchanged; only the infrastructure substrate is open-source.

| Layer | Choice | Rationale |
|-------|--------|-----------|
| Frontend | **React + TypeScript**, Vite, React Router; **Design system**: Radix UI primitives + custom Mersal theme; i18n via `i18next` (Arabic RTL + English LTR) | Accessibility-friendly primitives, strong RTL support, role-portal code-splitting |
| Mobile (future) | React Native (shared design tokens) | Reuse tokens & domain SDK |
| API style | **REST**, versioned (`/api/v1`), **OpenAPI 3.1**, **FHIR R4**-aligned resources where practical | Requirement; interoperability |
| Backend | **.NET 8 (C#)** primary for domain services; Node/TypeScript acceptable for BFF/notification edge | MIT-licensed, free, runs on Linux; strong typing; healthcare ecosystem maturity |
| Orchestration/Runtime | **k3s** (lightweight Kubernetes) on-prem; **Docker Compose** for single-server; **Helm** charts; portable to any managed K8s | Cloud-ready, same artifacts on-prem & cloud |
| API Gateway | **Kong Gateway (OSS)** (or APISIX/Traefik) | Versioning, throttling, JWT validation, OpenAPI |
| Ingress + WAF + TLS | **Traefik/NGINX Ingress** + **ModSecurity (OWASP CRS)** + **Let's Encrypt/internal CA** | Edge protection, free TLS |
| AuthN/Identity | **Keycloak** as IdP; OIDC/OAuth2; MFA (TOTP/WebAuthn); per-role clients | Self-hosted MFA, RBAC, LDAP federation, brute-force protection |
| AuthZ | **RBAC + ABAC**; **OPA/Cerbos** (or OpenFGA) policy engine for fine-grained/attribute rules | Need-to-know, provider isolation |
| Datastore (OLTP) | **PostgreSQL** (self-hosted), schema-per-service; HA via **Patroni** | Relational integrity, row-level security; at-rest via LUKS + pgcrypto |
| DB backup/PITR | **pgBackRest** (full + WAL) | RPO ≤ 15 min, tested restores |
| Search | **OpenSearch** (or Meilisearch/Typesense for light footprint) | Beneficiary & order lookup, typo tolerance |
| Cache | **Valkey** (BSD fork of Redis) | Eligibility snapshots, session, rate limits |
| Message/Event bus | **RabbitMQ** (commands/queues) + **NATS JetStream** or **Redpanda** (domain events) | Reliable workflows, async fan-out; transactional outbox |
| Object storage | **MinIO** (S3-compatible, SSE-encrypted, object-lock/WORM) | Documents, lab/imaging reports, audit archive |
| Documents/DMS | MinIO + metadata service, virus scan on ingest (**ClamAV**), OCR-ready | Uploads, certificates, results |
| Observability | **OpenTelemetry** + **Prometheus** + **Grafana** + **Loki** (logs) + **Tempo/Jaeger** (traces) | Tracing, audit correlation |
| Secrets/Keys | **OpenBao** (or HashiCorp Vault) — transit engine as KMS (AES-256, rotation); **SOPS** for GitOps | No secrets in code/images |
| Service mesh/mTLS | **Linkerd** | Automatic mTLS service-to-service |
| CI/CD + registry | **GitLab CE** (repo+CI+registry+scanning) or **Gitea + Woodpecker** + **Harbor**; **Trivy** scans; IaC via **OpenTofu + Ansible + Helm** | Self-hosted GitOps, repeatable envs |
| DR / backup | **pgBackRest** + **Velero** (cluster) + **restic** (files/MinIO), offsite copy | RPO≤15m / RTO≤2h |

See [0C-OPEN-SOURCE-STACK.md](0C-OPEN-SOURCE-STACK.md) (authoritative), [16-service-architecture.md](16-service-architecture.md), and [25-deployment-architecture.md](25-deployment-architecture.md).

---

## 5. Brand & color system (WCAG 2.2 AA)

> **Palette confirmed from Mersal's live website** (mersal-ngo.org), sampled directly from the site's rendered computed styles on **2026-07-21**. Mersal's identity is a **bright teal `#00ACAC`** with deeper teals (`#009091`, `#008080`, `#003737`) and a **gold/amber `#EDA827`** accent (plus a yellow highlight `#FDED04`). Because the bright brand teal fails text contrast on white (~2.8:1), the tokens below deliberately split **brand hues** (for logo, large graphics, decorative fills) from **accessible action/text tokens**. All text/UI pairings meet WCAG 2.2 AA (≥4.5:1 text, ≥3:1 large text & UI). If Mersal has a formal print brand book, do a final reconciliation, but these hexes are the site's actual brand colors, not a guess.

### 5.1a Brand hues (official — as sampled from the live site)

| Brand color | Hex | Where it appears | Text use |
|-------------|-----|------------------|----------|
| Mersal Teal (primary brand) | `#00ACAC` | Logo, headings art, large fills, dark-surface accents | ❌ not for text on white (~2.8:1) — use as fill with dark text, or on dark bg |
| Teal — mid | `#009091` | Buttons/links on site, icons | large/UI only on white (~3.9:1) |
| Teal — deep | `#008080` | Section fills | ✅ ~4.8:1 text on white |
| Teal — darkest | `#003737` | Footer, dark surfaces, headings | ✅ high contrast |
| Mersal Gold (accent) | `#EDA827` | Highlights, calls-to-action, decorative | ❌ decorative/large only — never text on white |
| Yellow highlight | `#FDED04` | Sparingly, highlights | ❌ decorative only; pair with dark text |

### 5.1b Accessible design tokens (use these in the UI)

| Token | Hex | Use | Contrast note |
|-------|-----|-----|---------------|
| `--mersal-teal-brand` | `#00ACAC` | **Brand only** — logo, large graphics, decorative fills, accents on dark surfaces | not for body text on white |
| `--mersal-teal-700` | `#007A7A` | Primary buttons, links, icons, focus ring — text/action on white | 5.2:1 on white ✅ (white text on it 5.2:1 ✅) |
| `--mersal-teal-800` | `#005C5C` | Hover/active/pressed, header bars | ~7.8:1 on white ✅ |
| `--mersal-teal-900` | `#003737` | Dark headings, dark-mode surfaces (official deep teal) | high contrast ✅ |
| `--mersal-teal-050` | `#E6F7F7` | Tints, selected rows, hover backgrounds | background only |
| `--mersal-ink-900` | `#12262B` | Body text | 14.8:1 on white ✅ |
| `--mersal-slate-600` | `#4A5A61` | Secondary text | 7.0:1 on white ✅ |
| `--mersal-gold` | `#EDA827` | Brand accent — large/decorative highlights, chart series | not for text on white |
| `--mersal-amber-700` | `#8A5A00` | Text-safe accent (accent links, inline highlights, warnings) | 5.9:1 on white ✅ |
| `--surface` | `#FFFFFF` / `#003737` (dark) | Page background (dark mode uses deep teal) | — |

> Practical rule: **`#00ACAC` and `#EDA827` are for brand/decoration**; anything that carries meaning as text or a control uses `--mersal-teal-700`/`-800`/`-900`, `--mersal-ink-900`, or `--mersal-amber-700`. Primary buttons: `--mersal-teal-700` bg + white text (5.2:1 ✅), hover `--mersal-teal-800`.

### 5.2 Status color tokens — **never color-only**

Every status renders as **{color + icon + shape + text + tooltip}** so it is legible to color-blind and screen-reader users. This mapping is normative across all portals.

| Status (generic) | Color token | Hex (on white ≥4.5:1) | Icon | Shape/Badge | Text label |
|------------------|-------------|------------------------|------|-------------|-----------|
| Eligible / Approved / Completed | Success | `#1E7A46` | ✓ check | Solid pill | "Eligible" / "Approved" |
| Pending / In review | Info | `#1F5FA6` | ⧗ clock | Dashed pill | "Pending approval" |
| Partial | Attention | `#8A5A00` | ◐ half | Half-filled pill | "Partially used" |
| Warning / Expiring | Caution | `#B25E00` | △ triangle | Outlined pill | "Expiring" |
| Rejected / Blocked / Suspended / Expired | Danger | `#B3261E` | ✕ / ⛔ | Solid square badge | "Rejected" |
| Inactive / Draft / Cancelled | Neutral | `#4A5A61` | ○ circle | Ghost pill | "Inactive" |

Color-blindness: palette validated against protanopia/deuteranopia/tritanopia simulation for hue separability; shape + icon guarantee non-color redundancy (WCAG 1.4.1 Use of Color). See [21-accessibility-checklist.md](21-accessibility-checklist.md).

### 5.3 Typography & layout tokens
- Font: system stack + **Cairo/Noto Sans Arabic** for Arabic, **Inter/Noto Sans** for Latin. Base 16px, 1.5 line-height.
- Min target size **44×44px** (WCAG 2.5.8). Focus ring 3px, `--mersal-teal-700` `#007A7A`, never removed.
- Spacing scale 4/8/12/16/24/32/48. Max content width 1280px; fluid down to 360px.
- **Directionality:** full **RTL (Arabic)** and LTR (English); layout mirrors, not just text.

---

## 6. Status taxonomy (canonical lifecycles)

These are referenced verbatim by [23-state-machines.md](23-state-machines.md).

- **Beneficiary/Member status:** `Pending` → `Active` → (`Suspended` | `Expired` | `Blocked` | `Inactive`)
- **Investigation Order:** `Requested` → `PendingApproval` → (`Approved` | `Rejected`) → `Active` → `PartiallyUsed` → `Completed`; plus `Expired`, `Cancelled`
- **Prescription:** `Draft` → `Submitted` → (`Approved` | `Rejected`) → `PartiallyDispensed` → `Dispensed`; plus `Expired`, `Cancelled`
- **Referral:** `Requested` → `Accepted` → `Scheduled` → `Completed`; plus `Cancelled`, `Expired`
- **Authorization:** `Draft` → `Submitted` → `UnderReview` → (`Approved` | `PartiallyApproved` | `Rejected` | `InfoRequested`); plus `Overridden`, `EmergencyApproved`, `Expired`

---

## 7. Cross-cutting design principles

1. **Idempotency & atomicity on order/prescription consumption** — "consume" is a single atomic transaction with optimistic concurrency + unique constraint on `(order_line_id, status=consumed)` so duplicate usage is *impossible* (requirement).
2. **Immutable audit** — append-only, hash-chained audit events; no hard deletes of clinical/benefit data (soft delete + history tables).
3. **Least privilege / need-to-know** by default-deny; every read is authorized at row + field level.
4. **Event-driven** — orders becoming "available" to providers, notifications, and reporting are driven by domain events, not synchronous coupling.
5. **Tenant & provider isolation** — providers see only their own queue and only the minimum beneficiary data for the task.
6. **Accessibility & bilingualism are non-negotiable acceptance criteria**, not enhancements.

---

## 8. Environments

`dev` → `test/QA` → `staging (prod-like, masked data)` → `production`. Each isolated (separate resource groups, key vaults, DBs). Production data never flows downward unmasked. See [25-deployment-architecture.md](25-deployment-architecture.md) and [26-testing-strategy.md](26-testing-strategy.md).
