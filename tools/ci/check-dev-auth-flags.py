#!/usr/bin/env python3
"""Development-only authentication relaxations may not escape Development (18-security-model.md §3.3, §10).

WHY THIS GATE EXISTS
====================
`libs/auth/HbmpAuthOptions.cs` defaults `ProtectedScopeRequiresMfa` to **true**, and the security model
requires MFA for every staff and provider role with step-up for T3/T4 actions. `infra/compose/compose.yaml`
then set `Auth__ProtectedScopeRequiresMfa: "false"` on all 21 services, alongside
`Auth__RequireHttpsMetadata: "false"` and a seeded shared demo password.

That is defensible for a laptop and indefensible anywhere else, and nothing distinguished the two. Compose
Tier 1 is the only deployment artifact that exists — `infra/helm`, `infra/tofu` and `infra/ansible` are
`.gitkeep` stubs — so the only way to actually run this platform was with password-only authentication over
plaintext, issuing fully-scoped tokens against every PHI endpoint. The relaxation was not a decision anyone
made twice; it was a dev convenience that became the deployment because no other deployment was written.

WHAT THIS ASSERTS
=================
A compose file, Helm values file or appsettings file may disable MFA or HTTPS metadata validation **only**
where the same service is pinned to `ASPNETCORE_ENVIRONMENT: Development`. A service that relaxes either
flag without that pin fails, which is what turns "we'll tighten it for production" from an intention into a
constraint — the same reason check-login-origin.py exists.

It deliberately does NOT try to judge whether Development is appropriate for a given file. Compose Tier 1
declares itself a development stack and is allowed to be one. What it stops is the flag travelling into a
file that does not.

Run: python3 tools/ci/check-dev-auth-flags.py
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# The relaxations that are only ever acceptable in Development, and what each one costs elsewhere.
RELAXATIONS = {
    "Auth__ProtectedScopeRequiresMfa": "scope-protected PHI endpoints accept a password-only token",
    "Auth:ProtectedScopeRequiresMfa": "scope-protected PHI endpoints accept a password-only token",
    "Auth__RequireHttpsMetadata": "the issuer's signing keys are fetched over plaintext HTTP",
    "Auth:RequireHttpsMetadata": "the issuer's signing keys are fetched over plaintext HTTP",
}

FALSE = re.compile(r'^\s*"?([A-Za-z_:]+)"?\s*[:=]\s*"?(false)"?\s*,?\s*$', re.IGNORECASE)
ENVIRONMENT = re.compile(r'ASPNETCORE_ENVIRONMENT\s*[:=]\s*"?(\w+)"?', re.IGNORECASE)

SEARCH = [
    ROOT / "infra",
    ROOT / "services",
]
SUFFIXES = {".yaml", ".yml", ".json"}
SKIP_PARTS = {"bin", "obj", "node_modules", ".git"}


def blocks(path: Path) -> list[tuple[int, str, str | None]]:
    """Each relaxation found, with the environment in force for its service block.

    Compose and Helm both group a service's settings under one indented key, so the environment that
    applies to a flag is the nearest ASPNETCORE_ENVIRONMENT at or above it within the same block. Tracking
    it by indentation is enough for both shapes and for a flat appsettings file, where there is one.
    """
    found: list[tuple[int, str, str | None]] = []
    current_env: str | None = None
    current_indent = -1

    for lineno, raw in enumerate(path.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
        stripped = raw.strip()
        if not stripped or stripped.startswith("#"):
            continue
        indent = len(raw) - len(raw.lstrip())

        # Dedenting out of the block the environment was declared in retires it, so one service's
        # Development pin cannot vouch for the next service's flags.
        if current_env is not None and indent < current_indent:
            current_env = None
            current_indent = -1

        if (m := ENVIRONMENT.search(stripped)) is not None:
            current_env = m.group(1)
            current_indent = indent
            continue

        if (m := FALSE.match(stripped)) is not None and m.group(1) in RELAXATIONS:
            found.append((lineno, m.group(1), current_env))

    return found


def main() -> int:
    offenders: list[str] = []
    checked = 0

    for root in SEARCH:
        if not root.exists():
            continue
        for path in sorted(root.rglob("*")):
            if path.suffix not in SUFFIXES or not path.is_file():
                continue
            if SKIP_PARTS & set(path.parts):
                continue
            checked += 1
            for lineno, key, env in blocks(path):
                if env is not None and env.lower() == "development":
                    continue
                rel = path.relative_to(ROOT)
                where = f"ASPNETCORE_ENVIRONMENT={env}" if env else "no ASPNETCORE_ENVIRONMENT in scope"
                offenders.append(f"{rel}:{lineno}: {key}=false with {where} — {RELAXATIONS[key]}")

    if offenders:
        print("Development-only auth relaxations found outside a Development profile:\n", file=sys.stderr)
        for o in offenders:
            print(f"  {o}", file=sys.stderr)
        print(
            "\nThese flags default to the secure value in libs/auth/HbmpAuthOptions.cs. Overriding them is "
            "acceptable on a development stack that says so, and nowhere else: pin the service to "
            "ASPNETCORE_ENVIRONMENT: Development, or remove the override and let the default stand.",
            file=sys.stderr,
        )
        return 1

    print(f"dev-auth-flags: OK — {checked} config files checked, no relaxation outside Development.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
