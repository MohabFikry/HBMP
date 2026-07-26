# Runbook: <title>

> Template per `34-technical-documentation.md`. Every runbook has: trigger, impact, checklist,
> recovery, post-incident, escalation.

- **Trigger:** what alert/condition starts this (link the Prometheus alert).
- **Impact / severity:** who/what is affected; user-visible symptoms.
- **Owner / on-call:** rotation + escalation path.

## Diagnosis checklist
1. …

## Recovery steps
1. …

## Verification
- How you confirm resolution (metric back under SLO, queue drained, chain intact).

## Post-incident
- Timeline, root cause, follow-up actions, ADR if a design change is needed.

## Escalation
- L1 → L2 → engineering owner → security/DPO (if PHI or breach suspected).
