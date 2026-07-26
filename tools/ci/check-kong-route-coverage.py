#!/usr/bin/env python3
"""Kong route-coverage guard (audit H3 / 16.5).

Every /api/v1 resource a service actually serves MUST have a matching route in the Kong gateway
config, otherwise the SPA can reach it only by talking to the service directly — bypassing the single
public origin. This script extracts each served resource prefix from services/*/Api and fails if
kong.yml has no route that covers it. Wired into CI in 16.8.

Discovery rules (minimal-api style used across the codebase):
  * MapGroup("/api/v1/<seg>...")            -> resource /api/v1/<seg>
  * bare MapGroup("/api/v1") + child .Map*("/<seg>...")  -> resource /api/v1/<seg>
  * direct app.Map*("/api/v1/<seg>...")     -> resource /api/v1/<seg>
Health/metrics/hello and non-/api/v1 paths are ignored.

Coverage: a resource R is covered when some Kong route path P
  * plain: R == P or R starts with P + "/"  (Kong prefix match), or
  * regex (P starts with "~"): the regex matches R or R + "/x".
"""
from __future__ import annotations
import re, sys, glob, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
IGNORE_SEGMENTS = {"hello", "health", "metrics"}
# Services intentionally NOT on the public gateway: audit-service is written via the internal audit
# client and its read API is compliance-internal — it is not part of the single SPA origin.
INTERNAL_SERVICES = {"audit"}

group_re  = re.compile(r'MapGroup\("(/api/v1[^"]*)"')
child_re  = re.compile(r'\.Map(?:Get|Post|Put|Patch|Delete)\("(/[^"]*)"')
direct_re = re.compile(r'\bMap(?:Get|Post|Put|Patch|Delete)\("(/api/v1/[^"]*)"')


def resource(path: str) -> str | None:
    """Reduce a full path to its /api/v1/<seg> resource prefix."""
    m = re.match(r'^(/api/v1/[^/]+)', path)
    if not m:
        return None
    seg = m.group(1).split("/")[3]
    return None if seg in IGNORE_SEGMENTS else m.group(1)


def served_resources() -> dict[str, str]:
    """resource-prefix -> owning service dir (first seen)."""
    found: dict[str, str] = {}
    for cs in glob.glob(str(ROOT / "services/*/Api/**/*.cs"), recursive=True):
        svc = pathlib.Path(cs).relative_to(ROOT).parts[1]
        if svc == "hello" or svc in INTERNAL_SERVICES:
            continue
        text = pathlib.Path(cs).read_text(encoding="utf-8", errors="ignore")
        groups = group_re.findall(text)
        has_bare = any(g.rstrip("/") == "/api/v1" for g in groups)
        # explicit group prefixes deeper than /api/v1
        for g in groups:
            r = resource(g)
            if r:
                found.setdefault(r, svc)
        # bare /api/v1 group: children carry the resource segment
        if has_bare:
            for child in child_re.findall(text):
                r = resource("/api/v1" + child)
                if r:
                    found.setdefault(r, svc)
        # direct maps with a full /api/v1 path
        for d in direct_re.findall(text):
            r = resource(d)
            if r:
                found.setdefault(r, svc)
    return found


def kong_paths() -> list[str]:
    paths: list[str] = []
    for raw in (ROOT / "infra/compose/config/kong.yml").read_text(encoding="utf-8").splitlines():
        line = raw.split("#", 1)[0]  # drop comments so /api/v1/<area> prose isn't mistaken for a route
        # quoted paths (incl. regex "~/api/v1/...[^/]+..." with brackets)
        paths += re.findall(r'"(~?/api/v1[^"]*)"', line)
        # unquoted YAML list item:  - /api/v1/x
        m = re.match(r'\s*-\s*(~?/api/v1\S*)\s*$', line)
        if m:
            paths.append(m.group(1))
    return sorted(set(paths))


def covers(resource_path: str, kong: list[str]) -> bool:
    for p in kong:
        if p.startswith("~"):
            rx = p[1:]
            if re.match(rx, resource_path) or re.match(rx, resource_path + "/x"):
                return True
        else:
            # Kong prefix match: route path P matches request R when R == P or R is under P.
            if resource_path == p or resource_path.startswith(p + "/"):
                return True
    return False


def main() -> int:
    served = served_resources()
    kong = kong_paths()
    missing = sorted(r for r in served if not covers(r, kong))
    print(f"Kong route-coverage guard: {len(served)} served resources, {len(kong)} kong paths")
    if missing:
        print("\n❌ MISSING Kong routes for served resources:")
        for r in missing:
            print(f"   {r:45} (served by {served[r]}-service)")
        print("\nAdd a route in infra/compose/config/kong.yml for each, then re-run.")
        return 1
    print("✓ every served /api/v1 resource has a Kong route")
    return 0


if __name__ == "__main__":
    sys.exit(main())
