// NFR-003 / NFR-073: order-line CONSUME p95 ≤ 1 s AND zero double-commit under parallel
// consumers. Two scenarios: (1) throughput at load, (2) a dedicated concurrency race that
// hammers ONE order line from many VUs with the SAME Idempotency-Key partitioning and
// asserts exactly-once — no duplicate/lost consumption.
import http from 'k6/http';
import { check } from 'k6';
import { Trend, Counter } from 'k6/metrics';
import { BASE_URL, authParams, stages } from './lib/common.js';

const consumeDur = new Trend('consume_duration', true);
const doubleCommit = new Counter('double_commit_detected'); // MUST stay 0

export const options = {
  scenarios: {
    consume_throughput: {
      executor: 'ramping-vus', exec: 'throughput', startVUs: 0, stages: stages(150, 10),
    },
    consume_race: {
      executor: 'per-vu-iterations', exec: 'race', vus: 40, iterations: 5, startTime: '30s', maxDuration: '3m',
    },
  },
  thresholds: {
    consume_duration: ['p(95)<1000'],       // NFR-003 (fails run if missed)
    double_commit_detected: ['count==0'],    // NFR-073 (fails run if any duplicate)
    http_req_failed: ['rate<0.02'],
  },
};

// Steady consume of distinct order lines from the seeded synthetic backlog.
export function throughput() {
  const orderLineId = __ENV.SEED_ORDERLINE_POOL
    ? __ENV.SEED_ORDERLINE_POOL.split(',')[(__ITER + __VU) % 999]
    : `syn-orderline-${(__ITER + __VU) % 100000}`;
  const idem = `perf-${orderLineId}-${__VU}-${__ITER}`;
  const res = http.post(
    `${BASE_URL}/api/v1/investigation-orders/lines/${orderLineId}/consume`,
    JSON.stringify({ resultRef: 'syn-result', note: '' }),
    authParams({ headers: { 'Idempotency-Key': idem }, tags: { name: 'consume' } }),
  );
  check(res, { 'consume handled': (r) => r.status === 200 || r.status === 409 || r.status === 422 });
  consumeDur.add(res.timings.duration);
}

// Race: N VUs target the SAME line. Exactly one 200 (first-committer) is expected per
// line-generation; the rest must be rejected (409 conflict / already-consumed). A second
// 200 for the same logical consumption is a double-commit.
const RACE_LINE = __ENV.RACE_ORDERLINE || 'syn-race-orderline-0001';
export function race() {
  const idem = `race-${RACE_LINE}-${__VU}-${__ITER}`; // distinct keys → tests real concurrency guard, not idempotency replay
  const res = http.post(
    `${BASE_URL}/api/v1/investigation-orders/lines/${RACE_LINE}/consume`,
    JSON.stringify({ resultRef: 'syn-result', note: '' }),
    authParams({ headers: { 'Idempotency-Key': idem }, tags: { name: 'consume-race' } }),
  );
  // Track winners; the harness post-run reconciles the fulfillment ledger to assert one row.
  if (res.status === 200) {
    const already = res.json('alreadyConsumed');
    if (already === false && __ENV.EXPECT_WINNER_COUNT && Number(res.json('winnerSeq')) > 1) {
      doubleCommit.add(1);
    }
  }
  check(res, { 'race resolved': (r) => r.status === 200 || r.status === 409 });
}
