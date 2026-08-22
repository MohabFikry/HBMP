#!/usr/bin/env python3
"""Every host Kong forwards to is something that actually runs.

WHY THIS EXISTS
---------------
`check-kong-route-coverage.py` asks one direction of the question: does every public resource this platform
SERVES have a route at the gateway. It has caught several real defects and it is the reason the day roster,
the booking reads and the availability CRUD all have routes.

Nothing asked the other direction. `inventory-service` was routed from Kong at 25.5, its schema was migrated,
its 68 tests passed — and it had no Dockerfile and no compose service. So `/api/v1/inventory` resolved to a
hostname that does not exist on the network, every request from the Inventory screen failed at name
resolution, and the SPA reported "the service couldn't complete this request": the wording of a transient
fault, for an upstream that had never been deployed at all. It shipped that way and stayed that way.

A route to nowhere is worse than a missing route. A missing one 404s at the edge, which reads as "not built
yet". A route to a host nobody runs looks exactly like an outage of something that exists.

THE RULE
--------
Every `url: http://<host>:<port>` in the Kong declarative config names a host that is either

  * a service in infra/compose/compose.yaml, or
  * listed in ALLOWED_EXTERNAL below, with a reason.

Usage: tools/ci/check-kong-upstreams.py
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
KONG = ROOT / "infra" / "compose" / "config" / "kong.yml"
COMPOSE = ROOT / "infra" / "compose" / "compose.yaml"

# Hosts Kong may forward to that are deliberately NOT compose services. Empty today; anything added here
# needs a sentence saying what runs it, because the whole point of this gate is that "it is somewhere else"
# is a claim somebody has to make on purpose.
ALLOWED_EXTERNAL: dict[str, str] = {}

UPSTREAM = re.compile(r"^\s*url:\s*https?://([A-Za-z0-9_.-]+)(?::\d+)?", re.MULTILINE)
# A compose service key: two spaces, a name, a colon, nothing else on the line — read ONLY from inside the
# top-level `services:` block. `volumes:` and `networks:` indent their children the same way, so scanning the
# whole file counts a volume as a service; that would only ever make this gate more permissive, which is the
# direction a guard must not drift. (`docker compose config --services` is authoritative but needs docker,
# which the lint job does not have.)
SERVICE = re.compile(r"^  ([a-z0-9][a-z0-9_-]*):\s*$", re.MULTILINE)
TOP_LEVEL = re.compile(r"^([a-z][a-z0-9_-]*):", re.MULTILINE)


def service_names(compose: str) -> set[str]:
    start = re.search(r"^services:\s*$", compose, re.MULTILINE)
    if start is None:
        return set()
    rest = compose[start.end():]
    end = TOP_LEVEL.search(rest)
    return set(SERVICE.findall(rest[: end.start()] if end else rest))


def main() -> int:
    if not KONG.exists():
        print(f"::error::{KONG} not found")
        return 1
    if not COMPOSE.exists():
        print(f"::error::{COMPOSE} not found")
        return 1

    kong = KONG.read_text(encoding="utf-8")
    compose = COMPOSE.read_text(encoding="utf-8")

    upstreams = sorted(set(UPSTREAM.findall(kong)))
    services = service_names(compose)

    if not upstreams:
        print("::error::no upstreams parsed from kong.yml — this gate would pass on an empty set")
        return 1
    if len(services) < 10:
        print(f"::error::only {len(services)} compose services parsed — refusing to judge against that")
        return 1

    missing = [h for h in upstreams if h not in services and h not in ALLOWED_EXTERNAL]

    print(f"Kong upstream guard: {len(upstreams)} upstream host(s), {len(services)} compose service(s)")
    if missing:
        for h in missing:
            print(f"::error::kong forwards to '{h}', which is not a service in infra/compose/compose.yaml")
        print()
        print("A route to a host nobody runs looks exactly like an outage of something that exists.")
        print("Add the service to compose.yaml (and give it a Dockerfile), or remove its Kong route.")
        return 1

    print("✓ every Kong upstream is a deployed service")
    return 0


if __name__ == "__main__":
    sys.exit(main())
