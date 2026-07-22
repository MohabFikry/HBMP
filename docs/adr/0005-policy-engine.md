# ADR-0005: Authorization policy engine — Cerbos (target) + native default-deny evaluator

- Status: Accepted
- Date: 2026-07-22
- Deciders: Security / Platform architecture
- Phase: 0 (0.4)

## Context
`18-security-model.md` mandates RBAC + ABAC enforced at gateway (coarse), service (scope), and **row + field level** (fine), default-deny, with break-glass. Prompt 0.4 says pick OPA or Cerbos, run as a sidecar, and ship versioned policy bundles through CI. We also need the library to be usable/testable in-process today (no sidecar available in Tier 1 dev / unit tests).

## Decision
Adopt **Cerbos** as the target external policy engine, behind the `IAuthorizationEngine` interface, with a **native in-process default-deny evaluator** (`DefaultAuthorizationEngine` over a `PolicyBundle`) as the implementation for Tier 1 dev, unit/authorization tests, and as a fallback.

- **Interface-first:** every service depends only on `IAuthorizationEngine` + the row/field primitives (`RowScope`, `FieldProjector`). Swapping the native evaluator for a Cerbos sidecar (or OPA) changes DI wiring only, not callers.
- **Why Cerbos over OPA:** Cerbos's resource/action/role policy model and condition expressions map directly onto our ABAC attributes (provider-ownership, treating-relationship, tenant, status) and its policies are plain YAML — lower authoring friction for a small team than Rego. Both run as a stateless sidecar; either satisfies the design.
- **Row + field primitives stay in-process regardless of engine:** `RowScope` produces an RLS-aligned predicate the data layer composes into SQL; `FieldProjector` strips field-classes per role and audits the strip. These are minimum-necessary enforcement in code (Invariant #2) and are not delegated to the engine.
- **Bundles are versioned + audited:** `PolicyBundle.Version`; deploying a bundle emits `admin.policy.deploy` (phase 8b) and goes through the CI review path (Security + DPO).

## Consequences
- Full authorization logic (RBAC+ABAC+break-glass+default-deny) is testable offline today; 15 authorization tests prove field-strip+audit, row predicates, break-glass time-boxing, and default-deny.
- A later phase stands up the Cerbos sidecar and ports the native bundle to Cerbos YAML; the interface guarantees no caller churn.

## Alternatives considered
- **OPA/Rego** — equally capable; Rego's learning curve is higher for a charity team. Kept as a documented alternative.
- **OpenFGA** — relationship-based; overkill for our attribute-based rules now.
- **Engine-only, no in-process primitives** — would push row/field minimization to the sidecar and complicate SQL composition + testing; rejected.
