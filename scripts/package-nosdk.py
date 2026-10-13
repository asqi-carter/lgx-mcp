#!/usr/bin/env python3
"""Zip a no-SDK publish directory and refuse to produce a leaky archive.

Usage: package-nosdk.py <publish-dir> <out.zip>
       package-nosdk.py --check <existing.zip>

The check (always run, on the finished zip) fails when any entry's file name
starts with "RockwellAutomation" (case-insensitive) or any path segment is an
``sdk`` directory, or when the zip root lacks LICENSE, NOTICE, README.md, SECURITY.md,
CHANGELOG.md or a non-empty licenses/ or docs/ directory. On failure the zip written by this script is deleted.
"""
from __future__ import annotations

import os
import stat
import sys
import zipfile


REQUIRED_FILES = ("LICENSE", "NOTICE", "README.md", "SECURITY.md", "CHANGELOG.md")
REQUIRED_DIRS = ("licenses", "docs")


def find_missing_required(names: list[str]) -> list[str]:
    """Return violations for required root-level files/dirs absent from ``names``."""
    entries = {n for n in names if n and not n.endswith("/")}
    missing = [f"missing required file at zip root: {f}" for f in REQUIRED_FILES if f not in entries]
    for d in REQUIRED_DIRS:
        if not any(e.startswith(d + "/") for e in entries):
            missing.append(f"missing required directory at zip root: {d}/ (or it is empty)")
    return missing


def find_violations(names: list[str]) -> list[str]:
    """Return human-readable violations for the given zip entry names."""
    bad: list[str] = find_missing_required(names)
    for name in names:
        parts = [p for p in name.split("/") if p]
        if not parts:
            continue
        if parts[-1].lower().startswith("rockwellautomation"):
            bad.append(f"Rockwell binary: {name}")
        # every segment but the last is a directory; a trailing "/" entry is a dir too
        dirs = parts if name.endswith("/") else parts[:-1]
        if any(d.lower() == "sdk" for d in dirs):
            bad.append(f"sdk/ directory entry: {name}")
    return bad


def check_zip(path: str) -> list[str]:
    """Return violations found in the zip at ``path``."""
    with zipfile.ZipFile(path) as zf:
        return find_violations(zf.namelist())


def build_zip(src: str, out: str) -> None:
    """Write the contents of ``src`` (sorted, exec bits preserved) to ``out``."""
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    if os.path.exists(out):
        os.remove(out)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zf:
        for root, dirs, files in os.walk(src):
            dirs.sort()
            for f in sorted(files):
                full = os.path.join(root, f)
                arc = os.path.relpath(full, src).replace(os.sep, "/")
                info = zipfile.ZipInfo.from_file(full, arc)
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = (stat.S_IMODE(os.stat(full).st_mode) | stat.S_IFREG) << 16
                with open(full, "rb") as fh:
                    zf.writestr(info, fh.read())


def main(argv: list[str]) -> int:
    if len(argv) == 3 and argv[1] == "--check":
        bad = check_zip(argv[2])
    elif len(argv) == 3:
        build_zip(argv[1], argv[2])
        bad = check_zip(argv[2])
        if bad:
            os.remove(argv[2])
    else:
        print(__doc__, file=sys.stderr)
        return 2
    if bad:
        for b in bad:
            print(f"  VIOLATION: {b}", file=sys.stderr)
        print("FAIL: no-SDK release zip contains forbidden content", file=sys.stderr)
        return 1
    print(f"Zip guard OK: {argv[-1]}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
