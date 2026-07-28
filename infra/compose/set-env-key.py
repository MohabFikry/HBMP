#!/usr/bin/env python3
"""Set KEY="<value>" in a dotenv file as a SINGLE line, with newlines escaped as \\n.

    printf '%s' "$pem" | ./set-env-key.py IDENTITY_JWKS_PUBLIC_KEY [.env]

Replaces the shell idiom this script was written to kill:

    grep -v '^KEY=' .env > tmp
    printf 'KEY="%s"\\n' "$(printf '%s' "$pem" | awk '{printf "%s\\\\n", $0}')" >> tmp

which has two independent bugs, and needs BOTH to be wrong before anything looks wrong:

1. GNU awk resolves `"%s\\n"` to a REAL newline (escape processing happens in the printf format
   string, not just at lex time), so the value is written across multiple lines. dotenv accepts a
   multi-line double-quoted scalar, so the first write is valid and the stack comes up fine.

2. `grep -v '^KEY='` then removes only the FIRST line of that multi-line value on the NEXT run,
   orphaning the rest of the PEM as bare lines. Every later `docker compose` command dies with
   `unexpected character "/" in variable name` — including the ones in this same script, which
   then fail silently and leave Kong running with a key that no longer matches the issuer.

Writing one escaped line, and parsing quotes properly when removing the old entry, fixes both.
Orphaned PEM fragments from a file corrupted by the old idiom are also stripped, so this
self-heals rather than requiring the .env to be restored by hand.
"""
import re
import sys
from pathlib import Path

PEM_BODY = re.compile(r"[A-Za-z0-9+/]{40,}={0,2}\Z")


def strip_existing(lines: list[str], key: str) -> list[str]:
    out: list[str] = []
    skipping = False
    for line in lines:
        if skipping:
            # inside a multi-line quoted value: consume through the closing quote
            if line.rstrip().endswith('"'):
                skipping = False
            continue

        if line.startswith(key + "="):
            value = line[len(key) + 1:].strip()
            # An opening quote with no closing quote means the value continues on later lines.
            if value.startswith('"') and not (len(value) > 1 and value.endswith('"')):
                skipping = True
            continue

        # Fragments left behind by a previous corrupting rewrite.
        stripped = line.strip()
        if stripped.startswith("-----BEGIN ") or stripped.startswith("-----END "):
            continue
        if PEM_BODY.match(stripped):
            continue

        out.append(line)
    return out


def main() -> int:
    if len(sys.argv) < 2:
        return print(__doc__, file=sys.stderr) or 2

    key = sys.argv[1]
    env_path = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(".env")
    value = sys.stdin.read().strip()

    if not value:
        print(f"refusing to write an empty {key}", file=sys.stderr)
        return 1

    lines = env_path.read_text().splitlines() if env_path.exists() else []
    kept = strip_existing(lines, key)
    while kept and not kept[-1].strip():
        kept.pop()

    escaped = "\\n".join(value.splitlines())
    kept.append(f'{key}="{escaped}"')
    env_path.write_text("\n".join(kept) + "\n")

    print(f"{env_path}: {key} set ({len(escaped)} chars, 1 line)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
