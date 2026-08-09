#!/usr/bin/env python3
"""The live SPA bundle must not CONTAIN the fixture backend (2026-08-09 audit §2).

THE FAILURE THIS EXISTS FOR. A `VITE_LIVE=1` build of apps/web shipped `DevApiClient` — 4,111 lines of
synthetic beneficiaries, prescriptions, claims and clinical results — together with `DevAuthClient`, a
sign-in that accepts any six digits and mints the permission set of whichever role you pick, and the role
picker that drives it. Verified rather than suspected: `MRS-M-10231`, `Amal Hassan` and `أمل حسن` were all
findable as plain strings in `dist/assets/index-*.js`.

None of it was reachable — three separate call sites branched on `LIVE` first. But "unreachable" is a
property of today's control flow, re-argued every time somebody edits one of those files, and it is not what
anybody means when they ask whether the deployed bundle contains a bypass login. So the fixture modules now
sit behind `@dev/fixtures`, which `apps/web/vite.config.ts` aliases to a refusing stub for a live build.

WHY A GATE AND NOT A CODE REVIEW. The elimination depends on a resolve alias, a tsconfig path and three
import sites all continuing to agree. Any one of them can be undone by a plausible-looking edit — adding
`import { DevApiClient }` back into a screen for "just a fallback" restores the whole subtree, and nothing
about the build would look different. This reads the built JavaScript.

THREE ASSERTIONS, because the obvious one alone is vacuous:

  1. no fixture marker appears in the LIVE build              — the thing being prevented
  2. the live sign-in path's own strings DO appear in it      — proves there was a real application to
                                                                search. An empty or half-written bundle
                                                                satisfies assertion 1 for free.
  3. every fixture marker DOES appear in the FIXTURE build    — proves the markers still exist to be found.
                                                                Without this the gate goes quiet the day a
                                                                fixture is renamed, and reports success
                                                                forever after.

    check-live-bundle-clean.py [--web-dir apps/web] [--keep] [--selftest]
"""
from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

# Literal strings that exist ONLY in fixture-mode modules. Chosen to be unmangleable: minification renames
# identifiers but never touches the inside of a string, so a marker either survives into the output or its
# module did not. Keep one per fixture module, so a partial regression is still caught.
MARKERS = [
    # src/api/DevApiClient.ts — the synthetic member directory.
    ("MRS-M-10231", "fixture beneficiary id"),
    ("Amal Hassan", "fixture beneficiary name"),
    ("Insulin out of stock at branch pharmacy", "fixture pharmacy exception"),
    ("Nile Central Hospital", "fixture provider"),
    # src/api/DevApiClient.ts — the demo fault injector.
    ("Simulated upstream failure", "fixture fault injection"),
    # src/auth/devAuthClient.ts — the any-six-digits sign-in's display names.
    ("Reham (Reception)", "fixture session display name"),
    ("Nadia (Maadi Coordinator)", "fixture session display name"),
]

# Strings from the LIVE sign-in path (src/auth/oidcClient.ts, src/auth/sessionApi.ts, the live login layout).
# They distinguish "the fixtures are absent" from "the search found nothing because there was nothing there".
#
# Not the stub's own refusal message, which was the first thing tried: `LIVE` folds to a constant in a live
# build, so rollup drops the unreachable `FIXTURES.createApi()` branch AND the stub behind it. Absence by
# folding is just as good an outcome as absence by aliasing — but it leaves the stub with no footprint to
# look for, and a control nobody can observe is not a control.
CONTROL_MARKERS = [
    ("code_challenge_method", "the PKCE sign-in"),
    ("login-split", "the live login layout"),
]


def build(web_dir: str, out_dir: str, live: bool) -> None:
    """Produce a bundle in `out_dir`. Vite is invoked directly: `pnpm --filter` would put the workspace
    filter's argument parsing between us and `--outDir`, and a flag that has to survive two parsers is a
    flag that eventually does not."""
    env = dict(os.environ, VITE_LIVE="1" if live else "0")
    vite = os.path.join(web_dir, "node_modules", ".bin", "vite")
    if not os.path.exists(vite):
        raise SystemExit(f"::error::{vite} not found — run the install step before this gate")
    r = subprocess.run([vite, "build", "--outDir", out_dir, "--emptyOutDir"],
                       cwd=web_dir, env=env, capture_output=True, text=True)
    if r.returncode != 0:
        raise SystemExit(f"::error::{'live' if live else 'fixture'} build failed:\n{r.stdout}\n{r.stderr}")


def read_js(out_dir: str) -> str:
    """Every emitted .js, concatenated. Lazily-loaded route chunks count: a fixture module split into its
    own chunk is still shipped and still downloadable."""
    blobs = []
    for root, _dirs, files in os.walk(out_dir):
        for f in files:
            if f.endswith(".js"):
                with open(os.path.join(root, f), encoding="utf-8", errors="replace") as fh:
                    blobs.append(fh.read())
    return "\n".join(blobs)


def check(live_js: str, fixture_js: str) -> list[str]:
    problems = []

    leaked = [(m, why) for m, why in MARKERS if m in live_js]
    for marker, why in leaked:
        problems.append(
            f"the LIVE bundle contains {marker!r} ({why}). The fixture backend is being bundled again — "
            "something imports src/api/DevApiClient, src/auth/devAuthClient or src/dev/DevLoginForm "
            "outside src/dev/fixtures.ts, or the @dev/fixtures alias in apps/web/vite.config.ts stopped "
            "resolving to fixtures.live.ts.")

    for marker, what in CONTROL_MARKERS:
        if marker not in live_js:
            problems.append(
                f"the LIVE bundle does not contain {marker!r} ({what}), so it is not a complete build of this "
                "application and finding no fixture code in it proves nothing. Either the build produced "
                "something unexpected, or that string moved and this control needs updating.")

    missing = [(m, why) for m, why in MARKERS if m not in fixture_js]
    for marker, why in missing:
        problems.append(
            f"the FIXTURE bundle does not contain {marker!r} ({why}), so this gate is no longer proving "
            "anything about it. The fixture was renamed or removed — update MARKERS in this file to a "
            "string that still exists.")

    return problems


def selftest() -> int:
    ok = True
    real_live = "var a=1;" + "".join(m for m, _ in CONTROL_MARKERS)
    real_fixture = "".join(m for m, _ in MARKERS)

    cases = [
        ("a clean live build alongside a real fixture build passes", real_live, real_fixture, 0),
        ("a fixture marker leaking into live fails",
         real_live + MARKERS[0][0], real_fixture, 1),
        # The two ways this gate could go quiet, each its own case.
        ("an empty live bundle fails rather than passing for free", "", real_fixture,
         len(CONTROL_MARKERS)),
        ("markers that no longer exist in the fixture build fail",
         real_live, "nothing here", len(MARKERS)),
    ]
    for name, live_js, fixture_js, expected in cases:
        got = len(check(live_js, fixture_js))
        if got != expected:
            print(f"FAIL: {name} — expected {expected} problem(s), got {got}")
            ok = False

    print("selftest: PASS — leak, empty-bundle and stale-marker cases all behave" if ok else "selftest: FAIL")
    return 0 if ok else 1


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--web-dir", default=os.path.join(REPO, "apps", "web"))
    ap.add_argument("--keep", action="store_true", help="leave the two bundles on disk for inspection")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()

    if a.selftest:
        return selftest()

    web_dir = os.path.abspath(a.web_dir)
    # Inside the app directory: vite refuses --outDir outside its root without extra ceremony, and a path
    # under the workspace is also what makes --keep useful.
    base = os.path.join(web_dir, ".bundle-check")
    live_dir, fixture_dir = os.path.join(base, "live"), os.path.join(base, "fixture")
    try:
        build(web_dir, live_dir, live=True)
        build(web_dir, fixture_dir, live=False)
        problems = check(read_js(live_dir), read_js(fixture_dir))
    finally:
        if not a.keep:
            shutil.rmtree(base, ignore_errors=True)

    if problems:
        print("::error::the live SPA bundle is not free of fixture code:")
        for p in problems:
            print(f"  - {p}")
        return 1
    print(f"live-bundle: OK — none of the {len(MARKERS)} fixture markers survive a VITE_LIVE=1 build, that "
          "build is a complete application, and every marker is still present in the fixture build.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
