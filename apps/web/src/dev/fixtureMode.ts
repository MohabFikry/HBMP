/**
 * Is this build a FIXTURE build? (`true` here; `false` in the `.live.ts` twin that `vite.config.ts` aliases
 * in when `VITE_LIVE` is set.)
 *
 * ============================================================================================================
 * WHY A MODULE AND NOT A `VITE_LIVE` READ
 * ============================================================================================================
 * `config.ts` derives `LIVE` from this rather than from `import.meta.env.VITE_LIVE` so that the app's runtime
 * belief about which mode it is in CANNOT disagree with which modules were actually bundled. They used to be
 * two independent readings of the same variable: the alias decides what `src/dev/fixtures` resolves to at
 * build time, `LIVE` decided what the app does at run time, and nothing tied them together. A build where
 * those two disagreed would either render a role picker whose implementation had been stripped, or reach for
 * an HTTP client while the fixtures sat unused in the bundle. Now there is one fact and both read it.
 *
 * Typed `boolean` deliberately, not `true`. As a literal type, every `if (LIVE)` in the app would narrow to
 * dead code and `noUnusedLocals`/`no-constant-condition` would start rejecting perfectly correct branches —
 * type-checking is done against THIS variant, and it must not assume it is the one that ships.
 */
export const FIXTURE_MODE: boolean = true;
