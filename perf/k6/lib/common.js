// Shared k6 config, auth and helpers for the Mersal HBMP perf suite (Phase 11.1).
// No PHI: all identifiers are synthetic and drawn from the seeded masked dataset.
import http from 'k6/http';
import { check } from 'k6';

export const BASE_URL = __ENV.BASE_URL || 'http://localhost:8000';
export const SMOKE = (__ENV.SMOKE || '') === '1';

// Full profile vs. smoke profile. Smoke proves the script + thresholds wire up on a
// laptop/CI; the full profile is what docs/PERFORMANCE-BASELINE.md is measured with.
export function stages(peakVus, holdMinutes) {
  if (SMOKE) return [{ duration: '10s', target: 3 }, { duration: '20s', target: 3 }, { duration: '5s', target: 0 }];
  return [
    { duration: '2m', target: Math.ceil(peakVus / 2) }, // ramp
    { duration: `${holdMinutes}m`, target: peakVus },   // hold at peak
    { duration: '1m', target: 0 },                      // ramp down
  ];
}

// Client-credentials token, cached per VU-init. Falls back to a supplied BEARER for local
// smoke runs. Never logs the token.
let cachedToken = null;
export function bearer() {
  if (__ENV.BEARER) return __ENV.BEARER;
  if (cachedToken) return cachedToken;
  const url = __ENV.OIDC_TOKEN_URL;
  if (!url) throw new Error('Set OIDC_TOKEN_URL + OIDC_CLIENT_ID/SECRET, or BEARER for smoke runs.');
  const res = http.post(url, {
    grant_type: 'client_credentials',
    client_id: __ENV.OIDC_CLIENT_ID,
    client_secret: __ENV.OIDC_CLIENT_SECRET,
    scope: __ENV.OIDC_SCOPES || 'reception:search eligibility:check orders:consume',
  }, { headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, tags: { name: 'oidc-token' } });
  check(res, { 'token 200': (r) => r.status === 200 });
  cachedToken = res.json('access_token');
  return cachedToken;
}

export function authParams(extra) {
  return {
    headers: {
      Authorization: `Bearer ${bearer()}`,
      'Content-Type': 'application/json',
      'Accept-Language': 'en',
      ...(extra && extra.headers ? extra.headers : {}),
    },
    tags: extra && extra.tags ? extra.tags : {},
  };
}

// Deterministic synthetic id space matching data-gen/generate.mjs (SEED-derived, masked).
// These are opaque synthetic keys — not real MRNs/national IDs.
export const SYNTH = {
  beneficiaries: Number(__ENV.SYNTH_BENEFICIARIES || (SMOKE ? 1000 : 1_000_000)),
  encounters: Number(__ENV.SYNTH_ENCOUNTERS || (SMOKE ? 5000 : 10_000_000)),
};
export function pickBeneficiaryMemberNo(i) {
  const n = (i % SYNTH.beneficiaries) + 1;
  return `SYN-M-${String(n).padStart(9, '0')}`; // synthetic member number
}
