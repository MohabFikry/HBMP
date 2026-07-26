# interop-service — FHIR R4 façade + interoperability layer (Phase 13)

Bounded context `interop`. Base path **`/fhir/r4`**. An **adapter** over the core services: it owns no clinical
data, reads/writes the internal model through native `/api/v1` endpoints under the caller's bearer token, maps
to/from FHIR R4, and stores only mapping/idempotency metadata. See `docs/adr/0016-fhir-facade-interop.md`.

## What it exposes (13.1)

FHIR R4 for the nine core resources (17-api-specifications §12):
`Patient`, `Coverage`, `ServiceRequest`, `MedicationRequest`, `DiagnosticReport`, `Encounter`, `Condition`,
`Observation`, `AllergyIntolerance`.

- **Read + search** for all nine (`GET /fhir/r4/{Resource}/{id}`, `GET /fhir/r4/{Resource}?patient={id}`;
  `Patient` also `?identifier=&name=`). Searches return a `Bundle`; errors are `OperationOutcome`.
- **Create** (translated to the owning service's native command) for `ServiceRequest`/referral,
  `MedicationRequest`, `Observation`, `AllergyIntolerance` only. Idempotent via `If-None-Exist` /
  `Idempotency-Key`. Derived/immutable resources reject POST with an `OperationOutcome`.
- `GET /fhir/r4/metadata` → `CapabilityStatement` advertising exactly the implemented interactions + SMART scopes.

## Minimum-necessary — never a bypass

Two layers (ADR-0016 §2):
1. **`InteropPolicies`** (`libs/authz`) — role + SMART scope + tenant per interaction, at the policy layer (every
   deny audited). The **role set per resource is the boundary** — e.g. Finance/Reception/Pharmacy/Lab are absent
   from `Condition`/`DiagnosticReport` reads → 403.
2. The **owning service** enforces field-level projection + record ABAC when the façade calls it under the
   caller's token.

Every interaction is hash-chain audited (`FhirAudit`, 19-audit-strategy).

## Layout

```
Domain/         Fhir (R4 JSON builders + status maps §12.1), Model (minimized source projections), Mapping (mappers + write translators)
Infrastructure/ InteropDbContext (interop schema: fhir_create idempotency ledger), IFhirDataSource + HttpFhirDataSource (native sibling calls), migrations
Api/            Program, InteropGate, FhirAudit, FhirCapability (single source of truth), FhirEndpoints, FhirResults
Tests/          FHIR mapping + write-translator + capability unit tests; endpoint tests (fake source + capturing audit); real-PG idempotency (INTEROP_TEST_DB)
```

## Build & test

```bash
./dotnet.sh build services/interop/Api/Mersal.Interop.Api.csproj
./dotnet.sh test  services/interop/Tests/Mersal.Interop.Tests.csproj          # unit + endpoint (DB-free)
INTEROP_TEST_DB="Host=localhost;Port=55432;Database=hbmp;Username=hbmp;Password=…" \
  ./dotnet.sh test services/interop/Tests/Mersal.Interop.Tests.csproj          # + real-PG idempotency
```

Min-necessary parity is also locked in `libs/authz/Tests/InteropPoliciesTests.cs`.

## Roadmap (13.2 / 13.3)

Integration adapters + anti-corruption layer (UNHCR/gov/insurer/HL7/referral, OCR + Arabic-NLP hooks), all
disabled behind the **DPIA gate** — no external integration goes live without a DPIA + data-sharing agreement
(20 §6), enforced in CI and at runtime.
