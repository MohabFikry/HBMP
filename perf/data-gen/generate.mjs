#!/usr/bin/env node
// Deterministic synthetic volume generator for the Mersal HBMP perf harness (Phase 11.1).
//
// Produces masked, seedable, volume-representative rows sized to NFR-012
// (default ≥ 1M beneficiaries, ≥ 10M encounters). Emits SYNTHETIC data ONLY:
//   - member numbers are opaque synthetic keys (SYN-M-*), NOT real MRNs/national IDs
//   - names are generated from a fixed non-real token list
//   - NO clinical free-text, NO real identifiers, NO PHI (NFR-042)
//
// Output is streamed as PostgreSQL COPY-friendly TSV to stdout (or --out file), one dataset
// per invocation (--dataset beneficiaries|encounters). Load with \copy into the staging DB's
// synthetic schema. Reproducible: same SEED ⇒ same rows.
//
// Usage:
//   node generate.mjs --dataset beneficiaries --count 1000000 --seed 42 > beneficiaries.tsv
//   node generate.mjs --dataset encounters   --count 10000000 --seed 42 > encounters.tsv

import { createWriteStream } from 'node:fs';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, arr) => {
    if (a.startsWith('--')) acc.push([a.slice(2), arr[i + 1]]);
    return acc;
  }, []),
);

const dataset = args.dataset || 'beneficiaries';
const seed = Number(args.seed ?? 42);
const count = Number(args.count ?? (dataset === 'encounters' ? 10_000_000 : 1_000_000));
const out = args.out ? createWriteStream(args.out) : process.stdout;

// Small deterministic PRNG (mulberry32) — reproducible masked data, no crypto needed.
function mulberry32(a) {
  return function () {
    a |= 0; a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const rnd = mulberry32(seed);

// Non-real token pools (deliberately synthetic — not drawn from any real person).
const FIRST = ['Alpha', 'Bravo', 'Charlie', 'Delta', 'Echo', 'Foxtrot', 'Golf', 'Hotel'];
const LAST = ['Xray', 'Yankee', 'Zulu', 'Quebec', 'Romeo', 'Sierra', 'Tango', 'Victor'];
const BRANCHES = ['BR-CAI', 'BR-ALX', 'BR-GIZ', 'BR-ASW'];
const SERVICE_TYPES = ['OutpatientConsult', 'LabTest', 'Imaging', 'Pharmacy', 'FollowUp'];

function pick(pool) { return pool[Math.floor(rnd() * pool.length)]; }
function pad(n, w) { return String(n).padStart(w, '0'); }

function writeBeneficiaries() {
  // cols: member_no, display_name, branch_code, dob(masked to year), synthetic_flag
  out.write('member_no\tdisplay_name\tbranch_code\tbirth_year\tsynthetic\n');
  for (let i = 1; i <= count; i++) {
    const memberNo = `SYN-M-${pad(i, 9)}`;
    const name = `${pick(FIRST)} ${pick(LAST)}`;
    const branch = pick(BRANCHES);
    const birthYear = 1950 + Math.floor(rnd() * 70); // year only — masked, no full DOB
    out.write(`${memberNo}\t${name}\t${branch}\t${birthYear}\ttrue\n`);
  }
}

function writeEncounters() {
  // cols: encounter_ref, member_no, branch_code, service_type, occurred_on (date only)
  out.write('encounter_ref\tmember_no\tbranch_code\tservice_type\toccurred_on\n');
  const beneCount = Number(args.beneficiaries ?? 1_000_000);
  for (let i = 1; i <= count; i++) {
    const encRef = `SYN-ENC-${pad(i, 10)}`;
    const memberNo = `SYN-M-${pad(1 + Math.floor(rnd() * beneCount), 9)}`;
    const branch = pick(BRANCHES);
    const svc = pick(SERVICE_TYPES);
    const day = new Date(Date.UTC(2024, 0, 1) + Math.floor(rnd() * 730) * 86400000)
      .toISOString().slice(0, 10);
    out.write(`${encRef}\t${memberNo}\t${branch}\t${svc}\t${day}\n`);
  }
}

if (dataset === 'beneficiaries') writeBeneficiaries();
else if (dataset === 'encounters') writeEncounters();
else { console.error(`unknown --dataset ${dataset}`); process.exit(2); }
