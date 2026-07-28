// Phase 20 (build prompt 20.5) — the unified patient profile's two budgets.
//
//   full profile     p95 < 2500 ms — it fans out to ~8 services in parallel.
//   context bar      p95 <  400 ms — it renders on EVERY clinical screen, so it cannot be slow.
//
// The second budget is the operationally important one, and it is why the endpoint takes a `?sections=`
// subset at all: the context bar asks for header+alerts and nothing else. A regression that made it fetch
// the whole profile would still be CORRECT — the matrix still decides what comes back — and would quietly
// add seconds to every encounter, order, dispense and approval screen in the platform. Correctness tests
// cannot catch that. This can.
//
// Deliberately NOT solved with a cache. The composition depends on role, treating relationship, branch,
// payer scope and live grants; a cache keyed on fewer dimensions than the decision depends on is a breach
// rather than a bug (the phase-18 X9 lesson, restated in ADR-0026).
import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';
import { BASE_URL, authParams, stages, pickBeneficiaryMemberNo } from './lib/common.js';

const profileDur = new Trend('patient_profile_duration', true);
const contextBarDur = new Trend('patient_context_bar_duration', true);

export const options = {
  scenarios: { profile: { executor: 'ramping-vus', startVUs: 0, stages: stages(120, 10) } },
  thresholds: {
    patient_profile_duration: ['p(95)<2500'],      // design 39 / build prompt 20.5
    patient_context_bar_duration: ['p(95)<400'],   // it is on every clinical screen
    http_req_failed: ['rate<0.01'],
  },
};

/**
 * Resolve a synthetic member number to a beneficiary id the way a real user does — by searching.
 *
 * The generator seeds member NUMBERS (SYN-M-*); the surrogate uuid is assigned on load, so it cannot be
 * derived here. The search is deliberately NOT counted toward either budget: it is a different screen with
 * its own NFR-004 target, already measured in 03-worklists.js.
 */
function resolveBeneficiaryId(iteration) {
  const memberNo = pickBeneficiaryMemberNo(iteration);
  const res = http.get(
    `${BASE_URL}/api/v1/reception/search?q=${encodeURIComponent(memberNo)}`,
    authParams({ tags: { name: 'profile-id-lookup' } }),
  );
  if (res.status !== 200) return null;
  try {
    const results = res.json('results') || [];
    return results.length > 0 ? results[0].identity.beneficiaryId : null;
  } catch {
    return null;
  }
}

export default function () {
  const id = resolveBeneficiaryId(__ITER);
  if (!id) return; // no seeded member for this iteration — do not pollute the budgets with a 404

  // The full profile — every section the caller's matrix row grants.
  const full = http.get(
    `${BASE_URL}/api/v1/patients/${id}/profile`,
    authParams({ tags: { name: 'patient-profile-full' } }),
  );
  check(full, {
    'profile 200': (r) => r.status === 200,
    // A profile that answered fast because every section degraded to Unavailable is not a passing result —
    // it is the failure mode this whole phase spends three states distinguishing.
    'profile served at least one section': (r) => {
      try { return (r.json('sections') || []).length > 0; } catch { return false; }
    },
  });
  profileDur.add(full.timings.duration);

  // The context bar — header + alerts only.
  const bar = http.get(
    `${BASE_URL}/api/v1/patients/${id}/profile?sections=header,alerts`,
    authParams({ tags: { name: 'patient-context-bar' } }),
  );
  check(bar, {
    'context bar 200': (r) => r.status === 200,
    // Asserting the SUBSET, not just the latency: if this ever returns the full profile the timing
    // regression is the symptom, and the subset is the cause.
    'context bar is a subset': (r) => {
      try { return (r.json('sections') || []).length <= 2; } catch { return false; }
    },
  });
  contextBarDur.add(bar.timings.duration);
}
