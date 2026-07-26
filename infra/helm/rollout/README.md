# Progressive delivery + automated rollback (phase 12.2)

Canary rollout on k3s via **Argo Rollouts** with **Linkerd** for traffic shifting and an
**AnalysisTemplate** wired to the **phase-11 SLOs**. A canary that breaches an SLO (error rate,
p99 latency, or error-budget burn) is **auto-aborted and rolled back** — no manual step.

Files:
- `rollout-template.yaml` — a reusable `Rollout` (canary 10→25→50→100) + `Service`/traffic wiring.
  Applied per service by Helm (`{{ .Values.service }}`, `{{ .Values.image }}`).
- `analysis-slo.yaml` — the `AnalysisTemplate` the rollout runs at each step: Prometheus queries for
  success-rate and p99 latency against the phase-11 thresholds; failure fails the step → rollback.

## SLO thresholds (from phase 11 / `docs/PERFORMANCE-BASELINE.md`, `infra/compose/config/rules/`)
| Signal | Query basis | Abort when |
|---|---|---|
| Success rate | `1 - (5xx / total)` over 5m | < 0.99 |
| p99 latency | `histogram_quantile(0.99, …)` over 5m | > 1s (read) |
| Burn rate | multi-window SLO burn | fast-burn firing |

## Proving automated rollback (staging drill — required before prod)
Deploy a deliberately bad canary (e.g. an image that 500s) to **staging**:

```bash
kubectl argo rollouts set image hbmp-emr emr=harbor.mersal.internal/hbmp/emr:bad-canary
kubectl argo rollouts get rollout hbmp-emr --watch
```
Expected: at the first analysis step the success-rate query drops below 0.99 → the AnalysisRun fails
→ the rollout **auto-aborts** and reverts to the stable ReplicaSet → an alert fires. This drill is a
hard precondition on the go-live gate (proves rollback works without a human).

> Tier 1 (single-server Compose) has no canary substrate; there the rollback path is
> `docs/runbooks/deploy-and-rollback.md` (re-point to the previous image tag). The same Helm chart +
> Rollout scales to Tier 2/3 (k3s/cloud) unchanged.
