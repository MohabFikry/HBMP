# ADR-0016 — FHIR R4 façade + interoperability layer (Phase 13)

- Status: Accepted
- Date: 2026-07-27
- Deciders: HBMP platform / interop
- Context docs: `17-api-specifications.md §12`, `16-service-architecture.md`, `35-implementation-plan.md §10`,
  `20-compliance-checklist.md §5/§6`, `11-permission-matrix.md`, `18-security-model.md`, `19-audit-strategy.md`.

## Context

v1 exposes native `/api/v1` REST. Phase 13 adds the **interoperability surface** so future partners (UNHCR,
government, insurers, HL7 networks, digital referral) attach **without re-platforming the core** (16 §, 35 §10).
Two decisions had to be pinned: (1) how to expose FHIR R4 without a second source of truth or an authorization
bypass; (2) whether to take a full FHIR SDK dependency.

## Decision

### 1. A separate `interop-service` (bounded context `interop`, base path `/fhir/r4`) that is an ADAPTER

- The façade **owns no clinical data**. It reads/writes the internal model through the owning services' native
  `/api/v1` endpoints **under the caller's bearer token** (`IFhirDataSource`), maps to/from FHIR R4, and stores
  only mapping/idempotency metadata (`interop.fhir_create`). Internal relational storage stays the source of
  truth (17 §12).
- `/fhir/r4` is **independently versioned**; `/api/v1` remains primary and unchanged. No core service was
  modified to ship the façade.

### 2. Minimum-necessary is enforced in TWO layers — the façade is never a bypass

- **Coarse (façade):** each FHIR interaction (resource × verb) is a distinct action in `InteropPolicies`
  (`libs/authz`), gated by role + SMART scope + tenant through the same engine as native APIs, so every deny is
  audited. The **role set per resource is the hard min-necessary boundary** — a role that cannot read a class of
  data natively is simply absent from that resource's rule. Finance/Reception/Pharmacy/Lab are absent from
  `Condition`/`DiagnosticReport` reads → `GET /fhir/r4/Condition` is default-denied for them.
- **Fine (owning service):** field-level projection + record-level ABAC (treating-relationship,
  provider-ownership, sensitive-result release) is enforced by the sibling when the façade calls it under the
  caller's token. The façade re-implements none of it (defense in depth).
- SMART-on-FHIR scopes (`fhir:read:{Resource}`, `fhir:write:{Resource}`) are **additive** — granted to
  integration clients on top of the frozen Phase-17 token contract, not replacing the core scope vocabulary.

### 3. Writes translate to native commands; derived resources are read-only

- WRITE exists only where a create is safe and sensible: `ServiceRequest`/referral, `MedicationRequest`,
  `Observation`, `AllergyIntolerance`. The inbound FHIR resource is translated (`WriteTranslators`) to the owning
  service's native command and POSTed under the caller's token (the sibling applies its own authz + validation +
  audit). Derived/immutable resources (`DiagnosticReport`, `Condition`, `Encounter`, `Patient`, `Coverage`)
  reject POST with an `OperationOutcome`.
- Creates are idempotent: FHIR `If-None-Exist` / `Idempotency-Key` → `interop.fhir_create` ledger returns the
  prior resource, never a second downstream command (proven on real PG).

### 4. Hand-rolled R4 JSON, not a full FHIR SDK — for now

- The façade emits/consumes spec-shaped R4 JSON via `System.Text.Json.Nodes` (`Domain/Fhir`), matching the
  repo's minimal-dependency, warnings-as-errors, supply-chain-audited posture (same call made for YAML in Phase
  12). The 13.3 conformance harness validates structure + cardinality + CapabilityStatement/endpoint parity.
- **Reversible:** a Firely (`Hl7.Fhir.R4`) StructureDefinition validator can be swapped in behind the conformance
  harness later for full profile validation, without changing the façade's shape.

## Consequences

- Adding a partner or a new mapped resource is **additive**: a mapper (+ optional translator) + an
  `InteropPolicies` rule + an `IFhirDataSource` method — no core redesign.
- Every FHIR read/search/create is hash-chain audited at the boundary (`FhirAudit`) in addition to the engine's
  authz audit; exports/bulk are high-severity.
- **Governance gate (13.2/13.3):** no external integration goes live without a DPIA + data-sharing agreement; the
  `DpiaGate` refuses enablement (CI + runtime) until both artifacts exist (20 §6), cross-border honours PDPL
  (20 §5).
- The production `HttpFhirDataSource` wiring to native endpoints is fail-soft and verified against live services
  in staging; the façade logic is proven in tests via a deterministic fake + a real-PG idempotency test.
