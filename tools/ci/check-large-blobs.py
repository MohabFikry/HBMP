#!/usr/bin/env python3
"""Phase 24 Gate 6.6 — no large binary enters the history again.

A 61 MB CPT code book sat in this repository's history from the very first commit (8208331). Deleting
the file in a later commit changed nothing: every clone still paid for it, because a blob leaves git
history only when the history is rewritten. It took a full mirror backup, a filter-repo pass and a
force-push of every ref to remove — which is precisely why the interesting control is not the purge but
the guard that stops the next one.

Checks the blobs a commit would ADD, not the working tree, so it is meaningful in a pre-commit hook and
in CI alike.

Usage:
  check-large-blobs.py --staged        what `git commit` is about to record (pre-commit hook)
  check-large-blobs.py --range A..B    every blob introduced by a range of commits (CI)
  check-large-blobs.py --all-history   every blob anywhere (the audit form)
  check-large-blobs.py --selftest      prove the size logic and the allow-list
"""
from __future__ import annotations

import subprocess
import sys

LIMIT_BYTES = 5 * 1024 * 1024

# Paths permitted to exceed the limit, each with the reason. Reviewed like a coverage exclusion: every
# entry is weight every clone of this repository carries forever.
ALLOWED: dict[str, str] = {
    "Raw Files/Egyptian Drugs - ATC Classified.csv":
        "6.2 MB. The Egyptian drug master the ATC loader reads (tools/, phase 8). Text, diff-able, and the "
        "source of a table the platform cannot be built without — carried deliberately rather than fetched "
        "at build time, because a formulary that changes under a rebuild changes dispensing decisions.",
}


def run(args: list[str]) -> str:
    return subprocess.run(["git", *args], capture_output=True, text=True, check=True).stdout


def blob_size(sha: str) -> int:
    try:
        return int(run(["cat-file", "-s", sha]).strip())
    except (subprocess.CalledProcessError, ValueError):
        return 0


def staged() -> list[tuple[str, int]]:
    """(path, size) for every blob this commit would add or modify."""
    out: list[tuple[str, int]] = []
    for line in run(["diff", "--cached", "--raw", "--no-renames"]).splitlines():
        # :100644 100644 <old> <new> M\tpath
        if not line.startswith(":"):
            continue
        meta, _, path = line.partition("\t")
        parts = meta.split()
        if len(parts) < 5:
            continue
        new_sha, status = parts[3], parts[4]
        if status.startswith("D") or new_sha == "0" * 40:
            continue
        out.append((path, blob_size(new_sha)))
    return out


def in_range(rev_range: str) -> list[tuple[str, int]]:
    out: dict[str, int] = {}
    for line in run(["rev-list", "--objects", rev_range]).splitlines():
        sha, _, path = line.partition(" ")
        if not path:
            continue
        size = blob_size(sha)
        if size:
            out[path] = max(out.get(path, 0), size)
    return sorted(out.items())


def all_history() -> list[tuple[str, int]]:
    return in_range("--all")


def report(found: list[tuple[str, int]]) -> int:
    over = [(p, s) for p, s in found if s > LIMIT_BYTES]
    unlisted = [(p, s) for p, s in over if p not in ALLOWED]

    for path, size in over:
        mark = "ALLOWED" if path in ALLOWED else "TOO BIG"
        print(f"  [{mark}] {size / 1048576:6.1f} MB  {path}")

    if unlisted:
        print()
        print(f"::error::{len(unlisted)} blob(s) over {LIMIT_BYTES // 1048576} MB.")
        print("A binary committed here is in every clone forever — removing it later means rewriting")
        print("history and force-pushing every ref, which is what Gate 6 had to do for a 61 MB PDF.")
        print("Put it in MinIO and commit a pointer, or add it to ALLOWED with the reason it must live here.")
        return 1

    print(f"large-blob gate: OK — {len(found)} blob(s) checked, {len(over)} allow-listed, none unlisted")
    return 0


def selftest() -> int:
    failures: list[str] = []

    # The threshold is a real boundary, not an approximation.
    if report([("small.bin", LIMIT_BYTES)]) != 0:
        failures.append("a blob exactly at the limit was rejected")
    if report([("big.bin", LIMIT_BYTES + 1)]) != 1:
        failures.append("a blob one byte over the limit was accepted")

    # The allow-list works and is keyed on the exact path.
    listed = next(iter(ALLOWED))
    if report([(listed, 99 * 1048576)]) != 0:
        failures.append("an allow-listed path was rejected")
    if report([(listed + ".copy", 99 * 1048576)]) != 1:
        failures.append("a near-miss path was treated as allow-listed")

    # And the file this gate exists because of must never pass.
    if report([("Raw Files/CPT 2022.PDF", 61 * 1048576)]) != 1:
        failures.append("the purged CPT PDF would be accepted back")

    if failures:
        for f in failures:
            print(f"SELFTEST FAIL: {f}")
        return 1
    print("check-large-blobs selftest: OK")
    return 0


def main(argv: list[str]) -> int:
    if "--selftest" in argv:
        return selftest()
    if "--staged" in argv:
        return report(staged())
    if "--all-history" in argv:
        return report(all_history())
    for i, a in enumerate(argv):
        if a == "--range" and i + 1 < len(argv):
            return report(in_range(argv[i + 1]))
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
