// NFR-006: operational reports p95 ≤ 3 s (heavy analytics run async and are excluded here).
// Reads the reporting-service KPI/dashboard endpoints that back the Director portal.
import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';
import { BASE_URL, authParams, stages } from './lib/common.js';

const reportDur = new Trend('operational_report_duration', true);

export const options = {
  scenarios: { dashboards: { executor: 'ramping-vus', startVUs: 0, stages: stages(60, 8) } },
  thresholds: {
    operational_report_duration: ['p(95)<3000'], // NFR-006
    http_req_failed: ['rate<0.01'],
  },
};

const REPORTS = [
  '/api/v1/reports/approval-tat?window=7d',
  '/api/v1/reports/pending-approvals',
  '/api/v1/reports/consume-throughput?window=24h',
  '/api/v1/reports/no-show?window=30d',
  '/api/v1/dashboards/executive',
];

export default function () {
  const path = REPORTS[(__ITER + __VU) % REPORTS.length];
  const res = http.get(`${BASE_URL}${path}`, authParams({ tags: { name: path.split('?')[0] } }));
  check(res, { 'report 200': (r) => r.status === 200 });
  reportDur.add(res.timings.duration);
}
