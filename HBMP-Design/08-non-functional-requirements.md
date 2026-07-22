# 08 — Non-Functional Requirements

> Back to [00-README-INDEX.md](00-README-INDEX.md) · [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [07-functional-requirements.md](07-functional-requirements.md) · [18-security-model.md](18-security-model.md) · [20-compliance-checklist.md](20-compliance-checklist.md) · [21-accessibility-checklist.md](21-accessibility-checklist.md) · [25-deployment-architecture.md](25-deployment-architecture.md)

This document specifies the **non-functional requirements (NFRs)** — the quality attributes the HBMP must satisfy. Each NFR carries:

- **ID** — `NFR-nnn` (grouped by quality attribute).
- **Requirement** — the quality constraint.
- **Metric / Target** — the measurable, testable threshold.
- **Verification** — how conformance is proven (test type, tool, or evidence).
- **Priority** — MoSCoW (**M/S/C/W**).

> Targets are **design intents for a clinic-scale humanitarian deployment**, sized for growth to multi-site / multi-tenant. They must be revalidated against confirmed load figures during [26-testing-strategy.md](26-testing-strategy.md) planning.

---

## 1. Performance & Latency (`PERF`)

| ID | Requirement | Metric / Target | Verification | Pri |
|----|-------------|-----------------|--------------|-----|
| NFR-001 | Interactive screens respond quickly under normal load. | **p95 ≤ 1.5 s**, p99 ≤ 3 s for primary screen loads (Reception search, worklists). | Load test (k6/JMeter) in staging. | M |
| NFR-002 | Eligibility check returns fast enough for front-desk flow. | Eligibility API **p95 ≤ 800 ms**, p99 ≤ 1.5 s. | API load test + APM traces. | M |
| NFR-003 | Order/prescription **consume** transaction commits promptly and atomically. | **p95 ≤ 1 s**; zero double-commit under concurrency. | Concurrency test (parallel consumers) + DB constraint check. | M |
| NFR-004 | Search (beneficiary/order lookup) is responsive with typo tolerance. | **p95 ≤ 700 ms** for indexed search over expected corpus. | Search benchmark on representative dataset. | S |
| NFR-005 | Document/report upload handles typical files without blocking UI. | Up to **25 MB** file; async virus scan; UI feedback < 1 s to accept. | Upload test + ClamAV scan log. | S |
| NFR-006 | Dashboards/reports render within acceptable time. | Operational report **p95 ≤ 3 s**; heavy analytics may be async with progress. | Report benchmark. | S |
| NFR-007 | Client bundle is optimized per portal (code-splitting). | Initial route **≤ 300 KB gzipped** JS per portal; lazy-load the rest. | Bundle analysis in CI. | S |

---

## 2. Scalability (`SCALE`)

| ID | Requirement | Metric / Target | Verification | Pri |
|----|-------------|-----------------|--------------|-----|
| NFR-010 | System scales horizontally without redesign. | Stateless services on k3s scale via HPA; **linear throughput to ≥ 5×** baseline load. | Scale test with autoscaling enabled. | M |
| NFR-011 | Supports concurrent operational users across portals. | Design baseline **≥ 500 concurrent active users**; degrade gracefully beyond. | Soak/stress test. | S |
| NFR-012 | Data model supports growing beneficiary population. | **≥ 1M** beneficiaries and **≥ 10M** encounters without schema change. | Volume test with synthetic data. | S |
| NFR-013 | Multi-tenant partitioning ready. | Add a tenant with **no code change**, isolated data & config. | Tenant provisioning test. | S |
| NFR-014 | Event bus absorbs bursts (order/Rx routing). | Sustained **≥ 200 events/s**, buffered without loss. | Event throughput test. | S |

---

## 3. Availability & SLA (`AVAIL`)

| ID | Requirement | Metric / Target | Verification | Pri |
|----|-------------|-----------------|--------------|-----|
| NFR-020 | Core platform is highly available during clinic hours. | **≥ 99.5%** monthly availability (target 99.9% for core APIs). | Uptime monitoring + SLO reporting. | M |
| NFR-021 | No single point of failure in critical path. | Redundant instances across ≥ 2 availability zones. | Architecture review + failover test. | M |
| NFR-022 | Planned maintenance minimizes disruption. | Zero-downtime deploys (rolling/blue-green); maintenance windows announced. | Deploy drill. | S |
| NFR-023 | Graceful degradation on dependency failure. | Read-only/cached eligibility fallback (FR-ELG-009); clear user messaging. | Chaos/fault-injection test. | S |
| NFR-024 | Health checks & readiness gates. | Liveness/readiness probes on every service; auto-restart unhealthy pods. | Kubernetes probe config review. | M |

---

## 4. Security (`SEC`) — see [18-security-model.md](18-security-model.md)

| ID | Requirement | Metric / Target | Verification | Pri |
|----|-------------|-----------------|--------------|-----|
| NFR-030 | Strong authentication with MFA. | 100% of staff via OIDC + MFA; no local passwords for privileged roles. | IdP config audit. | M |
| NFR-031 | Fine-grained authorization, default-deny. | RBAC+ABAC; every endpoint authorized at row+field level; **0** unauthorized-access defects at release. | Authorization test suite + pen test. | M |
| NFR-032 | Encryption in transit. | **TLS 1.2+ (prefer 1.3)** everywhere; HSTS. | TLS scan (e.g., testssl). | M |
| NFR-033 | Encryption at rest with managed keys. | AES-256; keys in OpenBao/Vault (transit engine); rotation policy. | Key management audit. | M |
| NFR-034 | Secrets never in code/config. | 100% secrets in OpenBao/Vault; secret scanning in CI. | CI secret-scan + repo audit. | M |
| NFR-035 | Vulnerability management. | Dependency & container scans in CI; **no known Critical/High** at release. | SCA + image scan reports. | M |
| NFR-036 | Uploaded files scanned. | Virus/malware scan on ingest before any access. | Ingest pipeline test. | M |
| NFR-037 | Penetration testing before major releases. | External pen test; all High/Critical remediated. | Pen-test report + retest. | S |
| NFR-038 | Rate limiting & abuse protection at gateway. | Configurable per-role/route limits; brute-force lockout. | Gateway config + test. | S |

---

## 5. Privacy & Data Minimization (`PRIV`)

| ID | Requirement | Metric / Target | Verification | Pri |
|----|-------------|-----------------|--------------|-----|
| NFR-040 | Minimum-necessary enforced at data layer, not just UI. | Each portal's API returns only permitted fields; verified for all role×resource pairs in [11-permission-matrix.md](11-permission-matrix.md). | Automated field-exposure tests per role. | M |
| NFR-041 | Documented role-based data zoning honored. | Reception≠EMR; Labs≠Rx; Pharmacy≠lab results; Finance≠diagnoses; Doctors=only treated patients; Approval=clinical visibility. | Contract tests per rule. | M |
| NFR-042 | PHI/PII exposure in logs prevented. | **0** PHI in application logs; structured redaction. | Log inspection + redaction tests. | M |
| NFR-043 | Data retention & deletion policy. | Retention schedule defined; soft-delete + lawful erasure workflow. | Policy doc + deletion test. | S |
| NFR-044 | Analytics use de-identified/pseudonymized data. | Aggregates carry no direct identifiers unless authorized. | Dataset review. | M |
| NFR-045 | Consent gates sensitive processing. | Missing mandatory consent blocks clinical use (FR-REG-009). | Functional test. | M |

---

## 6. Accessibility (`A11Y`) — see [21-accessibility-checklist.md](21-accessibility-checklist.md)

| ID | Requirement | Metric / Target | Verification | Pri |
|----|-------------|-----------------|--------------|-----|
| NFR-050 | Conforms to **WCAG 2.2 Level AA**. | 100% of AA success criteria met on shipped screens. | Axe/Lighthouse automated + manual audit. | M |
| NFR-051 | Full keyboard operability. | Every action reachable/operable without a mouse; visible focus. | Keyboard test scripts (21). | M |
| NFR-052 | Screen-reader support. | NVDA/JAWS/VoiceOver announce roles, states, errors; AR + EN. | SR test scripts (21). | M |
| NFR-053 | Color is never the sole status signal. | Status = color + icon + shape + text + tooltip (per 0A §5.2). | Visual + color-blind simulation. | M |
| NFR-054 | Minimum target size & contrast. | ≥ **44×44px** targets; text ≥ 4.5:1, UI ≥ 3:1. | Design token audit + contrast check. | M |
| NFR-055 | Respects reduced-motion & zoom. | Honors `prefers-reduced-motion`; usable at 200% zoom / 320px reflow. | Manual test. | S |

---

## 7. Localization & RTL (`I18N`)

| ID | Requirement | Metric / Target | Verification | Pri |
|----|-------------|-----------------|--------------|-----|
| NFR-060 | Full bilingual UI: Arabic (RTL) + English (LTR). | 100% of user-facing strings localized; no hard-coded text. | i18n coverage lint + review. | M |
| NFR-061 | True RTL layout mirroring, not just text direction. | Layout, icons, and flows mirror correctly in AR. | RTL visual audit. | M |
| NFR-062 | Locale-correct dates/numbers/units. | Timestamps stored UTC, displayed `Africa/Cairo` (user-selectable); AR/EN numerals as configured. | Formatting test. | S |
| NFR-063 | Bilingual master-data search. | ICD/CPT/Drug searchable by AR or EN term with typo tolerance. | Search test. | S |
| NFR-064 | Bilingual notifications. | Outbound comms in recipient's preferred language. | Notification test. | M |

---

## 8. Reliability, DR, RPO/RTO (`REL`)

| ID | Requirement | Metric / Target | Verification | Pri |
|----|-------------|-----------------|--------------|-----|
| NFR-070 | Backups are regular and restorable. | Automated backups; **restore drills quarterly**. | Restore test evidence. | M |
| NFR-071 | Recovery Point Objective. | **RPO ≤ 15 min** for OLTP (point-in-time restore / geo-replication). | DR test measuring data loss. | M |
| NFR-072 | Recovery Time Objective. | **RTO ≤ 4 h** for full-region recovery of core services. | DR failover drill. | M |
| NFR-073 | No data loss on consumption transactions. | Consumption is durable & atomic; **0** lost/duplicate on crash-recovery. | Crash-recovery test. | M |
| NFR-074 | Idempotent, retry-safe operations. | Mutating APIs accept idempotency keys (FR-INV-004). | Retry test. | M |
| NFR-075 | Message durability. | Events persisted; at-least-once delivery with dedupe. | Broker config + test. | S |
| NFR-076 | Geo-redundant storage for documents/backups. | Offsite/second-site copies of MinIO objects + backups (restic). | Config audit. | S |

---

## 9. Observability (`OBS`)

| ID | Requirement | Metric / Target | Verification | Pri |
|----|-------------|-----------------|--------------|-----|
| NFR-080 | Distributed tracing across services. | OpenTelemetry trace context propagated end-to-end; correlation IDs. | Trace inspection in Tempo/Jaeger. | M |
| NFR-081 | Centralized structured logging. | JSON logs to Loki; queryable; PHI-redacted. | Log platform review. | M |
| NFR-082 | Metrics & SLO dashboards. | RED/USE metrics per service; SLO burn-rate alerts. | Dashboard + alert config. | S |
| NFR-083 | Proactive alerting. | Alerts on latency, error rate, saturation, and security anomalies. | Alert test (synthetic incident). | S |
| NFR-084 | Audit correlation. | Audit events correlate to traces/sessions for investigation. | Cross-reference test. | S |

---

## 10. Maintainability (`MAINT`)

| ID | Requirement | Metric / Target | Verification | Pri |
|----|-------------|-----------------|--------------|-----|
| NFR-090 | Modular, service-oriented architecture. | Schema/DB-per-service; low coupling; clear domain boundaries. | Architecture review ([16](16-service-architecture.md)). | M |
| NFR-091 | Automated test coverage on critical paths. | **≥ 80%** unit coverage on domain logic; consumption invariants have dedicated tests. | Coverage report in CI. | S |
| NFR-092 | Coding standards & linting enforced. | CI blocks on lint/format/type errors. | CI config. | S |
| NFR-093 | Documented APIs. | OpenAPI 3.1 for all services; kept in sync. | Spec lint + drift check ([17](17-api-specifications.md)). | M |
| NFR-094 | Reusable design system / tokens. | Shared component library & tokens (0A §5) reused across portals. | Component audit. | S |
| NFR-095 | Config over code. | Environment/behavior via config; no rebuild to change tenant/policy settings. | Config review. | S |

---

## 11. Portability & Cloud Readiness (`PORT`)

| ID | Requirement | Metric / Target | Verification | Pri |
|----|-------------|-----------------|--------------|-----|
| NFR-100 | Containerized, cloud-agnostic where practical. | All services in OCI containers; no un-abstracted proprietary lock-in on core logic. | Build & portability review. | M |
| NFR-101 | Infrastructure as Code. | 100% of infra via OpenTofu + Ansible + Helm; reproducible environments. | IaC repo + `plan` review. | M |
| NFR-102 | Standards-based interfaces. | REST + OpenAPI 3.1; **FHIR R4-aligned** resources where practical. | Spec conformance. | S |
| NFR-103 | Environment parity. | dev → test → staging → prod isolated & consistent (0A §8). | Env config diff. | M |
| NFR-104 | Data export/portability. | Beneficiary data exportable in standard format on lawful request. | Export test. | S |

---

## 12. Compliance (`COMP`) — see [20-compliance-checklist.md](20-compliance-checklist.md)

| ID | Requirement | Metric / Target | Verification | Pri |
|----|-------------|-----------------|--------------|-----|
| NFR-110 | Align to **HIPAA-style** safeguards (as principles). | Access controls, audit, minimum-necessary, encryption implemented. | Controls mapping. | M |
| NFR-111 | Align to **GDPR principles**. | Lawful basis/consent, data-subject rights, minimization, retention. | DPIA + controls mapping. | M |
| NFR-112 | Respect Egyptian data-protection expectations. | Local data-residency & processing considerations documented. | Legal review. | S |
| NFR-113 | Auditable consent & data-subject workflows. | Consent, access, and erasure requests logged and fulfillable. | Workflow test. | S |
| NFR-114 | Clinical coding standards adopted. | ICD-10 (ICD-11 ready), CPT, LOINC-ready, ATC in use. | Master-data audit. | M |

---

## 13. Auditability (`AUDIT`)

| ID | Requirement | Metric / Target | Verification | Pri |
|----|-------------|-----------------|--------------|-----|
| NFR-120 | Tamper-evident audit trail. | Append-only, hash-chained; **0** ability to silently alter. | Integrity verification test. | M |
| NFR-121 | PHI read logging. | 100% of EMR/PHI reads logged with actor/target. | Access-log audit. | M |
| NFR-122 | No hard deletes of clinical/benefit data. | Soft-delete + history tables everywhere. | Schema review. | M |
| NFR-123 | Audit retention & protection. | Retained per policy; immutable to admins. | Retention + access review. | M |
| NFR-124 | Investigable & exportable audit. | Auditors query/replay per beneficiary/provider/user. | Query test. | S |

---

## 14. Verification methods legend

| Method | Meaning |
|--------|---------|
| Load/Stress/Soak test | Synthetic traffic against staging (k6/JMeter). |
| Concurrency/Crash test | Parallel operations + fault injection to prove invariants & recovery. |
| APM/Trace inspection | OpenTelemetry + Grafana/Tempo evidence. |
| Automated a11y | Axe/Lighthouse in CI; manual audit for judgment criteria. |
| SR/Keyboard scripts | Scripted manual tests in [21-accessibility-checklist.md](21-accessibility-checklist.md). |
| Config/Architecture review | Documented review against target ([16](16-service-architecture.md), [25](25-deployment-architecture.md)). |
| Pen test / SCA | External security testing + software composition analysis. |
| DR drill | Scheduled failover/restore exercise measuring RPO/RTO. |

---

## 15. Release-gating NFRs (non-negotiable)

The following are **acceptance gates** — a release is not shippable if any fail:

- **NFR-002/003** eligibility & consumption latency + atomicity
- **NFR-030/031/032/033** auth, authorization, encryption
- **NFR-040/041/042** data-minimization enforcement & no-PHI-in-logs
- **NFR-050–054** WCAG 2.2 AA core
- **NFR-060/061** bilingual + RTL
- **NFR-071/072/073** RPO/RTO + no consumption data loss
- **NFR-120/121/122** tamper-evident audit + PHI read logging + no hard delete

> These trace to the "Done" acceptance gate in [21-accessibility-checklist.md](21-accessibility-checklist.md) §Acceptance Gate and the MVP bar in [28-mvp-definition.md](28-mvp-definition.md).

---

### Cross-references
- Functional counterparts: [07-functional-requirements.md](07-functional-requirements.md)
- Security detail: [18-security-model.md](18-security-model.md) · Audit: [19-audit-strategy.md](19-audit-strategy.md)
- Compliance detail: [20-compliance-checklist.md](20-compliance-checklist.md) · Accessibility: [21-accessibility-checklist.md](21-accessibility-checklist.md)
- Deployment/DR: [25-deployment-architecture.md](25-deployment-architecture.md) · Testing: [26-testing-strategy.md](26-testing-strategy.md)
