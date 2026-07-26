# 12. Sensitive-result disclosure + report-access grants

Date: 2026-07-26
Status: Accepted
Phase: 14.7 / 16.6 (H4)

## Context

Some investigation results are sensitive (mental-health, and other non-Standard sensitivity levels). Design
37 §6 is explicit: a sensitive result's **content** must not be visible to the medical-approval team's
standing EMR oversight, nor to case managers — only its **existence**. The audit (H4) found this was enforced
on orders-service direct reads but the approvals review aggregation was a side channel around it.

## Decision

1. **One disclosure rule** — `libs/authz/SensitiveDisclosure.IsRestricted(level, callerHasAccess)`: a
   non-Standard item is content-restricted unless the caller has access. orders-service's
   `SensitiveResultGate.Decide` follows the same logic; there is a single semantic definition.
2. **Access = author OR active grant.** Full content is disclosed only to the authoring/ordering clinician,
   or the holder of an active `report_access_grant` — single-result-scoped, time-boxed (72h Sensitive / 24h
   HighlySensitive), revocable. The grant is requested with a purpose + mandatory justification and decided
   by the author or a Medical Director (dual-path so care isn't blocked when the author is away).
3. **Restricted = existence metadata only:** category, date, status, branch, a RESTRICTED marker — never
   values, never a fetchable document/report ref.
4. **Enforced at every disclosure point, not just the source.** orders gates direct reads; the approvals
   `ReviewView` projection re-applies the rule on the oversight aggregation (defense in depth) so no
   aggregation can leak around the source gate. Clinical-context items carry `SensitivityLevel` +
   `CallerHasAccess` (stamped by the data owner for the specific caller).
5. **Read-under-grant is a distinct HIGH-ish audit event** (grant id + purpose + actor + result ref),
   separate from an ordinary result read.

## Consequences

- The approval team can see a sensitive result *exists and is pending* (enough to make an authorization
  decision) without seeing its content — and must request a justified, audited grant to see more.
- The rule lives in `libs/authz` so any future consumer (a new portal, an export) inherits it rather than
  re-deriving disclosure.
- The emr `/clinical-context` oversight aggregation stamps the sensitivity contract; emr's own records are
  Standard (the sensitive investigation results are orders-owned).
