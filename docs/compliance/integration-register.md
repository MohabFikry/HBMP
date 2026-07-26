# External Integration Register + DPIA Gate (Phase 13.2 / 13.3)

> **Before you enable an integration, read this.** No external integration goes live in ANY environment without
> **(a)** a signed-off DPIA and **(b)** a recorded data-sharing agreement for that partner
> (`20-compliance-checklist.md §6` — *"any new integration (UNHCR/gov/insurer) always requires a DPIA"*).
> Cross-border partners additionally honour the PDPL (Law 151/2020) posture (§5). The gate is enforced in THREE
> places: this register + CI (`tools/ci/check-integration-dpia.py`), the runtime registry
> (`DpiaGate.CanEnable` on `POST /interop/integration/partners/{id}/enable`), and a DB `CHECK` constraint on
> `interop.integration_partner`. Every enablement attempt — allowed or refused — is hash-chain audited.

## The checklist (per partner, in order)

1. **DPIA** — complete + sign off the Data Protection Impact Assessment for this data flow (lawful basis, data
   minimization, residency/cross-border, retention, subject rights, risk treatment).
2. **Data-sharing agreement** — execute the agreement with the partner; record its reference (contract id / doc).
3. **ACL** — implement the adapter + anti-corruption mapping (partner-model ↔ internal domain events); no core
   service changes (see `services/interop/README.md` extension recipe).
4. **Enable** — record the DPIA + agreement (`POST …/dpia`), then enable (`POST …/enable`). The gate keeps the
   partner `Disabled` until both artifacts exist.

## Register

Update the **Status** column ONLY together with the DPIA + agreement columns — CI fails the build if any
`Enabled` row is missing a `SignedOff` DPIA or an agreement reference.

| Partner ID | Name | Status | DPIA | Data-Sharing Agreement | Cross-Border |
|---|---|---|---|---|---|
| digital-referral-network | Digital Referral Network (FHIR) | Disabled | NotStarted | — | No |
| hl7v2-referral | Digital Referral Network (HL7 v2) | Disabled | NotStarted | — | No |
| unhcr-identity | UNHCR Identifier Validation | Disabled | NotStarted | — | Yes |
| government-claims | Government Claims / Eligibility | Disabled | NotStarted | — | No |
| insurer-eligibility | Insurer Claims / Eligibility | Disabled | NotStarted | — | No |

All partners ship **Disabled / DPIA-pending** — the v1 platform exposes the interop *surface* (FHIR façade +
adapter interfaces + ACL + DPIA gate) but activates no external data flow. Each is a later, DPIA-gated release.

## Status/DPIA vocabulary

- **Status:** `Disabled` | `Enabled`
- **DPIA:** `NotStarted` | `InProgress` | `SignedOff`
- **Data-Sharing Agreement:** a reference (e.g. `DSA-2026-001`) or `—` when none is on file.
