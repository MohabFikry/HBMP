# ADR-0004: Network topology & edge

- Status: Accepted
- Date: 2026-07-22
- Deciders: Platform architecture / Security
- Phase: 0 (0.1)

## Context
Zero-Trust, least-privilege, default-deny (`18-security-model.md`). Public traffic must terminate at a hardened edge; services are in-cluster-only; the data tier is never publicly reachable; service-to-service traffic is mutually authenticated.

## Decision
Three network zones, default-deny between them:

1. **Edge (public).** Traefik/NGINX Ingress + **ModSecurity (OWASP CRS)** WAF + Let's Encrypt TLS 1.2+/1.3 terminate all inbound traffic. Only ports 80/443 are exposed.
2. **Gateway + application (in-cluster).** **Kong** (OSS) sits behind the edge: routing, OIDC/JWT validation against Keycloak, per-role scope checks, rate limiting, and correlation-id (W3C traceparent) propagation. Services run as ClusterIP only. Service-to-service traffic is **mTLS via Linkerd**. k3s **NetworkPolicies are default-deny** with per-service least-privilege allows.
3. **Data & platform tier (private).** PostgreSQL, RabbitMQ, NATS, Valkey, OpenSearch, MinIO, OpenBao — reachable only from their owning services, never from the public plane; no host ports in Tier 2/3.

Defense in depth for authz: gateway (coarse RBAC) → service (scope) → **row + field level** (ABAC + PostgreSQL RLS). Audit is isolated with its own identity and WORM store.

For **Tier 1 (Compose)** dev only, some ports are published to the host for convenience (documented as starter-only in `infra/compose`); Kong admin binds to 127.0.0.1. These are closed/tightened for Tier 2/3.

## Consequences
- Clear blast-radius boundaries; a compromised app pod cannot reach the data tier outside its NetworkPolicy (verified by a negative connectivity test in phase 11).
- Edge, gateway, and mesh are separate concerns, each independently hardenable.

## Alternatives considered
- **Kong as the sole edge (no separate ingress/WAF)** — loses the ModSecurity/OWASP-CRS layer; rejected.
- **No service mesh (plain TLS)** — loses automatic mTLS + identity; Linkerd is lightweight enough for on-prem.
