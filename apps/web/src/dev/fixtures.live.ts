import type { Fixtures } from "./fixtures";

/**
 * The live-build twin of `fixtures.ts`. `vite.config.ts` aliases `@dev/fixtures` here when `VITE_LIVE` is
 * set, so the demo backend, the bypass sign-in and the role picker are absent from the bundle rather than
 * merely unreached.
 *
 * The `import type` above is the whole conformance check: it is erased at build time, so nothing from the
 * fixture module is pulled in, and `tsc` still rejects this file the moment the two variants stop matching.
 * A stub that has quietly drifted from its twin is worse than no stub — it type-checks in the build nobody
 * runs locally and fails in the one that ships.
 *
 * Every member THROWS rather than returning a harmless empty. Reaching one of these means `LIVE` and the
 * bundled variant disagreed, which `fixtureMode.ts` now makes impossible; if that ever stops being true, the
 * app must say so at the point of confusion rather than silently serving an empty demo to a real clinic.
 *
 * In practice these bodies never reach the output at all. `FIXTURE_MODE` is a `const false` in a live build,
 * so rollup folds `LIVE` to `true` and drops the `FIXTURES.createApi()` branch along with everything behind
 * it — measured: aliasing only `@dev/fixture-mode` and leaving `@dev/fixtures` pointing at the real module
 * already produces a bundle with no fixture strings in it. That fold is a bonus, not the guarantee. It holds
 * only while every call site keeps `LIVE` in a position rollup can fold; one `useMemo(() => LIVE ? a : b)`
 * away from a lambda it declines to reason about and the whole 4,111-line subtree is back. The alias does not
 * depend on the optimiser's mood, which is why both exist.
 */
const absent = (what: string): never => {
  throw new Error(
    `${what} is not in this build. It is a fixture-only module and this bundle was built with VITE_LIVE set. ` +
      "Reaching it means config.ts's LIVE disagrees with the aliased fixture module — see src/dev/fixtures.ts.",
  );
};

export const FIXTURES: Fixtures = {
  available: false,
  createApi: () => absent("DevApiClient"),
  createAuth: () => absent("DevAuthClient"),
  createBranchApis: () => absent("DevBranchApis"),
  LoginForm: () => absent("DevLoginForm"),
};
