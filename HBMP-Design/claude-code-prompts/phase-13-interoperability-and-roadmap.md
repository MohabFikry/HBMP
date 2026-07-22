# Phase 13 — Interoperability & Roadmap Readiness (R6+)

**Goal:** Ship the production **FHIR R4 façade** over the internal models and the **integration-readiness surface** (outbound/inbound adapters + anti-corruption layer) that lets future partners — UNHCR, government, insurers, HL7 networks, digital referral — attach without re-architecting the core. Every external path stays behind an interface, respects minimum-necessary + audit, and is gated by a **DPIA + data-sharing agreement** before it can go live.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

---

## Skills to activate
> Activate `fhir-integration-architect` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management`. Skill files: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first

Open these before coding:

- [../17-api-specifications.md](../17-api-specifications.md) §12 **FHIR R4 Alignment** — the canonical HBMP↔FHIR mapping table and §12.1 status mapping; note "a read-only FHIR façade (`/fhir/r4/*`) can be layered later; native `/api/v1` remains primary." Mapping lives at the adapter layer; internal storage stays relational.
- [../16-service-architecture.md](../16-service-architecture.md) — service-oriented core designed so claims/PBM/inventory/**integrations attach later without re-platforming**; External Registries context (UNHCR identity references, batch validation).
- [../35-implementation-plan.md](../35-implementation-plan.md) §10 **Roadmap beyond v1 (R6+)** — sequenced additive trains incl. FHIR/HL7 interoperability, UNHCR/government/insurer integrations, digital referral network, OCR + Arabic NLP. The core is designed so each is additive; governance gate applies.
- [../20-compliance-checklist.md](../20-compliance-checklist.md) §6 — **DPIA triggers: "any new integration (UNHCR/gov/insurer)" always requires a DPIA**; §5 cross-border (PDPL Law 151/2020) rules; data-sharing agreements + legal sign-off before go-live.
- [../11-permission-matrix.md](../11-permission-matrix.md) — min-necessary + field-level scoping (FHIR reads must respect it); [../18-security-model.md](../18-security-model.md) — scopes/OAuth; [../19-audit-strategy.md](../19-audit-strategy.md) — immutable hash-chained audit incl. `data.export`.
- [../26-testing-strategy.md](../26-testing-strategy.md) — contract testing approach (Pact) and conformance testing.
- [../01-product-vision.md](../01-product-vision.md) (Future Roadmap notes) — extensibility intent.

Depends on all core services being live (this is a façade + adapter layer over them). Introduces **no** new source of truth: FHIR and adapters map/translate; they never own data.

---

## Prompts

### 13.1 — FHIR R4 façade over internal models (versioned, authz-scoped, audited)

```text
Build a fhir-facade service (.NET 8, bounded context `interop`, base path `/fhir/r4`) exposing FHIR R4 resources mapped from internal models per ../17 §12. It is an ADAPTER: it reads/writes through existing service APIs/read-models, owns no clinical data, and stores nothing relational of its own beyond mapping/idempotency metadata. Read ../17 §12 + §12.1, ../11, ../18, ../19 first.

RESOURCES & MAPPING (exactly per ../17 §12 table)
- Patient ↔ beneficiary (+identifiers as Patient.identifier with system per type: NationalID/Passport/RefugeeID/UNHCRNo/MemberNo; name/birthDate/gender/telecom/address).
- Coverage ↔ policy + coverage + coverage_limit (beneficiary→Patient, payor→Mersal/sponsor, class, costToBeneficiary, limits as extensions).
- ServiceRequest ↔ investigation_order / order_line (code CPT/LOINC, quantityQuantity, status via §12.1 mapping, subject, requester); referral ↔ ServiceRequest(intent=order, category=referral).
- MedicationRequest ↔ prescription / prescription_line (medicationReference→Medication, dosageInstruction, dispenseRequest.quantity, status).
- DiagnosticReport (+ Observation) ↔ order_fulfillment + result (references ServiceRequest, presentedForm).
- Encounter ↔ encounter; Condition ↔ diagnosis; Observation ↔ vital (LOINC, valueQuantity); AllergyIntolerance ↔ allergy (code/reaction/criticality).

BEHAVIOUR
- Implement READ + search for all resources; implement WRITE only where sensible and safe (ServiceRequest/referral create, MedicationRequest create, Observation/AllergyIntolerance create) — writes translate to the owning service's native command; reject writes to derived/immutable resources (e.g., DiagnosticReport) with an OperationOutcome.
- Version: capabilities advertise FHIR R4; the façade is independently versioned from `/api/v1`, which remains primary.
- AuthZ: every interaction requires an OAuth scope (e.g., `fhir:read:Patient`, `fhir:write:ServiceRequest`) AND passes through the SAME RBAC/ABAC + field-level minimum-necessary rules as native APIs (../11). A caller who cannot read a diagnosis natively cannot read Condition via FHIR. SMART-on-FHIR-style scopes acceptable.
- Errors as FHIR `OperationOutcome`; searches return `Bundle`. Idempotent create via `If-None-Exist`.
- Audit: every read/write/search writes a hash-chained audit event (actor, resource, ids, fields) via the shared client (../19); exports/bulk are high-severity.

ACCEPTANCE
- Given an authorized client with `fhir:read:Patient`, When it GETs /fhir/r4/Patient/{id}, Then it receives a valid R4 Patient mapped from beneficiary, and a PHI-read audit event is written.
- Given a client whose native role cannot read diagnoses (e.g., Finance), When it GETs /fhir/r4/Condition, Then 403/empty per policy — the façade does not bypass min-necessary.
- Given a ServiceRequest create, When posted, Then it is translated to a native investigation order and the resulting resource round-trips.

Ship: EF migration (mapping/idempotency only), FHIR CapabilityStatement, OpenAPI/annotations, unit + integration + authz tests (prove min-necessary parity with native), README/ADR. No core service is modified beyond additive read/command endpoints already present.
```

### 13.2 — Integration adapters + anti-corruption layer (UNHCR / gov / insurer / HL7 / referral; OCR + Arabic-NLP stubs)

```text
Add an integration-readiness layer to the interop context: a uniform outbound/inbound adapter pattern + anti-corruption layer (ACL) so future partners attach WITHOUT touching core services. Read ../16 (extensibility), ../35 §10 (roadmap), ../20 §6 (DPIA gate) first. Build the interfaces + stubs now; real partner wiring is a later, DPIA-gated release.

ADAPTER PATTERN + ACL
- Define interfaces: `IOutboundIntegrationAdapter` (push HBMP events/data to a partner) and `IInboundIntegrationAdapter` (ingest partner data into HBMP), plus `IExternalPartnerRegistry` describing each partner (id, direction, transport, enabled flag, DPIA status).
- Each adapter sits behind an ACL that translates between the partner's model and internal domain models — the core NEVER depends on a partner schema. Inbound data lands in a quarantine/staging store, is validated + mapped by the ACL, then emitted as internal domain events; nothing writes core tables directly.
- Outbound adapters subscribe to the existing outbox/event stream (no new coupling in producers) and map to the partner format.
- A feature-flag + `DpiaGate` policy: an adapter is `Disabled` until (a) DPIA sign-off and (b) a data-sharing agreement are recorded (../20 §6). Attempting to enable without both is refused and audited.

STUB IMPLEMENTATIONS (behind the interfaces, non-functional placeholders)
- UNHCR identifier-validation adapter (batch validation of RefugeeID/UNHCRNo per ../16 External Registries) — stub returns "not enabled / DPIA pending".
- Government + insurer claim/eligibility adapters — stubs.
- HL7 v2 / FHIR referral (digital referral network) inbound+outbound — stubs mapping to internal referral/ServiceRequest via the ACL.
- Document-OCR ingestion hook and Arabic-NLP extraction hook — define `IDocumentOcrProvider` and `IArabicNlpExtractor` interfaces with no-op/stub implementations so ingestion pipelines can be added later without redesign (../35 §10).

ACCEPTANCE
- Given a new partner, When an engineer adds an adapter, Then they implement the interface + ACL mapping only — no core service changes.
- Given an inbound message, When received, Then it lands in staging, is mapped by the ACL, and emits internal events; a malformed message is quarantined, never written to core tables.
- Given an adapter without DPIA + data-sharing agreement recorded, When someone tries to enable it, Then the DpiaGate refuses (with reason) and audits the attempt.

Ship: the interfaces, stub adapters, ACL + staging model, DpiaGate policy, unit tests (ACL mapping, gate refusal), README/ADR documenting the extension recipe for a new partner.
```

### 13.3 — Interop test harness: contract tests + FHIR conformance + DPIA gate reminder

```text
Build an interop test harness that proves the façade and adapters are correct and safe to extend. Read ../26 (testing), ../20 §6 first.

CONTRACT TESTS
- Consumer-driven contract tests (Pact or equivalent) for each outbound adapter against a partner contract fixture, and for inbound ACL mappings against sample partner payloads. Failing a contract fails CI.

FHIR CONFORMANCE
- A sample FHIR R4 conformance check: validate representative resources (Patient, Coverage, ServiceRequest, MedicationRequest, Condition, Observation, AllergyIntolerance) against the R4 StructureDefinitions/profiles; publish and verify the CapabilityStatement lists exactly the supported interactions. Include a min-necessary parity test: a role that cannot read a field natively cannot read it via FHIR.

DPIA / DATA-SHARING GATE (governance, enforced in CI + runtime)
- Add a machine-checkable gate: no external integration can be marked `Enabled` in any environment unless a DPIA sign-off record and a data-sharing agreement reference exist for it (../20 §6 — "any new integration (UNHCR/gov/insurer) requires a DPIA"; §5 cross-border PDPL posture). CI test asserts every enabled adapter has both artifacts; a runtime pre-flight refuses enablement otherwise. Emit a visible reminder in docs and in the adapter registry UI/log.

ACCEPTANCE
- Given the façade, When conformance runs, Then the sample resources validate against R4 and the CapabilityStatement matches implemented interactions.
- Given an adapter contract change, When it breaks the partner contract, Then CI fails.
- Given an adapter marked enabled without DPIA + data-sharing agreement, When the gate test/pre-flight runs, Then it fails/refuses with a clear message.

Ship: Pact contracts + broker config or fixtures, FHIR validator harness, the DPIA-gate CI test + runtime pre-flight, README documenting the "before you enable an integration" checklist.
```

---

## Guardrails

- **No external integration goes live without DPIA + data-sharing agreement.** The `DpiaGate` refuses enablement (CI + runtime) until both artifacts exist for that partner (../20 §6); cross-border processing honours PDPL posture (../20 §5). Enablement attempts are audited.
- **FHIR respects min-necessary + audit.** The façade reuses the SAME RBAC/ABAC + field-level rules as native APIs (../11) — it is never a bypass. Every FHIR interaction is hash-chain audited (../19); Finance still cannot reach Condition/diagnosis.
- **Adapters isolated behind interfaces + ACL.** The core depends on no partner schema; inbound data is quarantined/mapped before emitting internal events; outbound rides the existing outbox. Adding a partner = implement the interface + mapping only, no core redesign (../16, ../35 §10).
- **Façade owns no data.** FHIR and adapters translate over existing services; internal relational storage stays the source of truth (../17 §12).
- **Additive, versioned.** `/fhir/r4` is independently versioned; native `/api/v1` remains primary and unchanged.

## Done when

- The FHIR R4 façade serves the core resources (Patient, Coverage, ServiceRequest, MedicationRequest, DiagnosticReport, Encounter, Condition, Observation, AllergyIntolerance) with OAuth scopes, min-necessary parity, and full audit; safe writes translate to native commands.
- Integration adapter interfaces + ACL + stubs exist for UNHCR / government / insurer / HL7 / digital-referral, plus OCR and Arabic-NLP ingestion hooks — all disabled behind the DPIA gate, addable without touching core services.
- Contract tests, a sample FHIR conformance check, and the DPIA/data-sharing gate all run green in CI; the "before you enable an integration" checklist is documented.
- All acceptance criteria pass; unit/integration/authz/contract/conformance tests green; OpenAPI + CapabilityStatement + README/ADR updated. Global Definition of Done met.
