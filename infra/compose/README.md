# Tier 1 — Single-server Docker Compose starter

Stands up the **entire open-source Mersal HBMP infrastructure** on one machine so you can start building and running services on day one, at **$0 software-licensing cost**. This is Tier 1 from [`../../0C-OPEN-SOURCE-STACK.md`](../../0C-OPEN-SOURCE-STACK.md) — grow to k3s (Tier 2) or cloud (Tier 3) later with the same containers.

> ⚠️ **This is a starter, not a production deployment.** It boots the stack with sensible defaults and dev-mode secrets so you can develop immediately. Do **not** put real beneficiary data behind it until you complete the **Production hardening** checklist below (TLS, secrets in OpenBao, OpenSearch security on, Keycloak production mode, disk encryption, backups). Hardening is [`../../claude-code-prompts/phase-11-hardening-and-nfr.md`](../../claude-code-prompts/phase-11-hardening-and-nfr.md).

---

## Prerequisites
- A Linux server (or VM/VPS) with **Docker Engine + Docker Compose v2**. 8 GB RAM is comfortable for the full stack; 4 GB works if you trim OpenSearch/observability.
- `sysctl -w vm.max_map_count=262144` (OpenSearch needs it; make it persistent in `/etc/sysctl.conf`).
- Ports free per the service map below (or edit the `ports:` mappings).

## Quick start
```bash
cd deploy/tier1-compose
cp .env.example .env         # then edit .env — replace EVERY password
docker compose pull
docker compose up -d
docker compose ps            # wait for health = healthy
```
Bring it down with `docker compose down` (add `-v` to also wipe data volumes).

## Service map (default ports)
| Service | URL / port | Purpose | First-login |
|---------|-----------|---------|-------------|
| Kong Gateway | http://SERVER:8000 | Public API entry (admin on 127.0.0.1:8001) | — |
| Keycloak | http://SERVER:8080 | Identity + MFA | `KEYCLOAK_ADMIN` / `.env` |
| PostgreSQL | SERVER:5432 | Relational DB (`keycloak`, `hbmp`) | `POSTGRES_USER` / `.env` |
| MinIO API / Console | :9000 / http://SERVER:9001 | S3 storage + WORM audit | `MINIO_ROOT_USER` / `.env` |
| RabbitMQ | :5672 / http://SERVER:15672 | Command queues | `RABBITMQ_USER` / `.env` |
| NATS JetStream | :4222 / :8222 | Event stream | — |
| Valkey | :6379 | Cache | password in `.env` |
| OpenSearch | http://SERVER:9200 | Search | admin / `.env` |
| OpenBao | http://SERVER:8200 | Secrets / KMS | root token in `.env` |
| ClamAV | :3310 | Upload malware scan | — |
| Prometheus | http://SERVER:9090 | Metrics | — |
| Grafana | http://SERVER:3000 | Dashboards (LGTM) | `GRAFANA_ADMIN_USER` / `.env` |
| Loki / Tempo | :3100 / :3200,:4317 | Logs / traces (OTLP) | — |

## First-boot setup (once services are healthy)
1. **Keycloak:** log in → create realm `mersal` → create OIDC clients per role/app (web BFF, each service) → enable **MFA** (OTP/WebAuthn) as a required action → set password & brute-force policies. Point services' `Auth__Authority` at `http://keycloak:8080/realms/mersal`.
2. **MinIO:** open the console → create buckets: `documents`, `lab-results`, `imaging`, `audit-archive`. Turn on **Object Locking (WORM)** for `audit-archive` (immutable audit). Create a scoped service account per app (not root).
3. **OpenBao:** log in with the dev root token → enable the `transit` engine (KMS for AES-256 keys) and a `kv` mount → store service secrets there. (Dev mode is in-memory — for production run a real server with unseal keys.)
4. **OpenSearch:** create the beneficiary/order search indices with **minimum-necessary fields only** (no EMR/clinical fields) — see [`../../claude-code-prompts/phase-2-eligibility-reception.md`](../../claude-code-prompts/phase-2-eligibility-reception.md).
5. **Kong:** add each service + routes to `config/kong.yml` (JWT + rate-limiting plugins) and `docker compose restart kong`.

## Adding application services
As each .NET service is built, uncomment/copy the template at the bottom of `compose.yaml` (env wires it to Postgres, Keycloak, RabbitMQ, NATS, Valkey, OpenSearch, MinIO, OpenBao, and Tempo/OTLP). Build images with your CI (GitLab CE + Harbor + Trivy) and reference them by tag.

## Production hardening checklist (before real data)
- [ ] **TLS at the edge:** put **Traefik/NGINX + ModSecurity (OWASP CRS)** in front, terminate TLS with **Let's Encrypt**; stop exposing service ports directly — route through the gateway only.
- [ ] **Secrets:** move everything out of `.env` into **OpenBao** (real server, not `-dev`); inject via app; rotate. Never commit `.env`.
- [ ] **OpenSearch:** set `OPENSEARCH_DISABLE_SECURITY=false`, configure users/roles + TLS.
- [ ] **Keycloak:** run in production mode (`start` + `KC_HOSTNAME` + TLS), not `start-dev`.
- [ ] **Auth dev relaxations:** this file sets `Auth__RequireHttpsMetadata=false` and `Auth__ProtectedScopeRequiresMfa=false` on every service. Both default to **`true`** in code (`libs/auth/HbmpAuthOptions.cs`) and nothing under `infra/helm` overrides them, so deployed tiers stay secure — but never carry these two lines over into a Helm values file or a real `.env`.
- [ ] **Disk:** enable **LUKS** full-disk encryption on the host; confirm **pgcrypto** for PHI/PII columns.
- [ ] **Backups/DR:** wire **pgBackRest** (Postgres PITR) + **restic** (MinIO/files) with an **offsite** copy; test a restore. (Velero applies once you move to k3s.)
- [ ] **Network:** firewall the host; bind admin UIs (RabbitMQ, Grafana, Kong admin, OpenBao) to localhost or a VPN.
- [ ] **Resource limits & healthchecks** on app services; set restart policies; enable log rotation.
- [ ] Run the [`phase-11`](../../claude-code-prompts/phase-11-hardening-and-nfr.md) security + DR gates and get sign-off before go-live ([`phase-12`](../../claude-code-prompts/phase-12-migration-and-golive.md)).

## Growing beyond one server
The same images and env contracts move to **k3s + Helm** (Tier 2: PostgreSQL HA via Patroni, distributed MinIO, Linkerd mTLS, NetworkPolicies) and then to any **managed cloud** (Tier 3) by swapping infra, not code — see [`../../25-deployment-architecture.md`](../../25-deployment-architecture.md).

---
*Part of the Mersal HBMP build kit. Stack rationale: [`../../0C-OPEN-SOURCE-STACK.md`](../../0C-OPEN-SOURCE-STACK.md).*
