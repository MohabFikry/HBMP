/**
 * The live-build twin of `fixtureMode.ts`. `vite.config.ts` aliases `@dev/fixture-mode` here when `VITE_LIVE`
 * is set, which is the same switch that swaps `@dev/fixtures` for its refusing stub — so `LIVE === true` and
 * "the fixtures were not bundled" are one fact rather than two that can drift.
 */
export const FIXTURE_MODE: boolean = false;
