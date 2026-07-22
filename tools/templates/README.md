# HBMP service template

Every backend service follows the same shape and wiring. **`services/hello`** is the canonical reference implementation of this template (phase 0.5) — copy it when standing up a new bounded context.

## Structure (per CLAUDE.md)
```
services/<domain>/
  Api/             # ASP.NET minimal-API host: Program.cs wires the four libs + OTel + OpenAPI
  Domain/          # entities, aggregates, domain services (no infra deps)
  Infrastructure/  # EF Core DbContext + schema-per-service migrations, brokers, external clients
  Tests/           # unit + integration (WebApplicationFactory) + authz/concurrency tests
  Dockerfile       # multi-stage, non-root, same image for Compose + k3s
  README.md
```

## Mandatory wiring (Program.cs)
```csharp
builder.Services.AddHbmpAuthentication(builder.Configuration);   // libs/auth   — JWT + MFA
builder.Services.AddHbmpAuditClient("<domain>-service");         // libs/audit-client — hash-chained audit
builder.Services.AddHbmpAuthorization();                         // libs/authz  — RBAC+ABAC, row+field
builder.Services.AddHbmpEvents(builder.Configuration);           // libs/events — transactional outbox
builder.Services.AddOpenTelemetry()....WithTracing(...AddOtlpExporter());  // OTel → Tempo/LGTM
...
app.UseAuthentication(); app.UseAuthorization();
```
Then: RFC 7807 problem+json (`AddProblemDetails`), health probes, and endpoints gated with
`RequireAuthorization(HbmpPolicies.Scope("<scope>"))`, authorized via `IAuthorizationEngine`, mutating
inside a transaction that also enqueues audit + domain events to the outbox.

## Non-negotiables the template bakes in
- **libs/audit-client + libs/authz are mandatory** — a service that mutates without hash-chained audit,
  or returns data without field-level minimization, is not done.
- Schema-per-service + RLS; migrations expand/contract; soft-delete + `_history`; no hard delete.
- Idempotency-Key on mutations that must not double-apply; outbox for all domain events.
- Tokens validated at gateway **and** service; MFA for protected scopes.

## `dotnet new` template
A packaged `dotnet new hbmp-service` template will live here (`.template.config/`). Until then, clone
`services/hello` and rename. The reference already proves the whole slice end to end (4 integration tests).
