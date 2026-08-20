#!/usr/bin/env python3
"""Every endpoint's RESPONSE shape is part of its contract, and the specs must say so (Gate 2.9).

============================================================================================================
WHY THIS GATE EXISTS
============================================================================================================
`check-openapi-drift.sh` compares the committed specs against the running services and had been passing over
every response body on the platform. A minimal API that returns `Results.Ok(x)` publishes no schema for `x`,
so the generated spec described the ROUTE and the REQUEST and said nothing about what comes back.

That is not a documentation nicety. In 31.5 three fields were added to a prescription line — the dose, the
frequency and the duration a script was written from — and the drift gate reported "every committed spec
matches the running services". It was telling the truth about the half it could see.

The SPA parses those bodies with zod. A response shape that changes without anything noticing is a screen
that fails to parse at a dispensing counter, and the failure arrives as "could not load" rather than as a
build error.

============================================================================================================
WHY A RATCHET RATHER THAN A THRESHOLD
============================================================================================================
574 operations, and at the time this was written 149 of them declared a response. Demanding 100% would mean
inventing a named DTO for every anonymous object returned anywhere on the platform, in one change, across
twenty-two services — which is how a gate gets an `# TODO: re-enable` comment written above it.

So the bar is per service and it only moves UP. Each service's floor is what it achieves today; a change that
lowers one fails, and a change that raises one is asked to record the new floor. The remainder stops being a
sentence in a document that nobody re-reads and becomes a number that cannot grow.

Same shape as `check-floor-monotonicity.py` for coverage, and for the same reason: a bar that can be lowered
to pass is not a bar.

    check-response-schemas.py [--specs docs/api] [--floors tools/ci/response-schema-floors.json] [--update]
"""
from __future__ import annotations

import argparse
import json
import os
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SPECS = os.path.join(REPO, "docs", "api")
FLOORS = os.path.join(REPO, "tools", "ci", "response-schema-floors.json")

VERBS = ("get", "post", "put", "patch", "delete")


def measure(spec_dir: str) -> dict[str, tuple[int, int]]:
    """Per service: (operations declaring a success body, total operations)."""
    out: dict[str, tuple[int, int]] = {}
    for name in sorted(os.listdir(spec_dir)):
        if not name.endswith(".json"):
            continue
        with open(os.path.join(spec_dir, name), encoding="utf-8") as fh:
            spec = json.load(fh)
        declared = total = 0
        for item in spec.get("paths", {}).values():
            for verb, op in item.items():
                if verb not in VERBS:
                    continue
                total += 1
                # A 2xx response carrying `content` is one whose body shape the spec actually describes.
                # 204 and friends legitimately carry none, and are counted as declared: "no body" IS the
                # shape, and a gate that demanded a schema for them would be asking for a lie.
                responses = op.get("responses", {})
                if any(
                    str(code).startswith("2") and (payload.get("content") or code in ("204", "205"))
                    for code, payload in responses.items()
                ):
                    declared += 1
        out[name[:-5]] = (declared, total)
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--specs", default=SPECS)
    ap.add_argument("--floors", default=FLOORS)
    ap.add_argument("--update", action="store_true",
                    help="record today's numbers as the new floors (only ever raises them)")
    args = ap.parse_args()

    current = measure(args.specs)
    if not current:
        print(f"::error::no specs found under {args.specs}")
        return 2

    floors: dict[str, int] = {}
    if os.path.exists(args.floors):
        with open(args.floors, encoding="utf-8") as fh:
            floors = json.load(fh).get("declared", {})

    total_declared = sum(d for d, _ in current.values())
    total_ops = sum(t for _, t in current.values())
    print(f"response-schema gate: {total_declared}/{total_ops} operations declare a response body "
          f"({100 * total_declared / total_ops:.1f}%)")

    if args.update:
        raised = {name: max(floors.get(name, 0), declared) for name, (declared, _) in current.items()}
        for name in floors:
            raised.setdefault(name, floors[name])
        with open(args.floors, "w", encoding="utf-8") as fh:
            json.dump({
                "_comment": "Per-service count of operations declaring a response body. RATCHET: these only "
                            "go up. Raise them with --update after declaring more; a change that lowers one "
                            "fails the build. See check-response-schemas.py for why this is a floor and not "
                            "a target.",
                "declared": dict(sorted(raised.items())),
            }, fh, indent=2)
            fh.write("\n")
        print(f"floors written to {args.floors}")
        return 0

    regressions = []
    for name, (declared, total) in sorted(current.items()):
        floor = floors.get(name)
        if floor is None:
            regressions.append(f"{name}: no floor recorded — run with --update to set one")
        elif declared < floor:
            regressions.append(
                f"{name}: {declared}/{total} declare a response body, down from {floor}. "
                f"An endpoint stopped describing what it returns.")

    if regressions:
        print("\n::error::response-schema coverage went backwards:")
        for r in regressions:
            print(f"  - {r}")
        print("\nDeclare the response type on the endpoint you changed — `.Produces<T>()` on the map call.\n"
              "If the drop is deliberate, say why in the PR and lower the floor explicitly.")
        return 1

    behind = [(n, d, t) for n, (d, t) in sorted(current.items()) if d < t]
    if behind:
        print("\nstill undeclared (the ratchet's remaining work):")
        for name, declared, total in behind:
            print(f"  {name:14} {total - declared:4} of {total}")
    print("\nresponse-schema gate: OK — no service describes less than it did.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
