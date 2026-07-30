#!/usr/bin/env python3
"""Merge every coverage.cobertura.xml into ONE honest per-module report.

WHY THIS EXISTS (phase 24, Gate 0)
----------------------------------
`dotnet test` on a solution emits one cobertura report per TEST assembly, and each report covers every
assembly that test project loaded — not just the code it owns. `libs/authz`, `libs/auth`, `libs/events`
and each service's Domain therefore appear in many reports at once.

coverage-gate.sh summed them:

    for f in files:
        for cls in ...:
            for ln in ...:
                val += 1; cov += hit

so a shared library was counted once per test project that referenced it, and — because a project that
merely LOADS a library without exercising it contributes that library's full line count with almost no
hits — every extra test project dragged the aggregate down. That is a denominator that grows with the
number of test projects rather than with the amount of code, and it is why "overall" fell 19 points
(45 -> 25.7) while domain, which is far less shared, fell 11 (58 -> 46.9).

This tool merges by UNION instead: a physical source line is counted ONCE, and is covered if ANY test
run covered it. That is what "how much of this codebase is exercised" actually means.

It changes only the MEASUREMENT. No floor is altered here, and the domain denominator is not reduced by
excluding hand-written code — see tools/ci/coverage-exclusions.txt for the (generated-code-only) list.

Usage:
    coverage-report.py [RESULTS_DIR] [--json OUT] [--markdown OUT] [--exclusions FILE] [--selftest]
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import sys
import tempfile
import xml.etree.ElementTree as ET
from collections import defaultdict

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DEFAULT_EXCLUSIONS = os.path.join(REPO, "tools", "ci", "coverage-exclusions.txt")


# ---------------------------------------------------------------- path resolution

def load_exclusions(path: str) -> list[str]:
    """Prefix patterns, one per line. `#` comments carry the REASON and are required by the guard."""
    if not path or not os.path.exists(path):
        return []
    out = []
    for raw in open(path, encoding="utf-8"):
        line = raw.split("#", 1)[0].strip()
        if line:
            out.append(line.replace("\\", "/").lstrip("/"))
    return out


def resolve(filename: str, sources: list[str]) -> str:
    """Cobertura filenames are relative to one of the <source> roots. Join against the root that actually
    produces a real path, so the same physical file merges across reports written with different roots."""
    fn = filename.replace("\\", "/")
    if os.path.isabs(fn):
        full = fn
    else:
        full = None
        for src in sources:
            cand = os.path.normpath(os.path.join(src, fn))
            if os.path.exists(cand):
                full = cand
                break
        if full is None:
            # Unresolvable against any source (a file deleted since the run). Keep it keyed distinctly
            # rather than dropping it — silently discarding lines is how a denominator lies.
            full = os.path.normpath(os.path.join(sources[0] if sources else REPO, fn))
    rel = os.path.relpath(full, REPO).replace("\\", "/")
    return rel


def module_of(rel: str) -> tuple[str, str]:
    """(module, layer) for a repo-relative path. Module is the deployable/owning unit."""
    parts = rel.split("/")
    if len(parts) >= 2 and parts[0] in ("services", "libs", "tools", "apps"):
        module = f"{parts[0]}/{parts[1]}"
        layer = parts[2] if len(parts) > 3 and parts[2] in ("Domain", "Infrastructure", "Api", "Tests") else "other"
        # libs are flat: libs/authz/Foo.cs has no layer directory, and is domain-ish logic either way.
        if parts[0] == "libs" and layer == "other":
            layer = "Tests" if "/Tests/" in rel else "Lib"
        return module, layer
    return parts[0] if parts else "?", "other"


# ---------------------------------------------------------------- merge

def merge(results_dir: str, exclusions: list[str]) -> dict:
    files = sorted(glob.glob(f"{results_dir}/**/coverage.cobertura.xml", recursive=True))
    if not files:
        sys.exit(f"::error::no coverage.cobertura.xml found under {results_dir}")

    # (rel_path, line_no) -> covered?   UNION across every report.
    hits: dict[tuple[str, int], bool] = {}
    appearances: dict[str, int] = defaultdict(int)  # how many reports mention each file — the duplication proof

    for f in files:
        root = ET.parse(f).getroot()
        sources = [s.text for s in root.iter("source") if s.text]
        seen_here: set[str] = set()
        for cls in root.iter("class"):
            lines = cls.find("lines")
            if lines is None:
                continue
            rel = resolve(cls.get("filename", ""), sources)
            if any(rel.startswith(p) for p in exclusions):
                continue
            if rel not in seen_here:
                appearances[rel] += 1
                seen_here.add(rel)
            for ln in lines.findall("line"):
                try:
                    no = int(ln.get("number", "0"))
                except ValueError:
                    continue
                key = (rel, no)
                hits[key] = hits.get(key, False) or int(ln.get("hits", "0")) > 0

    per_module: dict[tuple[str, str], list[int]] = defaultdict(lambda: [0, 0])  # covered, total
    per_file: dict[str, list[int]] = defaultdict(lambda: [0, 0])
    for (rel, _no), covered in hits.items():
        module, layer = module_of(rel)
        per_module[(module, layer)][1] += 1
        per_module[(module, layer)][0] += 1 if covered else 0
        per_file[rel][1] += 1
        per_file[rel][0] += 1 if covered else 0

    modules = [
        {"module": m, "layer": l, "covered": c, "total": t, "pct": round(c / t * 100, 1) if t else 0.0}
        for (m, l), (c, t) in sorted(per_module.items())
    ]

    def totals(pred) -> dict:
        c = sum(x["covered"] for x in modules if pred(x))
        t = sum(x["total"] for x in modules if pred(x))
        return {"covered": c, "total": t, "pct": round(c / t * 100, 1) if t else 0.0}

    # PRODUCTION = everything that ships. Test code is excluded from the denominator: measuring how much of
    # a test suite the test suite executes is a tautology, and coverlet includes it by default.
    prod = lambda x: x["layer"] != "Tests"
    return {
        "reports_merged": len(files),
        "modules": modules,
        "totals": {
            "overall": totals(prod),
            "domain": totals(lambda x: x["layer"] == "Domain"),
            "tests_excluded_from_denominator": totals(lambda x: x["layer"] == "Tests"),
        },
        "duplication": {
            "files_seen_in_more_than_one_report": sum(1 for n in appearances.values() if n > 1),
            "max_reports_one_file_appeared_in": max(appearances.values()) if appearances else 0,
            "naive_sum_total_lines": None,  # filled by the caller when comparing against the old gate
        },
    }


def naive_total(results_dir: str) -> tuple[int, int]:
    """Reproduce the OLD gate's arithmetic, so the report can state both numbers side by side."""
    cov = val = 0
    for f in glob.glob(f"{results_dir}/**/coverage.cobertura.xml", recursive=True):
        for cls in ET.parse(f).getroot().iter("class"):
            lines = cls.find("lines")
            if lines is None:
                continue
            for ln in lines.findall("line"):
                val += 1
                cov += int(ln.get("hits", "0")) > 0
    return cov, val


def markdown(report: dict) -> str:
    rows = ["| Module | Layer | Covered | Total | % |", "|---|---|--:|--:|--:|"]
    for m in report["modules"]:
        if m["layer"] == "Tests":
            continue
        rows.append(f"| `{m['module']}` | {m['layer']} | {m['covered']} | {m['total']} | {m['pct']}% |")
    t = report["totals"]
    rows += [
        "",
        f"**overall (production code)** {t['overall']['covered']}/{t['overall']['total']} = {t['overall']['pct']}%",
        f"**domain** {t['domain']['covered']}/{t['domain']['total']} = {t['domain']['pct']}%",
        f"_merged {report['reports_merged']} cobertura reports; "
        f"{report['duplication']['files_seen_in_more_than_one_report']} files appeared in more than one "
        f"(max {report['duplication']['max_reports_one_file_appeared_in']})._",
    ]
    return "\n".join(rows)


# ---------------------------------------------------------------- selftest

SELFTEST_REPORT = """<?xml version="1.0"?>
<coverage><sources><source>{src}/</source></sources><packages><package name="P"><classes>
<class name="C" filename="{f}"><lines>
<line number="1" hits="{h1}"/><line number="2" hits="{h2}"/>
</lines></class></classes></package></packages></coverage>
"""


def selftest() -> int:
    """Prove the merge DEDUPLICATES and UNIONS — the two properties the old gate lacked."""
    with tempfile.TemporaryDirectory() as tmp:
        src = os.path.join(REPO, "services", "policy")
        target = "Domain/_selftest_probe.cs"
        os.makedirs(os.path.join(src, "Domain"), exist_ok=True)
        probe = os.path.join(src, target)
        created = not os.path.exists(probe)
        if created:
            open(probe, "w").write("// selftest probe\n// line 2\n")
        try:
            # Two reports naming the SAME file: one covers line 1, the other line 2.
            for i, (h1, h2) in enumerate([("1", "0"), ("0", "1")]):
                d = os.path.join(tmp, f"r{i}")
                os.makedirs(d)
                open(os.path.join(d, "coverage.cobertura.xml"), "w").write(
                    SELFTEST_REPORT.format(src=src, f=target, h1=h1, h2=h2))

            rep = merge(tmp, [])
            dom = rep["totals"]["domain"]
            naive_cov, naive_val = naive_total(tmp)

            ok = True
            if dom["total"] != 2:
                print(f"FAIL: expected 2 unique lines after dedupe, got {dom['total']}"); ok = False
            if dom["covered"] != 2:
                print(f"FAIL: expected union coverage 2, got {dom['covered']}"); ok = False
            if naive_val != 4:
                print(f"FAIL: the naive sum should double-count to 4, got {naive_val}"); ok = False
            if rep["duplication"]["max_reports_one_file_appeared_in"] != 2:
                print("FAIL: duplication not detected"); ok = False

            # And the guard must NOT silently pass a broken merge: exclusions must actually exclude.
            if merge(tmp, ["services/policy/Domain/"])["totals"]["domain"]["total"] != 0:
                print("FAIL: exclusion prefix did not exclude"); ok = False

            print("selftest: PASS — merge dedupes (2 unique vs 4 naive), unions hits, honours exclusions"
                  if ok else "selftest: FAIL")
            return 0 if ok else 1
        finally:
            if created and os.path.exists(probe):
                os.remove(probe)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("results", nargs="?", default="./coverage")
    ap.add_argument("--json", dest="json_out")
    ap.add_argument("--markdown", dest="md_out")
    ap.add_argument("--exclusions", default=DEFAULT_EXCLUSIONS)
    ap.add_argument("--compare-naive", action="store_true",
                    help="also print the old gate's double-counted figure, for the ADR")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()

    if a.selftest:
        return selftest()

    report = merge(a.results, load_exclusions(a.exclusions))
    if a.compare_naive:
        cov, val = naive_total(a.results)
        report["duplication"]["naive_sum_total_lines"] = val
        report["duplication"]["naive_sum_covered_lines"] = cov
        report["duplication"]["naive_pct"] = round(cov / val * 100, 1) if val else 0.0

    if a.json_out:
        with open(a.json_out, "w", encoding="utf-8") as fh:
            json.dump(report, fh, indent=2, sort_keys=True)
            fh.write("\n")
    md = markdown(report)
    if a.md_out:
        open(a.md_out, "w", encoding="utf-8").write(md + "\n")
    print(md)
    if a.compare_naive:
        d = report["duplication"]
        print(f"\nold gate (naive sum, double-counted): {d['naive_sum_covered_lines']}/"
              f"{d['naive_sum_total_lines']} = {d['naive_pct']}%")
    return 0


if __name__ == "__main__":
    sys.exit(main())
