# ADR-0003: Deployment-tier strategy (Tier 1 Compose → Tier 2 k3s → Tier 3 cloud)

- Status: Accepted
- Date: 2026-07-22
- Deciders: Platform architecture
- Phase: 0 (0.1)

## Context
Mersal is a charity. The platform must run on a single donated server today yet scale to HA on-prem and to cloud later without re-platforming. `0C-OPEN-SOURCE-STACK.md` and `25-deployment-architecture.md` define three tiers built from the **same containers**.

## Decision
Adopt a three-tier deployment strategy, same images throughout:

- **Tier 1 — Docker Compose, single server ($0).** For dev and the pilot go-live (phase 12). Infra defined in `infra/compose/` (reused from `HBMP-Design/deploy/tier1-compose`). Starter, not production-hardened; see its README.
- **Tier 2 — on-prem k3s cluster.** Multi-node HA (Patroni PostgreSQL, multi-replica services), deployed via the Helm charts in `infra/helm/`. Autoscaling via HPA + KEDA (queue depth).
- **Tier 3 — cloud lift-and-shift.** Same Helm charts on managed Kubernetes; migrating swaps infra, not code (OIDC, S3-compatible storage, OTel are cloud-portable).

The dev environment targets Tier 1 (Compose) or a single-node k3s. CI validates that the same Helm charts render for both k3s and a documented cloud values file.

## Consequences
- One artifact set (images + Helm charts) across all tiers; lowest cost of entry.
- Tier 1 trades HA/hardening for simplicity — hardening is phase 11, and the compose README lists what must change before real data (TLS, OpenBao real server, OpenSearch security on, LUKS, backups).

## Alternatives considered
- **k8s-only from day one** — too heavy/costly for a charity's first server and pilot.
- **PaaS/managed-only** — violates on-prem-first and $0 mandates; residency (PDPL) risk.
