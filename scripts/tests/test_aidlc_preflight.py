#!/usr/bin/env python3
"""Small standard-library tests for the read-only preflight inventory."""

from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))
import aidlc_preflight  # noqa: E402


class AidlcPreflightTests(unittest.TestCase):
    def test_baseline_surfaces_missing_acceptance_evidence(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            subprocess.run(["git", "init", "-q"], cwd=root, check=True)

            report = aidlc_preflight.collect(root)
            statuses = {item["id"]: item["status"] for item in report["findings"]}

            self.assertFalse(report["acceptance"])
            self.assertEqual(statuses["workflows"], "MISSING")
            self.assertEqual(statuses["behavior-tests"], "MISSING")
            self.assertEqual(statuses["authoritative-verify"], "MISSING")
            self.assertEqual(statuses["acceptance-boundary"], "MISSING")

    def test_inventory_recognizes_product_artifacts(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for relative in aidlc_preflight.REQUIRED_FILES:
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("coverage target >=90%\n", encoding="utf-8")
            (root / ".github/workflows/verify.yml").parent.mkdir(parents=True, exist_ok=True)
            (root / ".github/workflows/verify.yml").write_text("name: Verify\n", encoding="utf-8")
            (root / "tests/Promptly.Tests.csproj").parent.mkdir(parents=True, exist_ok=True)
            (root / "tests/Promptly.Tests.csproj").write_text("<Project />\n", encoding="utf-8")
            (root / "coverage.runsettings").write_text("coverage\n", encoding="utf-8")
            (root / "node_modules/fake/fake.test.ts").parent.mkdir(parents=True, exist_ok=True)
            (root / "node_modules/fake/fake.test.ts").write_text("dependency fixture\n", encoding="utf-8")
            (root / "vendor/fake.Tests.csproj").parent.mkdir(parents=True, exist_ok=True)
            (root / "vendor/fake.Tests.csproj").write_text("dependency fixture\n", encoding="utf-8")
            subprocess.run(["git", "init", "-q"], cwd=root, check=True)
            subprocess.run(["git", "add", "."], cwd=root, check=True)
            subprocess.run(
                [
                    "git",
                    "-c",
                    "user.name=Preflight Test",
                    "-c",
                    "user.email=preflight@example.invalid",
                    "commit",
                    "-qm",
                    "fixture",
                ],
                cwd=root,
                check=True,
            )

            report = aidlc_preflight.collect(root)
            statuses = {item["id"]: item["status"] for item in report["findings"]}

            self.assertEqual(statuses["required-files"], "PASS")
            self.assertEqual(statuses["workflows"], "INFO")
            self.assertEqual(statuses["behavior-tests"], "INFO")
            self.assertEqual(statuses["coverage-policy"], "INFO")
            self.assertEqual(report["inventory"]["testFiles"], ["tests/Promptly.Tests.csproj"])
            self.assertEqual(report["inventory"]["coverageCandidates"], ["coverage.runsettings"])
            self.assertEqual(len(report["repository"]["headSha"]), 40)

    def test_json_cli_is_machine_readable_and_non_accepting(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            completed = subprocess.run(
                [sys.executable, str(SCRIPTS / "aidlc_preflight.py"), "--root", str(root), "--format", "json"],
                check=True,
                capture_output=True,
                text=True,
            )

            report = json.loads(completed.stdout)
            self.assertEqual(report["kind"], "promptly-aidlc-preflight")
            self.assertFalse(report["acceptance"])
            self.assertEqual(report["mode"], "diagnostic")


if __name__ == "__main__":
    unittest.main()
