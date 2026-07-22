# Phase 11 — Production Hardening & Non-Functional Assurance

**Goal:** Turn the built application into a **production-grade, operable** system by proving it meets the non-functional bar in [../08-non-functional-requirements.md](../08-non-functional-requirements.md). This cross-cutting phase runs **after** the feature phases (0–9 + admin/network/case/finance/interop) and **before** go-live (phase 12). It delivers three assurance streams — **performance & scale**, **security hardening**, and **reliability & operability (HA/DR + observability + runbooks)** — each ending in signed, evidence-backed release gates. Nothing here re-implements features; it validates, tunes, and hardens what exists, and produces the evidence the go-live gate consumes.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

---

## Skills to activate
> Activate `healthcare-database-architect` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management`. Skill files: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first

- [../0C-OPEN-SOURCE-STACK.md](../0C-OPEN-SOURCE-STACK.md) — **authoritative stack.** All hardening/DR/observability steps below use the free, open-source, on-prem-first, cloud-ready tooling defined here (Keycloak, Kong, ModSecurity/OWASP CRS, Linkerd, OpenBao/Vault, LUKS/pgcrypto, MinIO, Trivy, ClamAV, pgBackRest/Velero/restic, Prometheus/Grafana/Loki/Tempo). The security/reliability **bar is unchanged** — only product names differ.
- [../08-non-functional-requirements.md](../08-non-functional-requirements.md) — the measurable NFR targets and the **§15 release-gating NFRs** (non-negotiable). Every acceptance below traces to an `NFR-nnn`.
- [../25-deployment-architecture.md](../25-deployment-architecture.md) — on-prem k3s topology (Docker Compose single-node → k3s cluster → cloud-ready), autoscaling (HPA + KEDA on RabbitMQ/NATS queue depth), in-cluster-only data tier, **§9 HA & DR** (RPO ≤ 15 min / RTO ≤ 2 h, WORM audit durability), **§8 CI/CD** gates.
- [../18-security-model.md](../18-security-model.md) — Zero Trust, threat model, authz model, secrets, break-glass.
- [../19-audit-strategy.md](../19-audit-strategy.md) — immutable hash-chained audit; audit durability requirements.
- [../20-compliance-checklist.md](../20-compliance-checklist.md) — security sign-off items, DPIA/RoPA, pre-prod gates.
- [../15-database-erd.md](../15-database-erd.md) — schema/indexing targets for DB tuning.
- [../34-technical-documentation.md](../34-technical-documentation.md) — runbook + ADR templates (`/docs/runbooks/`); sample DR and approvals-backlog runbooks.
- [../26-testing-strategy.md](../26-testing-strategy.md) — load/soak/chaos/DR test methods; [../27-risk-assessment.md](../27-risk-assessment.md) — risks these gates retire.
- Prerequisite: the platform is functionally complete and deployed to a **prod-like staging** environment with **masked** data (never unmasked prod downstream).

---

## Prompts

### 11.1 — Performance, scalability & capacity validation

```text
Establish and PROVE the performance and scalability bar in ../08. Read ../08 (§1 PERF, §2 SCALE), ../25 (§3 k3s autoscaling, §5 data tier), ../15 first. Work in the prod-like staging environment with masked, volume-representative data. Do NOT change business behavior — this is measurement, tuning, and autoscaling only.

LOAD / STRESS / SOAK HARNESS
- Add a repeatable k6 (or JMeter) suite under /perf, versioned and runnable in CI (nightly + on-demand), targeting staging through the ingress (Traefik/NGINX + ModSecurity) + Kong Gateway as real clients do.
- Seed synthetic volume per NFR-012: >= 1M beneficiaries, >= 10M encounters. Provide a deterministic data-generation script (masked/synthetic only).
- Model realistic mixes: Reception search + eligibility bursts, encounter/order creation, provider consume, approvals worklist, dashboard reads.

TARGETS TO ASSERT (fail the run if missed)
- Eligibility API p95 <= 800 ms, p99 <= 1.5 s (NFR-002).
- Order/prescription CONSUME p95 <= 1 s AND zero double-commit under parallel consumers (NFR-003, NFR-073) — run a dedicated concurrency test hammering the same order line.
- Primary screen loads (Reception search, worklists) p95 <= 1.5 s, p99 <= 3 s (NFR-001).
- Indexed beneficiary/order search p95 <= 700 ms (NFR-004).
- Operational reports p95 <= 3 s; heavy analytics async (NFR-006).
- Event bus sustains >= 200 events/s buffered without loss (NFR-014).

AUTOSCALING
- Verify HPA (CPU/memory) on stateless workload pods and KEDA scaling on RabbitMQ/NATS JetStream queue depth for order/approval bursts (NFR-010). Run a burst test proving pods scale out, drain the queue, and scale back in; assert >= 5x baseline throughput scales ~linearly.
- Confirm liveness/readiness probes and graceful-degradation fallbacks (cached/read-only eligibility) behave under saturation (NFR-024, NFR-023).

CACHING & DB TUNING
- Validate Valkey caching (eligibility snapshots, sessions, rate-limit counters): measure hit ratio and confirm correctness (no stale-eligibility clinical decisions; TTL + invalidation on policy change).
- Profile the top-N slow queries under load; add/adjust indexes per ../15 (covering indexes for search, worklist, TAT projections); document each change as an ADR. Verify Row-Level Security predicates remain index-friendly.

ACCEPTANCE (Given/When/Then)
- Given the seeded volume and load mix, When the suite runs against staging, Then every target above is met and the run is green; a results report (p50/p95/p99, throughput, error rate) is published as a CI artifact.
- Given a queue burst, When depth crosses the KEDA threshold, Then workers scale out, the backlog drains, and scale-in follows — captured in Prometheus/Grafana.
- Given parallel consumers on one order line, When they race, Then exactly one succeeds and no duplicate/lost consumption occurs.

Deliverables: /perf suite + data generator, CI job, a PERFORMANCE-BASELINE.md with measured numbers vs targets, index/caching ADRs. No PHI in perf data or logs.
```

### 11.2 — Security hardening, threat model & sign-off

```text
Harden the platform to the security NFRs and produce a green SECURITY SIGN-OFF. Read ../18, ../08 (§4 SEC), ../20 first. Remediate findings; do not weaken any control to pass.

THREAT MODEL (STRIDE)
- Run/refresh a STRIDE threat-model review per bounded context and the edge (ingress ModSecurity/OWASP CRS, Kong, k3s, data tier) using the model in ../18. Record threats, mitigations, and residual risk in /docs/security; open backlog items for gaps.

AUTOMATED SCANNING IN CI (block on failure)
- SAST on all services; DAST (e.g., OWASP ZAP) against staging; SCA dependency scanning; container image scanning with **Trivy** in the CI pipeline (SAST/DAST/SCA/image). Enforce: NO known Critical/High vulnerabilities at release (NFR-035, NFR-037).
- Secrets hygiene: secret-scanning in CI; assert 100% secrets in **OpenBao/Vault** (GitOps secrets via SOPS), retrieved via Kubernetes ServiceAccount / workload identity — zero secrets in code, config, or images (NFR-034). Verify separate OpenBao namespace/policies per env and prod keys unreachable from lower envs.

OWASP API TOP 10 + AUTHZ
- Run an OWASP API Top 10 checklist against every public endpoint (BOLA/broken object-level authz, broken function-level authz, mass assignment, SSRF, security misconfig, etc.). For BOLA specifically, prove row+field authorization holds for every role×resource pair per ../11 (minimum-necessary), aiming for 0 unauthorized-access defects (NFR-031, NFR-040/041).
- Verify TLS 1.2+ (prefer 1.3) + HSTS everywhere (NFR-032) via a TLS scan; verify Linkerd mTLS service-to-service; verify encryption at rest — LUKS full-disk + pgcrypto column-level + MinIO SSE, with AES-256 keys in OpenBao/Vault transit engine and a rotation policy (NFR-033).

EDGE PROTECTION
- Confirm WAF (**ModSecurity + OWASP Core Rule Set**) at ingress is in prevention/blocking mode; tune rules to remove false positives without disabling coverage. Verify **Kong** rate limiting / throttling / quotas per role/route and Keycloak brute-force lockout (NFR-038).
- Confirm uploaded-file malware scan (**ClamAV**) on ingest before any access (NFR-036).
- Confirm **k3s NetworkPolicies** are default-deny with per-service least-privilege rules (services in-cluster only, no public data-plane); verify with a negative connectivity test that a pod cannot reach the data tier or another service outside its policy.

PENETRATION TEST & BREAK-GLASS
- Commission an external pen test against staging; track all findings to closure; retest High/Critical (NFR-037). Record the pen-test report + retest evidence in /docs/security.
- VERIFY break-glass / emergency-access: elevated access is time-boxed, requires justification, and writes an immutable hash-chained audit event (../19); run a drill and confirm the audit trail and auto-revocation.

SECURITY SIGN-OFF CHECKLIST
- Produce /docs/compliance/security-sign-off.md mapping each pre-prod security gate in ../20 + ../18 to evidence: threat model done, Trivy SAST/DAST/SCA/image clean, secrets in OpenBao/Vault, authz suite green, TLS/Linkerd mTLS/encryption (LUKS+pgcrypto+MinIO SSE) verified, ModSecurity WAF + Kong rate-limit + k3s NetworkPolicies configured, ClamAV upload scan on, pen-test findings closed, break-glass audited. Each line is Pass + evidence link, and the DPO/Security owner signs it.

ACCEPTANCE (Given/When/Then)
- Given the CI security gates, When a build has any Critical/High vuln or an exposed secret, Then the pipeline blocks release.
- Given any endpoint, When a role requests an object it is not permitted to see, Then it is denied (default-deny) and no unpermitted field leaks — verified by the authz suite.
- Given break-glass access, When invoked, Then it is time-boxed, justified, immutably audited, and auto-revoked.
- Given the sign-off checklist, When reviewed at the pre-prod gate, Then every item is Pass with evidence and it is signed by Security/DPO.
```

### 11.3 — Reliability, DR, observability & operability runbooks

```text
Prove the platform is reliable, recoverable, and operable. Read ../25 (§7 observability, §9 HA & DR), ../08 (§3 AVAIL, §8 REL, §9 OBS), ../34 (runbook template), ../19 first. Targets: RPO <= 15 min, RTO <= 2 h.

HIGH AVAILABILITY
- Verify multi-node k3s + Patroni-managed PostgreSQL (HA, streaming replication) and multi-replica stateless services; confirm no single point of failure across >= 2 nodes (and a second on-prem site where available) (NFR-020, NFR-021). Run a node/pod-kill chaos test and confirm graceful degradation and auto-restart (NFR-024).

BACKUP / RESTORE
- Confirm daily full + continuous WAL (PITR) backups via **pgBackRest**, **Velero** for cluster state/volumes, and **restic** for files/MinIO — with an offsite/second-site copy (NFR-070, NFR-076). Perform an actual RESTORE test to a scratch instance and reconcile row counts + a hash-chain integrity check; record evidence. Schedule restore drills quarterly.

DISASTER RECOVERY FAILOVER DRILL (the headline gate)
- Execute a full DR drill to the second on-prem site (or a cloud region if funded) per ../25 §9: promote the Patroni/PostgreSQL replica (pgBackRest PITR as fallback), restore the MinIO object store from the offsite copy, Helm/IaC-redeploy services (Velero + restic), and repoint DNS/ingress. MEASURE data loss (must be <= 15 min RPO, NFR-071) and time-to-service (must be <= 2 h RTO, NFR-072). Verify WORM/immutable audit (MinIO object-lock) survived the failover with an intact hash chain (NFR-120, NFR-123). Capture start/end timestamps and a signed drill report.

OBSERVABILITY
- Confirm OpenTelemetry traces propagate end-to-end (gateway -> services -> audit) with correlation IDs, collected in **Tempo** (NFR-080); JSON logs to **Loki**, PHI-redacted, with redaction tests (NFR-081, NFR-042).
- Build **Grafana** dashboards (Prometheus metrics) for the FOUR GOLDEN SIGNALS (latency, traffic, errors, saturation) per service AND business KPIs (approval TAT, pending approvals, consume throughput, no-show) sourced from reporting-service (NFR-082). Assert audit-to-trace correlation for investigation (NFR-084).

SLOs & ALERTING
- Define SLOs (availability, latency, error budget) per critical service and wire burn-rate alerts (Prometheus/Alertmanager). Add proactive alerts on: SLO burn, saturation/queue depth, FAILED CONSUME/failed-consume events, APPROVALS-SLA-BREACH (approvals_pending_over_SLA), and AUTH ANOMALIES / security anomalies (NFR-082, NFR-083; ../19). Fire a synthetic incident to prove each alert routes to on-call.

ON-CALL RUNBOOKS
- Author runbooks in /docs/runbooks/ following the ../34 template for at least: DR failover, backup/restore, deploy + rollback, approvals-backlog/TAT breach, failed-consume replay, auth-anomaly / suspected breach, and event-bus backlog. Each has trigger, impact, checklist, recovery, post-incident, and escalation path.

ACCEPTANCE (Given/When/Then)
- Given a primary-site outage simulation, When the DR runbook is executed, Then core services recover on the second on-prem site (or cloud region) within RTO <= 2 h with data loss <= RPO 15 min, the audit hash chain is intact, and a signed drill report is filed.
- Given a backup, When a restore test runs, Then data reconciles and integrity verifies.
- Given a failed consume / approvals-SLA breach / auth anomaly, When it occurs, Then the corresponding alert fires to on-call and its runbook resolves it.
- Given any golden-signal or business-KPI dashboard, When viewed, Then live data renders and SLO burn-rate alerts are configured.

Deliverables: DR drill report, restore evidence, dashboards-as-code, alert rules (IaC), runbooks in /docs/runbooks/.
```

---

## Guardrails

- **Production gates are non-negotiable.** A release is not shippable while any serious/critical security finding is open, the DR drill has not passed within RPO/RTO, SLOs lack alerts, or audit durability (WORM/immutable hash chain) is unverified. These map to ../08 §15.
- **Test on masked/synthetic data only.** All load, DR, restore, and security testing runs in staging or a scratch environment; **production data never flows downstream unmasked**, and no PHI appears in perf data, logs, or reports (NFR-042).
- **Harden, never weaken.** No control is disabled or threshold relaxed to pass a gate; findings are remediated and retested. Break-glass stays time-boxed, justified, and immutably audited.
- **Evidence, not assertions.** Every gate produces a versioned artifact (perf report, security sign-off, DR drill report, restore evidence) linked from the release checklist. Runbooks and dashboards are code, reviewed in PRs.
- **No feature drift.** This phase measures, tunes (indexes, caching, autoscaling, alerts), and documents — it does not change business behavior or the consumption invariants proven in earlier phases.

## Done when

- The performance suite runs green against seeded volume in staging: eligibility p95 <= 800 ms, consume p95 <= 1 s with zero double-commit, and all other §1/§2 targets met; autoscaling (HPA + KEDA) demonstrably scales out and in.
- The **security sign-off is green**: STRIDE model current, Trivy SAST/DAST/SCA/image scans clean (no Critical/High), secrets fully in OpenBao/Vault, OWASP API Top 10 + row/field authz proven, ModSecurity WAF + Kong rate limits + k3s NetworkPolicies tuned, pen-test findings closed and retested, break-glass verified — signed by Security/DPO.
- A **DR failover drill to the second on-prem site (or cloud region) succeeds within RPO <= 15 min / RTO <= 2 h**, a restore test reconciles (pgBackRest/Velero/restic), and audit WORM/immutability (MinIO object-lock) survives — all with signed reports.
- Observability is complete: OTel traces, PHI-redacted logs, golden-signal + business-KPI dashboards, SLOs with burn-rate alerts (incl. failed-consume, approvals-SLA-breach, auth anomalies), and on-call runbooks in `/docs/runbooks/`.
- All release-gating NFRs in ../08 §15 pass; evidence artifacts are linked from the go-live checklist consumed by phase 12. Global Definition of Done met.
