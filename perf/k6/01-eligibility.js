// NFR-002: eligibility check p95 ≤ 800 ms / p99 ≤ 1.5 s; NFR-004 reception search p95 ≤ 700 ms.
// Models reception search + eligibility bursts against Kong. Synthetic member numbers only.
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend } from 'k6/metrics';
import { BASE_URL, authParams, stages, pickBeneficiaryMemberNo } from './lib/common.js';

const eligibilityDur = new Trend('eligibility_duration', true);
const searchDur = new Trend('reception_search_duration', true);

export const options = {
  scenarios: {
    eligibility_bursts: { executor: 'ramping-vus', startVUs: 0, stages: stages(200, 10), gracefulRampDown: '30s' },
  },
  thresholds: {
    eligibility_duration: ['p(95)<800', 'p(99)<1500'], // NFR-002 (fails run if missed)
    reception_search_duration: ['p(95)<700'],          // NFR-004
    http_req_failed: ['rate<0.01'],
  },
};

export default function () {
  const i = __ITER + __VU * 100000;
  const memberNo = pickBeneficiaryMemberNo(i);

  const search = http.get(
    `${BASE_URL}/api/v1/reception/search?query=${memberNo}`,
    authParams({ tags: { name: 'reception-search' } }),
  );
  check(search, { 'search 200': (r) => r.status === 200 });
  searchDur.add(search.timings.duration);

  const check200 = http.post(
    `${BASE_URL}/api/v1/eligibility/check`,
    JSON.stringify({ memberNo, serviceType: 'OutpatientConsult' }),
    authParams({ tags: { name: 'eligibility-check' } }),
  );
  check(check200, { 'eligibility 2xx': (r) => r.status >= 200 && r.status < 300 });
  eligibilityDur.add(check200.timings.duration);

  sleep(Math.random() * 0.5);
}
