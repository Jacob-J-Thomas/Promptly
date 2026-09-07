#!/usr/bin/env python3
"""Collect truthful, read-only AIDLC evidence for Promptly.

This inventory never builds, starts services, runs product tests, contacts a
provider, changes files, or certifies acceptance. Missing evidence is reported.
"""

from __future__ import annotations

import argparse
import fnmatch
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Any
from urllib.parse import urlsplit, urlunsplit

SCHEMA_VERSION = 1
REQUIRED_FILES = (
    "AGENTS.md",
    "docs/AIDLC_DELIVERY_CONVENTIONS.md",
    "docs/VERIFICATION.md",
    ".github/pull_request_template.md",
)
GENERATED_DIRS = {
    ".git", ".playwright-cli", ".venv", "bin", "build", "coverage", "dist",
    "node_modules", "obj", "output", "packages", "TestResults", "vendor",
}


def git(root: Path, *args: str) -> tuple[int, str, str]:
    """Run a read-only Git command."""
    try:
        result = subprocess.run(["git", *args], cwd=root, check=False,
                                capture_output=True, text=True)
    except OSError as exc:
        return 127, "", str(exc)
    return result.returncode, result.stdout.strip(), result.stderr.strip()


def finding(identifier: str, status: str, message: str, evidence: str | None = None) -> dict[str, str]:
    item = {"id": identifier, "status": status, "message": message}
    if evidence:
        item["evidence"] = evidence
    return item


def snapshot_files(root: Path) -> tuple[list[str], set[str]]:
    """Read tracked and nonignored untracked paths without scanning dependencies."""
    tracked_code, tracked_output, _ = git(root, "ls-files", "--cached")
    untracked_code, untracked_output, _ = git(root, "ls-files", "--others", "--exclude-standard")
    if tracked_code == 0 and untracked_code == 0:
        tracked = set(tracked_output.splitlines())
        return sorted(tracked | set(untracked_output.splitlines())), tracked
    paths = [
        path.relative_to(root).as_posix()
        for path in root.rglob("*")
        if path.is_file() and not any(part in GENERATED_DIRS for part in path.relative_to(root).parts)
    ]
    return sorted(paths), set()


def discover(files: list[str], patterns: tuple[str, ...]) -> list[str]:
    result = []
    for path in files:
        if any(part in GENERATED_DIRS for part in Path(path).parts):
            continue
        if any(fnmatch.fnmatch(path, pattern) or fnmatch.fnmatch(path, pattern.removeprefix("**/")) for pattern in patterns):
            result.append(path)
    return sorted(set(result))


def redact_remote(value: str) -> str:
    """Remove URL userinfo/query/fragment and scp-style remote userinfo."""
    if "://" in value:
        try:
            parsed = urlsplit(value)
            return urlunsplit((parsed.scheme, parsed.netloc.rsplit("@", 1)[-1], parsed.path, "", ""))
        except ValueError:
            return "<invalid remote redacted>"
    if "@" in value and ":" in value.split("@", 1)[1]:
        return value.split("@", 1)[1]
    return value


def collect(root: Path) -> dict[str, Any]:
    files, tracked = snapshot_files(root)
    findings: list[dict[str, str]] = []
    code, top, error = git(root, "rev-parse", "--show-toplevel")
    findings.append(finding("repository", "PASS", "Git repository identified", top) if code == 0
                    else finding("repository", "FAIL", "Unable to identify a Git repository", error))
    code, head, error = git(root, "rev-parse", "HEAD")
    valid_head = code == 0 and re.fullmatch(r"[0-9a-f]{40}", head or "")
    findings.append(finding("head-sha", "PASS", "Current HEAD is an exact commit SHA", head) if valid_head
                    else finding("head-sha", "FAIL", "Current HEAD is unavailable or not a commit", error or head))
    branch_code, branch, _ = git(root, "branch", "--show-current")
    upstream_code, upstream, _ = git(root, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}")
    dirty_code, dirty, dirty_error = git(root, "status", "--porcelain")
    if dirty_code == 0:
        findings.append(finding("worktree", "INFO", "Worktree has changes; preserve them and do not use this as acceptance", dirty) if dirty
                        else finding("worktree", "PASS", "Worktree is clean"))
    else:
        findings.append(finding("worktree", "FAIL", "Unable to inspect worktree", dirty_error))

    missing = [path for path in REQUIRED_FILES if not (root / path).is_file()]
    findings.append(finding("required-files", "MISSING", "Required delivery documents are missing", ", ".join(missing)) if missing
                    else finding("required-files", "PASS", "Required delivery documents are present"))
    workflows = discover(files, (".github/workflows/*",))
    findings.append(finding("workflows", "INFO", "Workflow files are present; hosted receipts are still required", ", ".join(workflows)) if workflows
                    else finding("workflows", "MISSING", "No committed GitHub workflow provides hosted gate receipts"))
    tests = discover(files, ("**/*Tests.csproj", "**/*Test.csproj", "**/*_test.py", "**/test_*.py",
                             "**/*.test.ts", "**/*.test.tsx", "**/*.spec.ts", "**/*.spec.tsx"))
    tests = [path for path in tests if not path.startswith("scripts/")]
    findings.append(finding("behavior-tests", "INFO", "Product test files or projects are present; execution receipts are still required", ", ".join(tests)) if tests
                    else finding("behavior-tests", "MISSING", "No committed .NET, Python, frontend, SDK, CLI, or browser behavior tests were found"))
    coverage = discover(files, ("**/*coverage*", "**/*coverlet*", "**/*.runsettings", "**/codecov.yml", "**/.coveragerc"))
    findings.append(finding("coverage-policy", "INFO", "Coverage candidates are present; exact per-component receipts are still required", ", ".join(coverage)) if coverage
                    else finding("coverage-policy", "MISSING", "No executable per-component coverage receipt/configuration was found; target is >=90% meaningful coverage"))
    verifiers = discover(files, ("scripts/verify.sh", "scripts/verify.ps1", "scripts/verify.py"))
    findings.append(finding("authoritative-verify", "INFO", "Verification scripts exist; fresh exact-head receipts are still required", ", ".join(verifiers)) if verifiers
                    else finding("authoritative-verify", "MISSING", "No authoritative full verification script exists"))

    remote_code, remote, _ = git(root, "remote", "get-url", "origin")
    remote = redact_remote(remote) if remote_code == 0 else ""
    findings.append(finding("remote", "PASS", "Origin remote identified", remote) if remote
                    else finding("remote", "MISSING", "Origin remote URL is unavailable"))
    wt_code, wt_output, _ = git(root, "worktree", "list", "--porcelain")
    worktrees = [line.removeprefix("worktree ") for line in wt_output.splitlines() if line.startswith("worktree ")] if wt_code == 0 else []
    findings.append(finding("worktrees", "INFO", "Git worktree inventory captured; preserve unrelated worktrees", ", ".join(worktrees)) if wt_code == 0
                    else finding("worktrees", "MISSING", "Git worktree inventory is unavailable"))
    tools = {name: bool(shutil.which(name)) for name in ("dotnet", "python3", "npm", "docker")}
    missing_tools = [name for name, present in tools.items() if not present]
    findings.append(finding("toolchain", "INFO", "Some optional local probes cannot run on this host", ", ".join(missing_tools)) if missing_tools
                    else finding("toolchain", "PASS", "Core local probe executables are available"))
    for tool, present in list(tools.items()):
        if present:
            try:
                version = subprocess.run([tool, "--version"], cwd=root, check=False,
                                         capture_output=True, text=True, timeout=10)
                tools[f"{tool}Version"] = (version.stdout or version.stderr).strip()
            except (OSError, subprocess.TimeoutExpired) as exc:
                tools[f"{tool}Version"] = f"unavailable: {exc}"
    gate_ids = "exact-head-repository-verify, component-behavior-tests, full-stack-browser-e2e, security-dependency, per-component-coverage, independent-full-review, independent-fix-delta-review"
    findings.append(finding("acceptance-boundary", "MISSING", "Required exact-head gates and review receipts are not established by preflight", gate_ids))
    counts: dict[str, int] = {}
    for item in findings:
        counts[item["status"]] = counts.get(item["status"], 0) + 1
    return {
        "schemaVersion": SCHEMA_VERSION, "kind": "promptly-aidlc-preflight",
        "mode": "diagnostic", "acceptance": False,
        "repository": {"root": str(root), "branch": branch if branch_code == 0 else None,
                        "upstream": upstream if upstream_code == 0 else None,
                        "headSha": head if valid_head else None, "remoteUrl": remote or None},
        "worktree": {"dirty": bool(dirty) if dirty_code == 0 else None},
        "inventory": {"trackedFileCount": len(tracked), "workingSnapshotFileCount": len(files),
                       "untrackedFileCount": len(set(files) - tracked), "workflows": workflows,
                       "testFiles": tests, "coverageCandidates": coverage,
                       "verificationScripts": verifiers, "worktrees": worktrees},
        "tools": tools, "findings": findings, "statusCounts": counts,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--format", choices=("text", "json"), default="text")
    parser.add_argument("--strict", action="store_true", help="fail when any finding is FAIL or MISSING")
    args = parser.parse_args()
    report = collect(args.root.resolve())
    if args.format == "json":
        print(json.dumps(report, indent=2, sort_keys=True))
    else:
        repo = report["repository"]
        print(f"AIDLC preflight: {repo['root']}")
        print(f"mode=diagnostic acceptance=false head={repo['headSha'] or 'MISSING'}")
        for item in report["findings"]:
            evidence = f" [{item['evidence']}]" if item.get("evidence") else ""
            print(f"{item['status']} {item['id']}: {item['message']}{evidence}")
        print("statusCounts: " + ", ".join(f"{key}={value}" for key, value in sorted(report["statusCounts"].items())))
        print("This is diagnostic inventory; it cannot certify acceptance.")
    return 1 if args.strict and any(item["status"] in {"FAIL", "MISSING"} for item in report["findings"]) else 0


if __name__ == "__main__":
    sys.exit(main())
