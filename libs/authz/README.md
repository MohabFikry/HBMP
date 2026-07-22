# `libs/authz` — RBAC + ABAC authorization (row + field level)

Phase 0.4. The mandatory authorization library for every service. Default-deny; enforces minimum-necessary at **row and field level in code**, with break-glass — the runtime of `11-permission-matrix.md` + `18-security-model.md §4`. Policy-engine choice in ADR-0005 (Cerbos target, native evaluator now).

## Pieces
- **`IAuthorizationEngine.EvaluateAsync(AuthzRequest)`** → `AuthzDecision` (allow/deny + reason code + satisfied conditions). `DefaultAuthorizationEngine` evaluates RBAC (role+scope) → ABAC → break-glass over a versioned `PolicyBundle`. **Default-deny**: no matching rule ⇒ deny.
- **ABAC conditions** (`AbacConditions`): `tenant-match`, `provider-ownership`, `treating-relationship`, `resource-status-active`, `break-glass`.
- **Row-level** (`RowScope`): a predicate the data layer composes into SQL (aligns with PostgreSQL RLS) — tenant + provider + treating-beneficiary scoping. `Allows(...)` gates a row server-side.
- **Field-level** (`FieldProjector` + `FieldAccessMatrix`): strips field-classes a role may not read (reception≠diagnosis, labs≠prescriptions, pharmacies≠results, finance≠diagnoses) and **audits every strip**.
- **Break-glass** (`BreakGlassGrant`, `IBreakGlassProvider`): scoped + time-boxed; widens a denied ABAC check only within its scope/window, forcing **high-severity** audit. Grant lifecycle (dual control) is phase 8b.
- Every **deny** is audited; **allow** is audited when the resource is sensitive or access is under break-glass (via `libs/audit-client`).

## Usage
```csharp
builder.Services.AddHbmpAuditClient("orders-service");   // required (deny audit)
builder.Services.AddHbmpAuthorization();                 // default bundle + field matrix

// In a handler:
var decision = await engine.EvaluateAsync(new AuthzRequest(principal, "orders:read", resource));
if (!decision.IsAllowed) return Results.Problem(statusCode: 403, title: decision.ReasonCode);

// Row scope for a query, field projection for the response:
var scope = RowScope.For(principal);                     // → WHERE tenant/provider/…
var safe  = await projector.ProjectAsync(principal, "encounter", record);
```

## Tests (15, all green offline)
Default-deny for unmapped actions; treating-relationship allow/deny (+audited); provider-ownership cross-provider deny; missing-scope deny; break-glass widens+high-severity-audits, expired grant does not; field-strip removes disallowed classes + audits (reception/finance/lab); row scope limits tenant/provider/treated-patient.
