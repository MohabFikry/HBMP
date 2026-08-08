#!/usr/bin/env bash
# ============================================================================================================
# author-dev-benefit-rules.sh — give the dev plans a priced benefit configuration.
# ============================================================================================================
#
# WHAT WAS MISSING
# ----------------
# The seeded environment enrols 96 coverages across two plans, and NEITHER plan version has a single benefit
# rule on it. (The 232 CONSULT rules in the database belong to plan versions with no members.) So every
# cost-share quote answered "this plan version does not price this benefit category at the resolved tier" —
# correctly, because it does not. A counter could show what a prescription costs and never what the patient
# pays, for every member in the environment.
#
# THESE FIGURES ARE ILLUSTRATIVE DEV DATA, NOT MERSAL'S TARIFF
# ------------------------------------------------------------
# Nobody has told me what Mersal actually charges, and inventing a tariff and calling it real would be worse
# than the gap. What this script establishes is that the MECHANISM works end to end: catalogue price ×
# quantity → allowed amount → deductible/copay/coinsurance → member and payer share, through the same
# libs/benefit-pricing path a claim is adjudicated by. Replace the numbers below with the real ones when
# they exist; the shape will not change.
#
# WHY THROUGH THE API AND NOT SQL
# --------------------------------
# Benefit rules on an Active version are immutable by database trigger — "amend the plan to create a new
# version" — and that guard is right. This drives the governed path instead of forcing past it:
#
#   POST /plans/{id}/amend            → a Draft, cloning whatever is in force
#   PUT  /plan-versions/{id}/rules    → the rule set, wholesale (a partial edit is how a half-reviewed
#                                       version gets activated)
#   POST /plan-versions/{id}/validate → the dry run, so problems surface before the irreversible step
#   POST /plan-versions/{id}/activate → supersedes the predecessor, windows abutting exactly
#
# Members reach the new version because eligibility resolves the version IN FORCE ON THE SERVICE DATE rather
# than the one they enrolled under (eligibility migration 0005). Without that this script would author terms
# nobody could ever be quoted.
#
# Idempotent-ish: re-running creates another version. It refuses when a draft is already open, which is the
# API's guard, not this script's.
#
#   tools/dev/author-dev-benefit-rules.sh
# ============================================================================================================
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
GW="${GW:-http://localhost:8000}"

TOKEN="$("$REPO/tools/dev/dev-token.sh" policy_admin | tail -1)"
[ -n "$TOKEN" ] || { echo "no policy_admin token" >&2; exit 1; }
AUTH=(-H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json")

# The Active tiers, read rather than hard-coded: activation refuses a covered category that leaves any Active
# tier unpriced, so a stale id here would fail the whole amendment at the last step.
T1="$(curl -sf "${AUTH[@]}" "$GW/api/v1/network-tiers" | python3 -c "
import json,sys
rows=json.load(sys.stdin)
print(next(r['networkTierId'] for r in rows if not r['isOutOfNetwork'] and r['status']=='Active'))")"
OON="$(curl -sf "${AUTH[@]}" "$GW/api/v1/network-tiers" | python3 -c "
import json,sys
rows=json.load(sys.stdin)
print(next(r['networkTierId'] for r in rows if r['isOutOfNetwork'] and r['status']=='Active'))")"

echo "tiers: T1=$T1  OON=$OON"

rules_json() {
python3 - "$T1" "$OON" <<'PY'
import json, sys
t1, oon = sys.argv[1], sys.argv[2]

def tier(tid, *, copay_fixed=None, copay_pct=None, coins=None, preauth=None):
    return {
        "networkTierId": tid, "isCovered": True,
        "copayFixed": copay_fixed, "copayPercent": copay_pct, "coinsurancePercent": coins,
        "copayCountsTowardDeductible": False,
        "requiresPreauthOverride": preauth, "limitMultiplier": None,
    }

def rule(code, *, limit, deductible, waived, preauth, threshold, in_net, out_net, notes):
    return {
        "benefitCategoryCode": code, "isCovered": True,
        "limitType": "Annual", "limitValue": limit, "resetPeriod": "Yearly",
        "deductible": deductible, "deductibleWaived": waived,
        "waitingPeriodDays": 0,
        "requiresPreauth": preauth, "preauthCostThreshold": threshold,
        "exclusions": None, "notes": notes,
        "tiers": [in_net, out_net],
    }

print(json.dumps({"rules": [
    # Primary care: a flat co-pay in network, and the plan deductible waived — the usual shape, because a
    # deductible on the first consultation is what stops someone coming in early.
    rule("CONSULT", limit=50000, deductible=200, waived=True, preauth=False, threshold=None,
         in_net=tier(t1, copay_fixed=20), out_net=tier(oon, coins=40),
         notes="ILLUSTRATIVE dev figures — not Mersal's tariff."),
    # Medicines: coinsurance, so the member's share tracks the price of what they are actually dispensed.
    rule("PHARMACY", limit=15000, deductible=200, waived=False, preauth=False, threshold=None,
         in_net=tier(t1, coins=20), out_net=tier(oon, coins=50),
         notes="ILLUSTRATIVE dev figures — not Mersal's tariff."),
    rule("LAB", limit=20000, deductible=200, waived=False, preauth=False, threshold=None,
         in_net=tier(t1, coins=10), out_net=tier(oon, coins=40),
         notes="ILLUSTRATIVE dev figures — not Mersal's tariff."),
    # Imaging carries a pre-auth threshold: the expensive end of it is where authorization actually earns its
    # keep, and pricing it without one would make the gate unreachable from a quote.
    rule("IMAGING", limit=30000, deductible=200, waived=False, preauth=True, threshold=2000,
         in_net=tier(t1, coins=20), out_net=tier(oon, coins=50),
         notes="ILLUSTRATIVE dev figures — not Mersal's tariff."),
    # No deductible at all, so nothing to waive: `waived` records an EXEMPTION from a figure that exists,
    # and setting it without one is what the DB check refuses.
    rule("REFERRAL", limit=10000, deductible=None, waived=False, preauth=True, threshold=None,
         in_net=tier(t1, coins=0), out_net=tier(oon, coins=50),
         notes="ILLUSTRATIVE dev figures — not Mersal's tariff."),
]}))
PY
}

# The plans members are actually enrolled on, discovered rather than hard-coded.
PLANS="$(curl -sf "${AUTH[@]}" "$GW/api/v1/plans" | python3 -c "
import json,sys
print(' '.join(p['planId'] for p in json.load(sys.stdin)))")"

for PLAN in $PLANS; do
  MEMBERS="$(docker exec mersal-hbmp-postgres-1 psql -U hbmp -d hbmp -tAc "
    SELECT count(*) FROM policy.coverage c
    JOIN policy.plan_version pv ON pv.plan_version_id = c.source_plan_version_id
    WHERE pv.plan_id = '$PLAN';" | tr -d ' ')"
  [ "${MEMBERS:-0}" -gt 0 ] || continue

  # Already priced? Then leave it alone. Re-running should be safe, and an amendment that changes nothing is
  # still a new version in the plan's history — noise in the one record an auditor reads to see what changed.
  PRICED="$(curl -sf "${AUTH[@]}" "$GW/api/v1/plans/$PLAN/versions" | python3 -c "
import json,sys
vs=json.load(sys.stdin)
active=next((v for v in vs if v['status']=='Active'), None)
print(len(active['rules']) if active else 0)")"
  if [ "${PRICED:-0}" -ge 5 ]; then
    echo "── plan $PLAN ($MEMBERS coverages) — already priced, skipping"
    continue
  fi

  echo "── plan $PLAN ($MEMBERS coverages)"
  # Amend, or pick up the draft already open. The API refuses a second draft (DRAFT_EXISTS) — correctly, since
  # two open drafts on one plan is two people editing what members are entitled to. Re-running after a failure
  # halfway through should continue rather than dead-end, so we resume the one that is there.
  DRAFT="$(curl -s -X POST "${AUTH[@]}" "$GW/api/v1/plans/$PLAN/amend" \
    | python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('planVersionId',''))" 2>/dev/null || true)"
  if [ -z "$DRAFT" ]; then
    DRAFT="$(curl -sf "${AUTH[@]}" "$GW/api/v1/plans/$PLAN/versions" \
      | python3 -c "
import json,sys
print(next((v['planVersionId'] for v in json.load(sys.stdin) if v['status']=='Draft'), ''))")"
    [ -n "$DRAFT" ] || { echo "   no draft and none could be opened — skipping"; continue; }
    echo "   resuming open draft"
  fi
  echo "   draft $DRAFT"

  rules_json > /tmp/hbmp-rules.$$.json
  # NOT `curl -sf`. With `set -e` a refusal exits the script silently and the operator is left staring at the
  # last line that printed; the whole point of the validate step below is that problems are readable.
  RESP="$(curl -s -w '\n%{http_code}' -X PUT "${AUTH[@]}" "$GW/api/v1/plan-versions/$DRAFT/rules" \
    --data-binary @/tmp/hbmp-rules.$$.json)"
  rm -f /tmp/hbmp-rules.$$.json
  if [ "$(printf '%s' "$RESP" | tail -1)" != "200" ]; then
    echo "   rules refused: $(printf '%s' "$RESP" | head -n -1)"
    continue
  fi

  echo -n "   validate: "
  curl -sf -X POST "${AUTH[@]}" "$GW/api/v1/plan-versions/$DRAFT/validate" \
    | python3 -c "import json,sys; d=json.load(sys.stdin); print('OK' if d['valid'] else d['problems'])"

  echo -n "   activate: "
  ACT="$(curl -s -X POST "${AUTH[@]}" "$GW/api/v1/plan-versions/$DRAFT/activate")"
  printf '%s' "$ACT" | python3 -c "
import json,sys
d=json.load(sys.stdin)
print(d['status'], 'v'+str(d['versionNo']), 'from', d['effectiveFrom']) if 'status' in d and 'versionNo' in d else print('refused:', d)
" 2>/dev/null || echo "refused: $ACT"
done

echo "done."
