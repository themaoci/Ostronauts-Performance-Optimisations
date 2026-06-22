#!/usr/bin/env python3
"""
Ostronauts PerfOpt Release Script
Usage: py release.py <version> [changelog_file]

If changelog_file is omitted, looks for CHANGELOG_<version>.md

Example:
  py release.py v5.2.0
  py release.py v5.2.0 CHANGELOG_v5.2.0.md
"""

import os
import sys
import re
import subprocess
import json
from pathlib import Path

REPO_DIR = Path(__file__).parent.resolve()
CSPROJ = REPO_DIR / "OstronautsPerfOpt.csproj"
PLUGIN_CS = REPO_DIR / "Plugin.cs"
BIN_DIR = REPO_DIR / "bin" / "Release" / "netstandard2.1"
DLL_NAME = "OstronautsPerfOpt.dll"


def fail(msg):
    print(f"[FAIL] {msg}")
    sys.exit(1)


def step(msg):
    print(f"[...] {msg}")


def ok(msg):
    print(f"[ OK ] {msg}")


def check_dependencies():
    missing = []
    if not subprocess.run(["where", "dotnet"], capture_output=True, silent=True).returncode == 0:
        missing.append("dotnet (install .NET SDK)")
    if not subprocess.run(["where", "gh"], capture_output=True, silent=True).returncode == 0:
        missing.append("gh (install GitHub CLI)")
    if not subprocess.run(["gh", "auth", "status"], capture_output=True, silent=True).returncode == 0:
        missing.append("gh not authenticated (run: gh auth login)")
    if missing:
        fail("Missing dependencies:\n  " + "\n  ".join(missing))
    ok("Dependencies OK")


def read_current_version():
    text = PLUGIN_CS.read_text(encoding="utf-8")
    m = re.search(r'"([\d]+\.[\d]+\.[\d]+)"\)]\s*$', text, re.MULTILINE)
    if not m:
        fail(f"Could not parse version from {PLUGIN_CS}")
    return m.group(1)


def validate_version(version):
    if not re.match(r'^v?\d+\.\d+\.\d+$', version):
        fail(f"Version must be in format vX.Y.Z or X.Y.Z, got: {version}")
    version = version.lstrip("v")
    return version


def find_changelog(version, cl_file):
    if cl_file:
        path = REPO_DIR / cl_file
        if not path.exists():
            fail(f"Changelog file not found: {path}")
        return path
    candidates = [
        REPO_DIR / f"CHANGELOG_v{version}.md",
        REPO_DIR / f"CHANGELOG_{version}.md",
        REPO_DIR / "CHANGELOG.md",
    ]
    for c in candidates:
        if c.exists():
            return c
    fail(f"No changelog found. Create CHANGELOG_v{version}.md or pass a filename.")


def check_git_clean():
    r = subprocess.run(["git", "status", "--porcelain"], capture_output=True, text=True, cwd=REPO_DIR)
    if r.stdout.strip():
        print("[WARN] Uncommitted changes:")
        for line in r.stdout.strip().split("\n"):
            print(f"       {line}")
        ans = input("       Continue anyway? [y/N] ").strip().lower()
        if ans != "y":
            sys.exit(0)
    ok("Git working tree clean (or confirmed)")


def build():
    step("Building Release...")
    r = subprocess.run(
        ["dotnet", "build", "-c", "Release", "--no-restore"],
        capture_output=True, text=True, cwd=REPO_DIR
    )
    if r.returncode != 0:
        # Check if only deploy errors (DLL locked by game)
        errors = [l for l in r.stderr.split("\n") if "error" in l.lower()]
        deploy_errors = [l for l in errors if "MSB302" in l]
        other_errors = [l for l in errors if "MSB302" not in l]
        if other_errors:
            print(r.stderr)
            fail("Build failed with compilation errors")
        if deploy_errors:
            print("[WARN] Build succeeded but deploy failed (game has DLL locked)")
            print("       The DLL is in bin/Release/netstandard2.1/")
    else:
        ok("Build succeeded")

    dll_path = BIN_DIR / DLL_NAME
    if not dll_path.exists():
        fail(f"DLL not found at {dll_path}")
    ok(f"DLL ready: {dll_path} ({dll_path.stat().st_size / 1024:.0f} KB)")
    return dll_path


def create_tag(version):
    tag = f"v{version}"
    step(f"Creating git tag {tag}...")
    r = subprocess.run(["git", "tag", tag], capture_output=True, text=True, cwd=REPO_DIR)
    if r.returncode != 0:
        if "already exists" in r.stderr:
            print(f"[WARN] Tag {tag} already exists")
            ans = input("       Overwrite? [y/N] ").strip().lower()
            if ans == "y":
                subprocess.run(["git", "tag", "-d", tag], cwd=REPO_DIR)
                subprocess.run(["git", "tag", tag], cwd=REPO_DIR)
            else:
                sys.exit(0)
        else:
            fail(f"Failed to create tag: {r.stderr}")
    ok(f"Tag {tag} created")


def create_release(version, changelog_path, dll_path):
    tag = f"v{version}"
    title = f"v{version}"
    notes = changelog_path.read_text(encoding="utf-8")

    step("Creating GitHub release...")
    r = subprocess.run(
        ["gh", "release", "create", tag, "--title", title, "--notes", notes, dll_path],
        capture_output=True, text=True, cwd=REPO_DIR
    )
    if r.returncode != 0:
        fail(f"Release creation failed:\n{r.stderr}")
    ok(f"Release {tag} created: {r.stdout.strip()}")


def main():
    if len(sys.argv) < 2 or sys.argv[1] in ("-h", "--help"):
        print(__doc__)
        sys.exit(0)

    raw_version = sys.argv[1]
    cl_file = sys.argv[2] if len(sys.argv) > 2 else None

    print("=== Ostronauts PerfOpt Release Script ===\n")

    check_dependencies()
    version = validate_version(raw_version)
    current = read_current_version()
    print(f"  Current version in Plugin.cs: {current}")
    print(f"  Requested version:             {version}")
    if version != current:
        print(f"[WARN] Version mismatch! Plugin.cs has {current}, you requested {version}")
        ans = input("       Continue anyway? [y/N] ").strip().lower()
        if ans != "y":
            sys.exit(0)

    changelog_path = find_changelog(version, cl_file)
    print(f"  Changelog: {changelog_path}")

    check_git_clean()
    dll_path = build()
    create_tag(version)
    create_release(version, changelog_path, dll_path)

    print("\n=== Release complete! ===")


if __name__ == "__main__":
    main()
