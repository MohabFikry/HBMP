#!/usr/bin/env python3
"""Every invariant this platform claims has a named test, and CI refuses to run without it (Gate 2.2).

A coverage percentage can be satisfied by testing the easy parts while the money and PHI paths stay
untested. It is a proxy. What must actually never regress is the set of guarantees the design documents
claim — and those guarantees live in prose, in a dozen files, with nothing connecting a sentence in
`38-policy-member-administration.md` to the test that proves it.

docs/quality/invariant-registry.yaml makes that link explicit, and this checker keeps it honest:

  * every named test must EXIST (renaming one without updating the registry fails the build)
  * no named test may be unconditionally skipped ([Fact(Skip=...)], Skip.If(true, ...)) — a permanently
    skipped invariant test is worse than a missing one, because it reports green
  * an entry with no tests at all is reported as UNPROVEN, which is the registry's real job: making the
    absence of a test as visible as its failure

When it fails it prints the invariant's own SENTENCE, not just an id, because the person who broke it is
usually not the person who wrote it.

    check-invariant-registry.py [--registry FILE] [--allow-unproven] [--selftest]
"""
from __future__ import annotations

import argparse
import os
import re
import sys
import tempfile

try:
    import yaml
except ImportError:
    sys.exit("::error::check-invariant-registry needs PyYAML (pip install pyyaml)")

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
REGISTRY = os.path.join(REPO, "docs", "quality", "invariant-registry.yaml")

NAMESPACE_RE = re.compile(r"^\s*namespace\s+([A-Za-z0-9_.]+)\s*;?", re.M)
CLASS_RE = re.compile(r"^\s*(?:public|internal)\s+(?:sealed\s+|abstract\s+|partial\s+|static\s+)*class\s+([A-Za-z0-9_]+)", re.M)
# [Fact], [Theory], [SkippableFact], [SkippableTheory], with or without arguments.
ATTR_RE = re.compile(r"\[\s*(Skippable)?(Fact|Theory)\s*(\((?P<args>[^\]]*)\))?\s*\]")
METHOD_RE = re.compile(r"(?:public|internal|private)\s+(?:static\s+)?(?:async\s+)?[A-Za-z0-9_<>,\[\]\? ]+\s+([A-Za-z0-9_]+)\s*\(")


def index_tests(roots: list[str]) -> dict[str, dict]:
    """FQN -> {file, skipped, reason}. Source-scanned, so it needs no build and runs in a second."""
    out: dict[str, dict] = {}
    for root in roots:
        for dirpath, _dirs, files in os.walk(root):
            if f"{os.sep}bin{os.sep}" in dirpath or f"{os.sep}obj{os.sep}" in dirpath:
                continue
            for name in files:
                if not name.endswith(".cs"):
                    continue
                path = os.path.join(dirpath, name)
                try:
                    src = open(path, encoding="utf-8").read()
                except OSError:
                    continue
                ns_match = NAMESPACE_RE.search(src)
                if not ns_match:
                    continue
                ns = ns_match.group(1)

                # Walk attributes in order; the class in scope is the nearest class declaration above.
                class_positions = [(m.start(), m.group(1)) for m in CLASS_RE.finditer(src)]
                attrs = list(ATTR_RE.finditer(src))
                for i, m in enumerate(attrs):
                    cls = None
                    for pos, cname in class_positions:
                        if pos < m.start():
                            cls = cname
                        else:
                            break
                    if cls is None:
                        continue
                    # A method's region ends where the NEXT test attribute begins. Reading a fixed number
                    # of characters instead runs past the closing brace into the following method, so a
                    # live test inherits its neighbour's Skip and is reported as skipped — which would make
                    # this guard fail builds for tests that are perfectly fine.
                    region_end = attrs[i + 1].start() if i + 1 < len(attrs) else len(src)
                    region = src[m.end():region_end]
                    mm = METHOD_RE.search(region)
                    if not mm:
                        continue
                    method = mm.group(1)
                    fqn = f"{ns}.{cls}.{method}"

                    args = m.group("args") or ""
                    skipped = "Skip" in args and "=" in args
                    reason = "attribute-level Skip" if skipped else ""

                    # An unconditional Skip.If(true, ...) inside the body is the same lie by another route.
                    if re.search(r"Skip\.If\(\s*true\b", region[mm.end():]):
                        skipped, reason = True, "Skip.If(true, ...) in the body"

                    out[fqn] = {"file": os.path.relpath(path, REPO), "skipped": skipped, "reason": reason}
    return out


def check(registry_path: str, index: dict[str, dict], allow_unproven: bool) -> tuple[list[str], list[str]]:
    doc = yaml.safe_load(open(registry_path, encoding="utf-8"))
    entries = doc.get("invariants") or []
    errors: list[str] = []
    unproven: list[str] = []

    seen_ids = set()
    for e in entries:
        iid = e.get("id", "<no id>")
        statement = (e.get("statement") or "").strip()
        if iid in seen_ids:
            errors.append(f"{iid}: duplicate id")
        seen_ids.add(iid)
        if not statement:
            errors.append(f"{iid}: has no statement — an invariant nobody can read is not a control")

        tests = e.get("tests") or []
        if not tests:
            unproven.append(f"{iid} — {statement}  (source: {e.get('source', '?')})")
            continue

        for t in tests:
            # A non-.NET test (vitest/playwright) is named `path::name`; those are verified by their own
            # runners, so the registry records them without a source-scan claim it cannot honour.
            if "::" in t:
                target = t.split("::", 1)[0]
                if not os.path.exists(os.path.join(REPO, target)):
                    errors.append(f"{iid}: names '{t}' but {target} does not exist. {statement}")
                continue
            info = index.get(t)
            if info is None:
                errors.append(f"{iid}: names test '{t}' which does not exist "
                              f"(renamed or deleted?).\n        INVARIANT: {statement}")
            elif info["skipped"]:
                errors.append(f"{iid}: test '{t}' is permanently skipped ({info['reason']}) in "
                              f"{info['file']} — it reports green while proving nothing.\n"
                              f"        INVARIANT: {statement}")
    return errors, unproven


def selftest() -> int:
    ok = True
    with tempfile.TemporaryDirectory() as tmp:
        tests_dir = os.path.join(tmp, "svc", "Tests")
        os.makedirs(tests_dir)
        open(os.path.join(tests_dir, "T.cs"), "w").write(
            "namespace Mersal.Svc.Tests;\n"
            "public class GoodTests\n{\n"
            "    [Fact]\n    public void It_holds() { }\n"
            "    [Fact(Skip = \"later\")]\n    public void It_is_skipped() { }\n"
            "    [SkippableFact]\n    public async Task It_is_conditionally_skipped()\n"
            "    { Skip.If(true, \"always\"); }\n"
            "}\n")
        index = index_tests([tmp])

        for expect in ("Mersal.Svc.Tests.GoodTests.It_holds",
                       "Mersal.Svc.Tests.GoodTests.It_is_skipped",
                       "Mersal.Svc.Tests.GoodTests.It_is_conditionally_skipped"):
            if expect not in index:
                print(f"FAIL: index missed {expect}"); ok = False
        if index.get("Mersal.Svc.Tests.GoodTests.It_holds", {}).get("skipped"):
            print("FAIL: a live test was reported as skipped"); ok = False
        if not index.get("Mersal.Svc.Tests.GoodTests.It_is_skipped", {}).get("skipped"):
            print("FAIL: [Fact(Skip=...)] was not detected"); ok = False
        if not index.get("Mersal.Svc.Tests.GoodTests.It_is_conditionally_skipped", {}).get("skipped"):
            print("FAIL: Skip.If(true, ...) was not detected"); ok = False

        def reg(body: str) -> str:
            p = os.path.join(tmp, "r.yaml")
            open(p, "w").write(body)
            return p

        # 1. a good entry passes
        errs, unp = check(reg("invariants:\n  - id: I1\n    statement: it holds\n    source: doc\n"
                              "    tests: [Mersal.Svc.Tests.GoodTests.It_holds]\n"), index, False)
        if errs:
            print(f"FAIL: a valid entry should pass, got {errs}"); ok = False

        # 2. a renamed/deleted test fails
        errs, _ = check(reg("invariants:\n  - id: I2\n    statement: it holds\n    source: doc\n"
                            "    tests: [Mersal.Svc.Tests.GoodTests.Gone]\n"), index, False)
        if not errs:
            print("FAIL: a missing test must fail the build"); ok = False

        # 3. a skipped test fails — the case that reports green while proving nothing
        errs, _ = check(reg("invariants:\n  - id: I3\n    statement: it holds\n    source: doc\n"
                            "    tests: [Mersal.Svc.Tests.GoodTests.It_is_skipped]\n"), index, False)
        if not errs:
            print("FAIL: a permanently skipped test must fail the build"); ok = False

        # 4. an entry with no tests is UNPROVEN, not an error — deleting it to go green is the thing
        #    the registry exists to prevent
        errs, unp = check(reg("invariants:\n  - id: I4\n    statement: nothing proves this\n"
                              "    source: doc\n    tests: []\n"), index, False)
        if errs or len(unp) != 1:
            print(f"FAIL: an empty tests[] should be reported as unproven, got errs={errs} unproven={unp}")
            ok = False

    print("selftest: PASS — detects missing, renamed, attribute-skipped, Skip.If(true) and unproven"
          if ok else "selftest: FAIL")
    return 0 if ok else 1


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--registry", default=REGISTRY)
    ap.add_argument("--allow-unproven", action="store_true",
                    help="report entries with no tests without failing (Gate 3 is still filling them in)")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()

    if a.selftest:
        return selftest()

    index = index_tests([os.path.join(REPO, "services"), os.path.join(REPO, "libs"),
                         os.path.join(REPO, "tools")])
    errors, unproven = check(a.registry, index, a.allow_unproven)

    print(f"invariant registry: {len(index)} test methods indexed")
    if unproven:
        print(f"\n{len(unproven)} invariant(s) with NO test — Gate 3 work items:")
        for u in unproven:
            print(f"  - {u}")
    if errors:
        print("\n::error::invariant registry is broken:")
        for e in errors:
            print(f"  - {e}")
        return 1
    if unproven and not a.allow_unproven:
        print("\n::error::every invariant must name at least one test "
              "(pass --allow-unproven while Gate 3 is in flight)")
        return 1
    print("\ninvariant registry: OK — every named test exists and runs.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
