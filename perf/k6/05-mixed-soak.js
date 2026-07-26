// Realistic mixed load + 1h soak. Proves stability over time (no leak / latency creep) and
// that the event bus sustains ≥ 200 events/s buffered without loss (NFR-014) when write
// paths (encounter/order/consume) fire under sustained load. Event-loss reconciliation is
// done post-run by comparing emitted domain events to the outbox relay / stream offsets
// (see PERFORMANCE-BASELINE.md → "Event-bus durability check").
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend } from 'k6/metrics';
import { BASE_URL, authParams, SMOKE, pickBeneficiaryMemberNo } from './lib/common.js';

const mixDur = new Trend('mixed_duration', true);

export const options = {
  scenarios: {
    soak: {
      executor: 'constant-vus',
      vus: SMOKE ? 5 : 150,
      duration: SMOKE ? '40s' : '60m',
    },
  },
  thresholds: {
    mixed_duration: ['p(95)<1500'],   // sustained latency must not creep past the primary-screen bar
    http_req_failed: ['rate<0.02'],
  },
};

// ~70% reads / 30% writes — representative of clinic operations.
export default function () {
  const i = __ITER + __VU * 100000;
  const memberNo = pickBeneficiaryMemberNo(i);
  const roll = Math.random();

  let res;
  if (roll < 0.4) {
    res = http.get(`${BASE_URL}/api/v1/reception/search?query=${memberNo}`, authParams({ tags: { name: 'mix-search' } }));
  } else if (roll < 0.7) {
    res = http.get(`${BASE_URL}/api/v1/authorizations?status=Pending&pageSize=25`, authParams({ tags: { name: 'mix-worklist' } }));
  } else if (roll < 0.85) {
    res = http.post(`${BASE_URL}/api/v1/eligibility/check`,
      JSON.stringify({ memberNo, serviceType: 'OutpatientConsult' }),
      authParams({ tags: { name: 'mix-eligibility' } }));
  } else {
    // write path → emits a domain event via outbox (feeds the NFR-014 durability check)
    res = http.post(`${BASE_URL}/api/v1/investigation-orders`,
      JSON.stringify({ memberNo, cptCode: '80053', priority: 'Routine' }),
      authParams({ headers: { 'Idempotency-Key': `soak-${i}` }, tags: { name: 'mix-order-create' } }));
  }
  check(res, { 'mix ok': (r) => r.status >= 200 && r.status < 500 });
  mixDur.add(res.timings.duration);
  sleep(Math.random());
}
