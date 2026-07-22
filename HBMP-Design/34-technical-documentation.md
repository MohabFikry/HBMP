# 34 — Technical Documentation Plan

> Back to [00-README-INDEX.md](00-README-INDEX.md) · [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [16-service-architecture.md](16-service-architecture.md) · [17-api-specifications.md](17-api-specifications.md) · [25-deployment-architecture.md](25-deployment-architecture.md)

Defines the documentation set the engineering team will maintain, where each lives, standards, and starter templates (a sample ADR and a sample runbook). Documentation is **treated as code**: versioned in the repo, reviewed in PRs, and kept current as a Definition-of-Done item.

---

## 1. Documentation set & ownership

| Doc type | Purpose | Location | Owner | Cadence |
|----------|---------|----------|-------|---------|
| **Architecture Decision Records (ADRs)** | Capture significant, hard-to-reverse decisions + rationale | `/docs/adr/` in mono/meta repo | Architect | Per decision |
| **Service READMEs** | Per-service: responsibility, owned data, APIs, events, run locally | Each service repo `/README.md` | Service owner | Per change |
| **API docs** | Generated from OpenAPI 3.1; published to developer portal (Kong) | Kong dev portal + `/docs/api` | API owner | Auto on release |
| **Data dictionary** | Column-level schema & sensitivity | [22-data-dictionary.md](22-data-dictionary.md) + `/docs/data` | DB architect | Per migration |
| **Event catalog** | Domain events, schemas, producers/consumers | `/docs/events` | Architect | Per change |
| **Runbooks** | Operational procedures (deploy, DR, incident, on-call) | `/docs/runbooks/` | SRE/DevOps | Per change + drills |
| **Security handbook** | Threat model, authz model, secrets, break-glass | `/docs/security` | Security | Quarterly |
| **Compliance pack** | RoPA, DPIA, retention, breach runbook | `/docs/compliance` | DPO | Per data-flow change |
| **Onboarding guide** | New-engineer setup, conventions, glossary | `/docs/onboarding` | Team lead | Quarterly |
| **User/admin guides** | End-user & admin docs (AR/EN) for Mersal staff & providers | Knowledge base | Tech writer | Per feature |
| **Test docs** | Strategy, plans, traceability | [26-testing-strategy.md](26-testing-strategy.md) + `/docs/test` | QA lead | Per release |

---

## 2. Documentation standards
- **Markdown** in-repo; diagrams as **Mermaid** (versionable) or C4 where useful.
- Every service README follows a fixed template (below).
- Public/shared terms come from the glossary in [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md) — one vocabulary everywhere.
- API reference is **generated** from the source-of-truth OpenAPI spec, never hand-written.
- Docs changes ride in the same PR as the code change; "docs updated?" is a merge checklist item.
- Bilingual (AR/EN) required for end-user/admin guides; internal engineering docs are English.

### Service README template
```
# <service-name>
## Responsibility        (one paragraph; bounded context)
## Owned data            (tables/schema owned; link to data dictionary)
## Public API            (link to OpenAPI; key endpoints)
## Events                (published / consumed; link to event catalog)
## Dependencies          (services, infra)
## Run locally           (prereqs, commands, seed data)
## Configuration         (env/config keys; secrets via OpenBao/Vault)
## Observability         (dashboards, key metrics, alerts)
## Security notes        (authz scopes, sensitive fields, min-necessary)
## Runbook links
```

---

## 3. Sample ADR

```
# ADR-0007: Atomic, idempotent consumption of investigation order lines

Status: Accepted
Date: 2026-08-xx
Context:
  Investigation orders may have multiple lines fulfilled by different providers,
  possibly partially and concurrently. The business requires: unused lines stay
  available, used lines cannot be reused, partial fulfillment is allowed, and
  duplicate usage must be IMPOSSIBLE, with a full audit trail.

Decision:
  Model each fulfillable unit as an order_line with a status. "Consume" is a single
  DB transaction using optimistic concurrency (version column) plus a unique
  partial index enforcing at most one active consumption per line
  (unique(order_line_id) WHERE status='consumed'). The consume endpoint accepts an
  Idempotency-Key; a replay returns the original result without side effects.
  Consumption emits OrderLineConsumed to the event bus via the outbox pattern; the
  audit-service records the event immutably.

Consequences:
  + Duplicate usage impossible even under concurrency/retries.
  + Partial fulfillment and "remaining stays active" fall out naturally.
  - Requires careful transaction boundaries and outbox plumbing in orders-service.
  - Cross-service consistency is eventual for downstream consumers (acceptable).

Alternatives considered:
  - Application-level locks only (rejected: race windows).
  - Distributed lock manager (rejected: added infra, weaker guarantee than DB constraint).

Related: 23-state-machines.md, 24-sequence-diagrams.md, 16-service-architecture.md
```

---

## 4. Sample runbook

```
# RUNBOOK: Approvals backlog / TAT breach

Trigger: Alert "approvals_pending_over_SLA" or KEDA queue depth > threshold.

Impact: High-cost services (MRI/CT/surgery/expensive drugs) delayed for beneficiaries.

Checklist:
  1. Confirm scope: open Approvals SLA/TAT board; identify count + oldest item.
  2. Check approvals-service health (Grafana/Prometheus/Loki: latency, errors, pod restarts).
  3. Check event bus: is the approvals queue draining? (RabbitMQ metrics)
  4. If system healthy but volume high: notify Medical Director; enable additional
     reviewers / emergency-approval path per policy.
  5. If system unhealthy: scale approvals-service pods; inspect failing dependency
     (DB, identity, notification). Roll back last deploy if correlated.
  6. Verify audit events still writing (audit-service healthy).

Recovery: TAT board returns under SLA; queue depth normal.
Post-incident: log in incident register; add to risk review (27-risk-assessment.md).
Escalation: on-call SRE -> Eng lead -> Medical Director (clinical impact).
```

---

## 5. Where the design set fits
This 35-document design workspace is the **pre-implementation baseline**. Once build starts, living docs (ADRs, service READMEs, generated API docs, runbooks) supersede the corresponding design docs, which are retained as the approved baseline and rationale record. The [00-README-INDEX.md](00-README-INDEX.md) remains the map.

---

### Cross-references
- Services/events: [16-service-architecture.md](16-service-architecture.md) · API source-of-truth: [17-api-specifications.md](17-api-specifications.md)
- Ops/DR: [25-deployment-architecture.md](25-deployment-architecture.md) · Security: [18-security-model.md](18-security-model.md) · Testing: [26-testing-strategy.md](26-testing-strategy.md)
