// NFR-001: primary screen loads p95 ≤ 1.5 s / p99 ≤ 3 s. NFR-004: indexed search p95 ≤ 700 ms.
// Approvals worklist, order provider queue, beneficiary search — the read-heavy screens.
import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';
import { BASE_URL, authParams, stages, pickBeneficiaryMemberNo } from './lib/common.js';

const screenDur = new Trend('primary_screen_duration', true);
const searchDur = new Trend('indexed_search_duration', true);

export const options = {
  scenarios: { worklists: { executor: 'ramping-vus', startVUs: 0, stages: stages(120, 10) } },
  thresholds: {
    primary_screen_duration: ['p(95)<1500', 'p(99)<3000'], // NFR-001
    indexed_search_duration: ['p(95)<700'],                // NFR-004
    http_req_failed: ['rate<0.01'],
  },
};

export default function () {
  const worklist = http.get(
    `${BASE_URL}/api/v1/authorizations?status=Pending&page=1&pageSize=25`,
    authParams({ tags: { name: 'approvals-worklist' } }),
  );
  check(worklist, { 'worklist 200': (r) => r.status === 200 });
  screenDur.add(worklist.timings.duration);

  const queue = http.get(
    `${BASE_URL}/api/v1/investigation-orders?status=Ordered&page=1&pageSize=25`,
    authParams({ tags: { name: 'provider-queue' } }),
  );
  check(queue, { 'queue 200': (r) => r.status === 200 });
  screenDur.add(queue.timings.duration);

  const memberNo = pickBeneficiaryMemberNo(__ITER + __VU * 100000);
  const bene = http.get(
    `${BASE_URL}/api/v1/beneficiaries?query=${memberNo}&pageSize=10`,
    authParams({ tags: { name: 'beneficiary-search' } }),
  );
  check(bene, { 'bene search 200': (r) => r.status === 200 });
  searchDur.add(bene.timings.duration);
}
