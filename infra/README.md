# `/infra` — Infrastructure as Code

Open-source, on-prem-first, cloud-ready. Tooling per **ADR-0002** (OpenTofu + Ansible + Helm); tiers per **ADR-0003**; network zones per **ADR-0004**.

## Layout
| Path | Purpose | Tier |
|------|---------|------|
| `compose/` | **Tier 1** single-server Docker Compose — the dev environment and pilot footprint. Stands up the full infra (Postgres, Keycloak, Kong, MinIO, RabbitMQ, NATS, Valkey, OpenSearch, OpenBao, ClamAV, Prometheus/Grafana/Loki/Tempo). Starter, **not** production-hardened — see `compose/README.md`. | 1 |
| `tofu/` | **OpenTofu** stacks (`dev/`, later `staging/`, `prod/`) + reusable `modules/`. Provisions hosts/network/DNS and, in cloud tiers, managed resources. State in a remote S3-compatible backend (MinIO) — **never committed**. | 2/3 |
| `ansible/` | Host configuration: k3s install, OS/LUKS hardening, pgBackRest/restic agents. | 2/3 |
| `helm/` | Per-service + umbrella Helm charts. Same charts target on-prem k3s (Tier 2) and cloud (Tier 3). | 2/3 |

## Secrets
No plaintext secrets in git. Runtime secrets come from **OpenBao**; config-as-code secrets are **SOPS**-encrypted. `.env` files are git-ignored; only `*.env.example` are committed.

## CI gates (`.gitlab-ci.yml`)
- `iac-validate` → `tofu init -backend=false && tofu validate` on every stack.
- `helm-lint` → `helm lint` on every chart.
Both are wired now and run as no-ops until stacks/charts land.

## Quick start (Tier 1 dev)
```bash
cd infra/compose
cp .env.example .env      # then edit every value
docker compose up -d      # requires Docker (needs root to install — see repo docs/adr/0003)
```
Endpoints and hardening checklist are in `compose/README.md`.
