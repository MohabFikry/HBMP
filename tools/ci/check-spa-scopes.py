#!/usr/bin/env python3
"""Fail the build when the SPA's requested OAuth scopes drift from the issuer's interactive set.

WHY THIS GATE EXISTS
--------------------
`apps/web/src/config.ts` hard-codes the scope string the SPA sends to `/connect/authorize`. It is a hand-
maintained copy of `IdentityContract.InteractiveScopes`, and by phase 19 the copy was nineteen scopes behind
the original — every claims scope, both note scopes, `patient:read`, `rx:read`, `reception:read`,
`reporting:read-financial`, `provider:admin`, and the whole policy-administration set.

The SPA asks for the union in ONE authorization request, so drift does not degrade gracefully:

  * a scope the SPA requests that the client is not permitted refuses the entire login —
    `invalid_request … not allowed to use the specified scope` (OpenIddict ID2051), no portal at all;
  * a scope the SPA omits yields a token that signs in perfectly and then 403s on every endpoint that
    guards it, which reads to the user as a broken screen rather than a missing permission.

Both occurred simultaneously: the machine-only ingest/projection scopes were still requested after 18.B1
narrowed the public client to interactive scopes, while nothing added after phase 17 was requested at all.

A scope added to the frozen contract is a two-file change. This makes forgetting the second file a red build
instead of a refused login.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTRACT = ROOT / "services/identity/Domain/IdentityContract.cs"
CONFIG = ROOT / "apps/web/src/config.ts"

# Granted implicitly by the OIDC/OpenIddict layer rather than listed in the platform's scope vocabulary.
PROTOCOL_SCOPES = {"openid", "offline_access"}


def csharp_list(source: str, name: str) -> list[str]:
    match = re.search(rf"{name} =\s*\[(.*?)\];", source, re.S)
    if not match:
        sys.exit(f"check-spa-scopes: could not find `{name}` in {CONTRACT.relative_to(ROOT)}")
    return re.findall(r'"([^"]+)"', match.group(1))


def spa_scopes(source: str) -> set[str]:
    # Trailing `,` on the final line as well as ` +` on the continuations — anchoring on only one of the
    # two silently reads a short list and reports a drift that is not there.
    #
    # `//` lines are part of the run too. The literal is sixty-odd scopes long and the ones that need
    # explaining are commented in place; a pattern that only matched consecutive STRING lines stopped dead
    # at the first such comment, read one line of a nine-line literal, and reported sixty-eight scopes
    # missing that were sitting right there. A checker whose failure mode is a confident false positive
    # teaches people to distrust it, which costs more than the drift it was written to catch.
    match = re.search(r'  scope:\n((?:    (?://[^\n]*|"[^"]*"(?: \+|,)?)\n)+)', source)
    if not match:
        sys.exit(f"check-spa-scopes: could not find the `scope:` literal in {CONFIG.relative_to(ROOT)}")
    # Drop the comment lines before harvesting strings, so a quoted word inside a comment cannot be read
    # as a requested scope.
    body = "\n".join(ln for ln in match.group(1).splitlines() if not ln.lstrip().startswith("//"))
    return set(" ".join(re.findall(r'"([^"]*)"', body)).split())


def main() -> int:
    contract = CONTRACT.read_text(encoding="utf-8")
    scopes = csharp_list(contract, "Scopes")
    service = set(csharp_list(contract, "ServiceScopes"))
    interactive = {s for s in scopes if s not in service}

    requested = spa_scopes(CONFIG.read_text(encoding="utf-8"))
    missing_protocol = PROTOCOL_SCOPES - requested
    not_requested = interactive - requested
    # A public client may never hold the machine ingest/projection scopes (18.B1), so requesting one is not
    # merely redundant — it is a login the issuer will refuse outright.
    machine = requested & service
    unknown = requested - interactive - PROTOCOL_SCOPES - service

    problems: list[str] = []
    if missing_protocol:
        problems.append(f"  missing protocol scopes: {sorted(missing_protocol)}")
    if not_requested:
        problems.append(
            f"  in the issuer's interactive set but NOT requested (token would 403 on those endpoints):\n"
            f"    {sorted(not_requested)}"
        )
    if machine:
        problems.append(
            f"  machine-only scopes requested by the PUBLIC client — the issuer refuses the whole login:\n"
            f"    {sorted(machine)}"
        )
    if unknown:
        problems.append(f"  requested but not in the issuer's vocabulary at all:\n    {sorted(unknown)}")

    if problems:
        print("SPA scope guard: apps/web/src/config.ts has drifted from IdentityContract", file=sys.stderr)
        print("\n".join(problems), file=sys.stderr)
        print(
            "\nFix: make the `scope:` literal equal openid + offline_access + InteractiveScopes.",
            file=sys.stderr,
        )
        return 1

    print(f"SPA scope guard: {len(interactive)} interactive scopes, all requested; no machine scopes leaked")
    print("✓ apps/web/src/config.ts matches IdentityContract")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
