#!/usr/bin/env python3
"""One origin for the app, the API and the issuer (ADR-0036 §4, phase 28.2).

WHY THIS GATE EXISTS
====================
`infra/compose/config/kong.yml` has routed /connect and /.well-known to identity-service since phase 17, with
a comment stating it is done "so the SPA reaches one origin". The SPA then pointed `VITE_OIDC_AUTHORITY` at
`http://localhost:8090` anyway — for two years — because nothing compared the two. An intention recorded in a
comment is not a constraint, and this file is the difference.

WHAT A VIOLATION COSTS
======================
Not an outage anyone could diagnose from the symptom. The issuer's four Identity cookies are `SameSite=Strict`
(IssuerSetup, on the stated assumption that "nothing in this flow is a legitimate cross-site navigation"), so
a cross-origin login POST has its session cookie dropped by the BROWSER. Nothing logs, nothing 500s: the
sign-in reports success and the authorize that follows reports `login_required`. The user is told their
credentials are wrong.

WHAT COUNTS AS SAME-ORIGIN
==========================
A blank or relative value is COMPLIANT, not a violation — it means "resolve against this document", which is
same-origin by definition and is the default the code now ships. Only an explicitly configured foreign origin
fails, and it is compared against the app's own redirect URI, which is the one value that must stay absolute
(the issuer matches `redirect_uri` byte-for-byte against the registered client).

THE GITIGNORED FILES ARE CHECKED TOO, AND THAT IS THE POINT
===========================================================
This gate passed, every run, while a developer's own `apps/web/.env.local` held
`VITE_OIDC_AUTHORITY=http://localhost:8090` — and sign-in answered "this is not a problem with your password"
for exactly the reason described above. The gate inspected three COMMITTED files and the defect was in an
untracked one, so the only configuration that actually reaches a running browser was the one configuration
nobody checked.

Local env files are therefore read when present, following Vite's own cascade (`.env` → `.env.local` →
`.env.[mode]` → `.env.[mode].local`, later winning) and checked as the MERGED result, because that is what the
bundle is built from. A partial `.env.local` that names only an authority is a real defect and a per-file
check would miss it.

They are OPTIONAL: absent in CI, where none of them exists, so this stays a committed-files gate there and
becomes a local-configuration gate on the machine that has a local configuration.

Run: python3 tools/ci/check-login-origin.py
"""
from __future__ import annotations

import re
import sys
from pathlib import Path
from urllib.parse import urlparse

ROOT = Path(__file__).resolve().parents[2]

# Where the browser-facing origins are configured. Each entry names the file and how to read the three values.
COMPOSE = ROOT / "infra" / "compose" / "compose.yaml"
DOCKERFILE = ROOT / "apps" / "web" / "Dockerfile"
ENV_EXAMPLE = ROOT / "apps" / "web" / ".env.example"

KEYS = ("VITE_API_BASE", "VITE_OIDC_AUTHORITY", "VITE_OIDC_REDIRECT")


def origin_of(value: str) -> str | None:
    """The origin of a configured value, or None when it is relative/blank (⇒ this origin, by definition)."""
    value = value.strip().strip('"').strip("'")
    if not value:
        return None
    parsed = urlparse(value)
    if not parsed.scheme or not parsed.netloc:
        return None
    return f"{parsed.scheme}://{parsed.netloc}"


def read_pairs(path: Path) -> dict[str, str]:
    """Pull the three VITE_* values out of a file, whichever syntax it uses.

    Deliberately syntax-agnostic (`KEY: "v"`, `KEY=v`, `ARG KEY=v`) rather than a YAML/Dockerfile parse. Three
    files in three formats declare the same three names, and a gate that understood only one of them would go
    quiet the moment somebody moved the value — which is the exact failure it is here to prevent.
    """
    if not path.exists():
        return {}
    text = path.read_text(encoding="utf-8")
    found: dict[str, str] = {}
    for key in KEYS:
        # Horizontal whitespace only. `\s*` after the `=` matches NEWLINES, so a blank `ARG VITE_OIDC_REDIRECT=`
        # swallowed the line break and captured the NEXT declaration's text — the gate then reported a real
        # violation against the wrong line, which is worse than silence because somebody would have edited the
        # innocent line to make it stop.
        pattern = rf"^[^\S\n]*(?:ARG[^\S\n]+|ENV[^\S\n]+)?{key}[^\S\n]*[:=][^\S\n]*(.*?)[^\S\n]*\\?[^\S\n]*$"
        for m in re.finditer(pattern, text, re.MULTILINE):
            value = m.group(1)
            # Skip pass-throughs like `ENV VITE_API_BASE=$VITE_API_BASE` — that line declares plumbing, not a
            # value. Reading it as one made this gate report the Dockerfile's ENV block as a malformed
            # redirect URI: a true complaint about the wrong line, which is worse than no complaint because
            # somebody would have "fixed" it.
            if value.startswith("$"):
                continue
            found[key] = value
            break
    return found


WEB = ROOT / "apps" / "web"

# Vite's env cascade, lowest precedence first. `.env.example` is excluded — it is documentation, checked on its
# own above, and is not loaded by Vite.
_GENERIC = re.compile(r"^\.env$")
_GENERIC_LOCAL = re.compile(r"^\.env\.local$")
_MODE = re.compile(r"^\.env\.([A-Za-z0-9_-]+)$")
_MODE_LOCAL = re.compile(r"^\.env\.([A-Za-z0-9_-]+)\.local$")


def local_env_cascades() -> dict[str, list[Path]]:
    """Every env file Vite would load, grouped by mode and ordered lowest-precedence first.

    A file like `.env.local.bak` matches nothing here on purpose: it is a backup, Vite never reads it, and
    failing a build over a file the bundle cannot see would be a gate crying about the wrong thing.
    """
    if not WEB.is_dir():
        return {}
    names = {p.name for p in WEB.iterdir() if p.is_file()}
    modes = {m.group(1) for n in names if (m := _MODE.match(n)) and m.group(1) not in ("local", "example")}
    modes |= {m.group(1) for n in names if (m := _MODE_LOCAL.match(n))}

    def chain(mode: str | None) -> list[Path]:
        order = [".env", ".env.local"] + ([f".env.{mode}", f".env.{mode}.local"] if mode else [])
        return [WEB / n for n in order if n in names]

    cascades = {mode: chain(mode) for mode in sorted(modes)}
    base = chain(None)
    if base and not cascades:
        cascades["(no mode)"] = base
    return {k: v for k, v in cascades.items() if v}


def merge(paths: list[Path]) -> tuple[dict[str, str], dict[str, Path]]:
    """The effective values after the cascade, and which file supplied each — so the message names the file
    somebody has to edit rather than the mode it was resolved under."""
    values: dict[str, str] = {}
    source: dict[str, Path] = {}
    for path in paths:
        for key, value in read_pairs(path).items():
            values[key] = value
            source[key] = path
    return values, source


def check_pairs(pairs: dict[str, str], label: str, source: dict[str, Path] | None = None) -> list[str]:
    """The origin comparison itself, over an already-resolved set of values."""
    if not pairs:
        return []

    def where(key: str) -> str:
        if source and key in source:
            return str(source[key].relative_to(ROOT))
        return label

    rel = label
    app = origin_of(pairs.get("VITE_OIDC_REDIRECT", ""))
    problems: list[str] = []

    if app is None:
        # The redirect URI is the ONE value that must stay absolute: the issuer compares it byte-for-byte
        # against the registered client, and a relative one can never match.
        #
        # BLANK is exempt, and deliberately. apps/web/Dockerfile declares these ARGs with empty defaults so an
        # unparameterised `docker build` still produces a working bundle (config.ts treats blank as absent and
        # falls back). Failing on blank would fail the image that is behaving correctly.
        declared = pairs.get("VITE_OIDC_REDIRECT", "").strip().strip('"').strip("'")
        if declared:
            problems.append(
                f"{where('VITE_OIDC_REDIRECT')}: VITE_OIDC_REDIRECT is {declared!r} — it must be an ABSOLUTE "
                "URL, because the issuer matches redirect_uri against the registered client exactly."
            )
        return problems

    for key in ("VITE_API_BASE", "VITE_OIDC_AUTHORITY"):
        if key not in pairs:
            continue
        other = origin_of(pairs[key])
        if other is not None and other != app:
            problems.append(
                f"{where(key)}: {key} is on {other} but the app is on {app}. The SPA, the API and the issuer "
                "must share ONE origin (ADR-0036 §4) — the app's own proxy forwards /api, /connect, "
                f"/.well-known and /identity to the gateway. Use a relative value, or {app}."
            )
    return problems


def check(path: Path) -> list[str]:
    """One committed file, read on its own."""
    pairs = read_pairs(path)
    return check_pairs(pairs, str(path.relative_to(ROOT))) if pairs else []


def main() -> int:
    problems: list[str] = []
    checked = 0
    for path in (COMPOSE, DOCKERFILE, ENV_EXAMPLE):
        if read_pairs(path):
            checked += 1
        problems.extend(check(path))

    if checked == 0:
        # A gate that finds nothing to check is not passing; it has lost its subject. Only the COMMITTED files
        # count here: the local ones are absent in CI by design, so requiring them would fail every CI run.
        print("login-origin gate: FAILED — none of the expected files declared any VITE_* origin. "
              "The values moved and this gate went quiet, which is the failure it exists to prevent.")
        return 1

    # The local cascade, when there is one. This is the configuration a running browser is actually built
    # from, and until 28.16 it was the only one nobody looked at.
    local = 0
    for mode, paths in local_env_cascades().items():
        values, source = merge(paths)
        if not values:
            continue
        local += 1
        problems.extend(check_pairs(values, f"apps/web (mode {mode})", source))

    print(f"login-origin gate: {checked} committed file(s) declare browser-facing origins"
          + (f", plus {local} local env cascade(s)" if local else " (no local env files on this machine)"))
    if problems:
        print("login-origin gate: FAILED")
        for p in problems:
            print(f"  - {p}")
        return 1
    print("login-origin gate: OK — app, API and issuer share one origin everywhere they are configured")
    return 0


if __name__ == "__main__":
    sys.exit(main())
