# Runbook: auth anomaly / suspected breach

- **Trigger:** `AuthAnomaly` (401/403 burst), `ErrorBudgetBurnFast`, or `AuditChainStalled` alert.
- **Impact:** possible credential stuffing, authz probing, or (chain stall) audit-integrity/tampering concern.
- **Owner / on-call:** security on-call + DPO (mandatory for suspected PHI breach).

## Diagnosis checklist
1. Golden-signal + auth dashboard: source IPs, targeted endpoints, roles, spike shape.
2. Correlate via OTel trace + `correlation_id` into the audit trail (audit-to-trace correlation, NFR-084).
3. Distinguish misconfig (a client with wrong scope) from attack (distributed 401s, BOLA attempts).

## Recovery steps
1. Attack: tighten Kong rate-limit/quota on the targeted route; confirm Keycloak brute-force lockout engaged; block offending IPs at ingress/ModSecurity.
2. Compromised account: revoke sessions/tokens (Keycloak), force re-auth + MFA reset.
3. `AuditChainStalled`: treat as **potential tampering** — freeze the audit partition, verify hash-chain
   linkage (`infra/dr/restore-rehearsal.sh` chain check), engage DPO; do not mutate audit data.

## Verification
- 401/403 rate back to baseline; chain advancing again with intact linkage; no unauthorized access confirmed.

## Post-incident (breach path)
- DPO-led: scope of exposure, notification obligations, RoPA/DPIA update, remediation ADR.

## Escalation
- Security on-call → DPO → incident commander. Any confirmed PHI exposure follows the breach-notification process in `20-compliance-checklist.md`.
