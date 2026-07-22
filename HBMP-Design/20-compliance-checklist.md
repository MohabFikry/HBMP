# 20 — Compliance Checklist

> Back to [00-README-INDEX.md](00-README-INDEX.md) · [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [18-security-model.md](18-security-model.md) · [19-audit-strategy.md](19-audit-strategy.md) · [11-permission-matrix.md](11-permission-matrix.md)

> ⚠️ **Not legal advice.** This is an engineering compliance design aid. Mersal must have qualified legal counsel and a Data Protection Officer validate applicability — especially for refugee data, which is high-risk special-category data. Mersal operates in Egypt, so **Egypt's Personal Data Protection Law (Law No. 151 of 2020, "PDPL")** is the primary statute; HIPAA and GDPR are applied as **design principles / best practice** (they are not directly binding on an Egyptian NGO unless it processes data of EU data subjects or contracts with covered entities), and UNHCR data-protection standards may apply where refugee data is shared with or sourced from UNHCR.

---

## 1. Applicable frameworks & why

| Framework | Applicability to Mersal | How we treat it |
|-----------|-------------------------|-----------------|
| **Egypt PDPL (Law 151/2020)** | Directly applicable — controller processing personal data in Egypt | Primary compliance target; consent, DPO, cross-border rules, breach notice to the Data Protection Centre. **On-prem hosting in Egypt keeps regulated refugee data in-country, so no cross-border transfer basis is normally required** (see §5 residency note and [0C-OPEN-SOURCE-STACK.md](0C-OPEN-SOURCE-STACK.md)). |
| **GDPR (EU 2016/679)** | Applicable *if* EU data subjects' data is processed or EU-funded partners require it | Adopted as the design baseline (strongest standard) |
| **HIPAA (US)** | Not directly binding unless partnering with US covered entities/BAAs | Adopted as security/privacy *principles* for health data |
| **UNHCR Data Protection Policy** | Applicable when integrating/sharing refugee data with UNHCR | Align identifiers, purpose limitation, data-sharing agreements |
| **ISO 27001 / 27799** | Voluntary but recommended | Target certification posture for controls |

---

## 2. Core data-protection principles → control mapping

| Principle | How the platform meets it | Reference | Owner | Status |
|-----------|---------------------------|-----------|-------|--------|
| **Lawfulness/consent** | Consent captured at registration; lawful-basis recorded per processing purpose | [15-database-erd.md](15-database-erd.md) (consent), [22-data-dictionary.md](22-data-dictionary.md) | DPO | ☐ |
| **Purpose limitation** | Data used only for care/benefit administration; purposes documented in RoPA | §5 below | DPO | ☐ |
| **Data minimization** | Field-level min-necessary enforced per role (reception≠EMR, finance≠diagnosis, etc.) | [11-permission-matrix.md](11-permission-matrix.md) | Security | ☐ |
| **Accuracy** | Edit + verification workflows; identifier verification status; history tables | [15](15-database-erd.md) | Product | ☐ |
| **Storage limitation** | Retention schedule + soft-delete + purge jobs; legal hold overrides | §6 below | DPO | ☐ |
| **Integrity & confidentiality** | AES-256 at rest, TLS/mTLS in transit, RBAC+ABAC, RLS | [18-security-model.md](18-security-model.md) | Security | ☐ |
| **Accountability** | Immutable audit, access reviews, RoPA, DPIA | [19-audit-strategy.md](19-audit-strategy.md) | DPO | ☐ |
| **Transparency** | Privacy notice in AR/EN at registration; beneficiary-facing rights info | Product | ☐ |

---

## 3. Data-subject / beneficiary rights

| Right | Platform capability | Status |
|-------|--------------------|--------|
| Access (copy of data) | Export beneficiary record via authorized DSAR workflow; access logged | ☐ |
| Rectification | Correct data with history/audit trail | ☐ |
| Erasure (where lawful) | Soft-delete + purge, subject to medical-record retention & legal hold | ☐ |
| Restriction/objection | Status flags to restrict processing; consent withdrawal | ☐ |
| Portability | Structured export (FHIR-aligned) where applicable | ☐ |
| Info on processing | Privacy notice, RoPA extract on request | ☐ |

DSAR requests are themselves audited; identity of requester verified before fulfilment.

---

## 4. HIPAA-principle checklist (security/privacy safeguards)

| Safeguard | Control | Status |
|-----------|---------|--------|
| Administrative | Role-based access, workforce training, sanction policy, access reviews | ☐ |
| Physical | On-prem server-room/facility controls (access control, environmental, locked racks) in Egypt; device management for staff; LUKS full-disk encryption on all hosts | ☐ |
| Technical — access control | Unique user IDs, MFA, automatic logoff (session timeout), encryption | ☐ |
| Technical — audit controls | Immutable audit of PHI access & changes | ☐ |
| Technical — integrity | Hash-chaining, checksums, no unauthorized alteration | ☐ |
| Technical — transmission security | TLS 1.2+/mTLS between services | ☐ |
| Breach notification | Incident response + notification runbook | ☐ |
| Minimum necessary | Field-level minimization per role | ☐ |

---

## 5. Records of Processing Activities (RoPA) — starter

| Processing activity | Data categories | Purpose | Lawful basis | Recipients | Retention |
|---------------------|-----------------|---------|--------------|-----------|-----------|
| Beneficiary registration | Identity, contact, family, documents | Eligibility & care | Consent/vital interest | Internal, contracted providers | Per medical-record schedule |
| Clinical encounters/EMR | Health data (special category) | Provide care | Consent/vital interest | Treating clinicians, approvals | Long-term medical retention |
| Orders/prescriptions | Health + service data | Fulfil care | Consent | Assigned providers only | Medical retention |
| Approvals | Health + cost data | Authorize services | Legitimate/consent | Approval team, provider | Medical + financial retention |
| Provider network | Provider org/contact | Manage network | Contract | Internal | Contract term + statutory |
| Notifications | Contact + minimal context | Inform beneficiary/provider | Consent | Beneficiary/provider | Short |

Cross-border / residency note: HBMP is **hosted on-prem in Egypt** (see [0C-OPEN-SOURCE-STACK.md](0C-OPEN-SOURCE-STACK.md)), which keeps regulated refugee data **in-country by default** and *simplifies* PDPL cross-border rules — no transfer basis is required for normal processing. Backups/DR copies stay on a **second on-prem site in Egypt**; if any processing/storage or DR copy ever leaves Egypt (e.g., a later cloud region), PDPL cross-border transfer rules and safeguards must first be satisfied and a transfer basis documented.

---

## 6. Retention & DPIA

**Retention schedule (to be finalized with counsel):**

| Data class | Retention (indicative) | Then |
|-----------|------------------------|------|
| Clinical/medical records | Long-term per Egyptian medical-record norms | Archive/anonymize |
| Benefit/authorization records | Medical + financial period | Archive |
| Financial/settlement | Statutory accounting period | Archive |
| Audit logs | ≥ retention of the data they describe; min 6–7 yrs | WORM archive |
| Notifications/transient | 30–90 days | Purge |
| Marketing/consent artifacts | Until withdrawn + proof period | Purge |

**DPIA triggers (must run a Data Protection Impact Assessment):** processing special-category refugee health data at scale (always), any new integration (UNHCR/gov/insurer), profiling/AI CDS, large data migration, cross-border transfer, new provider-data sharing. Each release that touches these must attach a DPIA sign-off before go-live ([35-implementation-plan.md](35-implementation-plan.md) governance gate).

---

## 7. Breach management
- Detection via monitoring/audit anomaly alerts ([19-audit-strategy.md](19-audit-strategy.md)).
- Documented incident response runbook: contain → assess → notify → remediate → review.
- PDPL/GDPR notification timelines tracked (e.g., authority notification without undue delay; GDPR 72h as the working target); beneficiary notification where high risk.
- Breach register maintained; post-incident review feeds risk register ([27-risk-assessment.md](27-risk-assessment.md)).

---

## 8. Compliance acceptance gate
- [ ] RoPA completed and approved by DPO.
- [ ] DPIA completed for special-category processing and each integration.
- [ ] Retention schedule configured and enforced by purge jobs.
- [ ] Privacy notice (AR/EN) published and shown at registration.
- [ ] Data-sharing agreements in place with each provider and any partner (UNHCR/gov).
- [ ] Breach runbook tested (tabletop exercise).
- [ ] Legal counsel sign-off on PDPL applicability & cross-border posture.

---

### Cross-references
- Security controls: [18-security-model.md](18-security-model.md) · Audit: [19-audit-strategy.md](19-audit-strategy.md)
- Minimization enforcement: [11-permission-matrix.md](11-permission-matrix.md) · Data classes: [22-data-dictionary.md](22-data-dictionary.md)
- Governance gate: [35-implementation-plan.md](35-implementation-plan.md) · Risks: [27-risk-assessment.md](27-risk-assessment.md)
