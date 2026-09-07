"""Run Promptly Worker verification identically on local machines and CI."""

from __future__ import annotations

import base64
import hashlib
import json
import os
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any

WORKER_ROOT = Path(__file__).resolve().parents[1]
REPOSITORY_ROOT = WORKER_ROOT.parent
ARTIFACT_ROOT = REPOSITORY_ROOT / "artifacts" / "test-results" / "python"
IMAGE_TAG = "promptly-worker:verification"
MINIMUM_COVERAGE = 0.90


def run(
    command: list[str],
    *,
    capture: bool = False,
    environment: dict[str, str] | None = None,
) -> str:
    """Run a command from the worker directory and stop on any failure."""

    print(f"+ {' '.join(command)}", flush=True)
    completed = subprocess.run(
        command,
        cwd=WORKER_ROOT,
        check=True,
        text=True,
        stdout=subprocess.PIPE if capture else None,
        env=environment,
    )
    return completed.stdout.strip() if capture else ""


def production_sources() -> list[Path]:
    """Return exactly the Python source cohort copied into the production image."""

    sources = [*WORKER_ROOT.glob("*.py"), *WORKER_ROOT.joinpath("routers").rglob("*.py")]
    return sorted(path.relative_to(WORKER_ROOT) for path in sources if path.is_file())


def prepare_artifacts() -> None:
    if ARTIFACT_ROOT.exists():
        shutil.rmtree(ARTIFACT_ROOT)
    for directory in ("coverage", "quality", "security"):
        ARTIFACT_ROOT.joinpath(directory).mkdir(parents=True, exist_ok=True)


def require_file(path: Path) -> None:
    if not path.is_file() or path.stat().st_size == 0:
        raise RuntimeError(f"Required verification artifact is missing or empty: {path}")


def normalize_source(value: str) -> str:
    return Path(value.removeprefix("./")).as_posix()


def audit_postconditions(path: Path, label: str) -> tuple[set[tuple[str, str]], int]:
    audit_data = json.loads(path.read_text(encoding="utf-8"))
    dependencies = audit_data.get("dependencies")
    if not isinstance(dependencies, list) or not dependencies:
        raise RuntimeError(f"{label} pip-audit artifact contains no dependencies")

    audited_packages: set[tuple[str, str]] = set()
    vulnerabilities = 0
    for dependency in dependencies:
        if not isinstance(dependency, dict):
            raise RuntimeError(f"{label} pip-audit artifact contains an invalid dependency")
        name = dependency.get("name")
        version = dependency.get("version")
        findings = dependency.get("vulns")
        if (
            not isinstance(name, str)
            or not isinstance(version, str)
            or not isinstance(findings, list)
        ):
            raise RuntimeError(f"{label} pip-audit artifact has an invalid dependency record")
        audited_packages.add((name.lower(), version))
        vulnerabilities += len(findings)

    if vulnerabilities:
        raise RuntimeError(f"{label} pip-audit reported {vulnerabilities} vulnerabilities")
    return audited_packages, vulnerabilities


def require_full_audit_cohort(
    runtime_packages: set[tuple[str, str]],
    verification_packages: set[tuple[str, str]],
) -> None:
    missing_runtime_packages = runtime_packages - verification_packages
    if missing_runtime_packages:
        raise RuntimeError(
            "Full verification dependency audit omitted runtime packages: "
            f"{sorted(missing_runtime_packages)}"
        )
    if not any(name == "httpx2" for name, _ in verification_packages):
        raise RuntimeError("Full verification dependency audit omitted required httpx2")


def assert_tool_artifacts(sources: list[Path]) -> dict[str, Any]:
    expected_sources = {path.as_posix() for path in sources}
    paths = {
        "junit": ARTIFACT_ROOT / "junit.xml",
        "coverage_xml": ARTIFACT_ROOT / "coverage" / "coverage.xml",
        "coverage_json": ARTIFACT_ROOT / "coverage" / "coverage.json",
        "source_manifest": ARTIFACT_ROOT / "quality" / "production-sources.txt",
        "ruff": ARTIFACT_ROOT / "quality" / "ruff.json",
        "mypy": ARTIFACT_ROOT / "quality" / "mypy.xml",
        "mypy_tests": ARTIFACT_ROOT / "quality" / "mypy-tests.xml",
        "bandit": ARTIFACT_ROOT / "security" / "bandit.json",
        "runtime_requirements": ARTIFACT_ROOT / "security" / "runtime-requirements.txt",
        "runtime_audit": ARTIFACT_ROOT / "security" / "pip-audit.json",
        "verification_requirements": ARTIFACT_ROOT / "security" / "verification-requirements.txt",
        "verification_audit": ARTIFACT_ROOT / "security" / "pip-audit-verification.json",
    }
    for path in paths.values():
        require_file(path)

    manifest_sources = set(paths["source_manifest"].read_text(encoding="utf-8").splitlines())
    if manifest_sources != expected_sources:
        raise RuntimeError(
            f"Production-source manifest mismatch: expected={sorted(expected_sources)}, "
            f"actual={sorted(manifest_sources)}"
        )

    test_report = ET.parse(paths["junit"]).getroot()
    tests = int(test_report.attrib.get("tests", "0"))
    if tests == 0:
        tests = sum(int(suite.attrib.get("tests", "0")) for suite in test_report)
    if tests == 0:
        raise RuntimeError("Worker verification collected zero tests")

    coverage_xml = ET.parse(paths["coverage_xml"]).getroot()
    line_rate = float(coverage_xml.attrib["line-rate"])
    branch_rate = float(coverage_xml.attrib["branch-rate"])
    if line_rate < MINIMUM_COVERAGE or branch_rate < MINIMUM_COVERAGE:
        raise RuntimeError(f"Coverage below 90%: lines={line_rate:.2%}, branches={branch_rate:.2%}")

    coverage_data = json.loads(paths["coverage_json"].read_text(encoding="utf-8"))
    measured_sources = {normalize_source(path) for path in coverage_data["files"]}
    if measured_sources != expected_sources:
        raise RuntimeError(
            f"Coverage cohort mismatch: expected={sorted(expected_sources)}, "
            f"actual={sorted(measured_sources)}"
        )

    if json.loads(paths["ruff"].read_text(encoding="utf-8")):
        raise RuntimeError("Ruff artifact contains findings")

    mypy_report = ET.parse(paths["mypy"]).getroot()
    if int(mypy_report.attrib["errors"]) or int(mypy_report.attrib["failures"]):
        raise RuntimeError("Production-source mypy artifact contains failures")

    mypy_tests_report = ET.parse(paths["mypy_tests"]).getroot()
    if int(mypy_tests_report.attrib["errors"]) or int(mypy_tests_report.attrib["failures"]):
        raise RuntimeError("Test-suite mypy artifact contains failures")

    bandit_data = json.loads(paths["bandit"].read_text(encoding="utf-8"))
    bandit_sources = {
        normalize_source(path) for path in bandit_data["metrics"] if path != "_totals"
    }
    if bandit_sources != expected_sources:
        raise RuntimeError(
            f"Bandit cohort mismatch: expected={sorted(expected_sources)}, "
            f"actual={sorted(bandit_sources)}"
        )
    if bandit_data["errors"] or bandit_data["results"]:
        raise RuntimeError("Bandit artifact contains errors or findings")

    runtime_packages, runtime_vulnerabilities = audit_postconditions(
        paths["runtime_audit"], "Runtime"
    )
    verification_packages, verification_vulnerabilities = audit_postconditions(
        paths["verification_audit"], "Full verification"
    )
    require_full_audit_cohort(runtime_packages, verification_packages)

    return {
        "tests": tests,
        "production_sources": sorted(expected_sources),
        "line_coverage": line_rate,
        "branch_coverage": branch_rate,
        "audited_dependencies": len(runtime_packages),
        "vulnerabilities": runtime_vulnerabilities,
        "verification_audited_dependencies": len(verification_packages),
        "verification_vulnerabilities": verification_vulnerabilities,
    }


def assert_summary(path: Path) -> None:
    require_file(path)
    summary = json.loads(path.read_text(encoding="utf-8"))
    if not summary.get("tests") or not summary.get("production_sources"):
        raise RuntimeError("Verification summary is missing required postconditions")
    if summary.get("line_coverage", 0) < MINIMUM_COVERAGE:
        raise RuntimeError("Verification summary reports insufficient line coverage")
    if summary.get("branch_coverage", 0) < MINIMUM_COVERAGE:
        raise RuntimeError("Verification summary reports insufficient branch coverage")
    if summary.get("vulnerabilities") != 0:
        raise RuntimeError("Verification summary reports runtime dependency vulnerabilities")
    if not summary.get("verification_audited_dependencies"):
        raise RuntimeError("Verification summary reports no full verification dependencies")
    if summary.get("verification_vulnerabilities") != 0:
        raise RuntimeError("Verification summary reports full dependency vulnerabilities")


def assert_compose_isolation() -> None:
    compose_file = str(REPOSITORY_ROOT / "docker" / "docker-compose.yml")
    environment = os.environ.copy()
    environment.pop("JWT__Key", None)
    environment.pop("JWT__RetiredKeyFingerprints", None)
    verification_key = base64.b64encode(secrets.token_bytes(48)).decode("ascii")
    retired_fingerprint = "a" * 64

    with tempfile.TemporaryDirectory(prefix="promptly-compose-verification-") as temp_dir:
        empty_env_file = Path(temp_dir) / "missing.env"
        empty_env_file.write_text("", encoding="utf-8")
        empty_key_env_file = Path(temp_dir) / "empty.env"
        empty_key_env_file.write_text("JWT__Key=\n", encoding="utf-8")
        valid_env_file = Path(temp_dir) / "valid.env"
        valid_env_file.write_text(
            f"JWT__Key={verification_key}\nJWT__RetiredKeyFingerprints={retired_fingerprint}\n",
            encoding="utf-8",
        )

        for key_case, env_file in (
            ("missing", empty_env_file),
            ("empty", empty_key_env_file),
        ):
            compose_command = [
                "docker",
                "compose",
                "--env-file",
                str(env_file),
                "--file",
                compose_file,
                "config",
            ]
            rejected = subprocess.run(
                [*compose_command, "--quiet"],
                cwd=WORKER_ROOT,
                check=False,
                text=True,
                capture_output=True,
                env=environment,
            )
            if rejected.returncode == 0 or "JWT__Key must be set" not in rejected.stderr:
                raise RuntimeError(f"Compose must reject a {key_case} JWT signing key")

        compose_json = run(
            [
                "docker",
                "compose",
                "--env-file",
                str(valid_env_file),
                "--file",
                compose_file,
                "config",
                "--format",
                "json",
            ],
            capture=True,
            environment=environment,
        )
    services = json.loads(compose_json)["services"]
    server_environment = services["promptly-server"]["environment"]
    if "JWT__Key" not in server_environment:
        raise RuntimeError("Compose must forward the injected JWT signing key")
    if "JWT__RetiredKeyFingerprints" not in server_environment:
        raise RuntimeError("Compose must forward retired JWT key fingerprints")
    if server_environment["JWT__Key"] != verification_key:
        raise RuntimeError("Compose must forward the exact injected JWT signing key")
    if server_environment["JWT__RetiredKeyFingerprints"] != retired_fingerprint:
        raise RuntimeError("Compose must forward the exact retired JWT key fingerprints")
    if server_environment["JWT__Key"].startswith("YourSuperSecretJWTKey"):
        raise RuntimeError("Compose must not contain the published legacy JWT signing key")
    worker = services["promptly-eval"]
    if worker.get("ports"):
        raise RuntimeError("Compose must not publish the worker port to the host")
    if "8000" not in {str(port) for port in worker.get("expose", [])}:
        raise RuntimeError("Compose must expose worker port 8000 on its internal network")
    dependency = services["promptly-server"]["depends_on"]["promptly-eval"]
    if dependency["condition"] != "service_healthy":
        raise RuntimeError("The server must wait for a healthy worker")


def assert_jwt_rotation_helper() -> None:
    helper = REPOSITORY_ROOT / "docker" / "fingerprint-jwt-key.sh"
    verification_key_bytes = secrets.token_bytes(48)
    verification_key = base64.b64encode(verification_key_bytes).decode("ascii")

    with tempfile.TemporaryDirectory(prefix="promptly-jwt-rotation-verification-") as temp_dir:
        absent_env_file = Path(temp_dir) / "absent.env"
        absent = subprocess.run(
            ["bash", str(helper), str(absent_env_file)],
            cwd=REPOSITORY_ROOT,
            check=False,
            text=True,
            capture_output=True,
        )
        if absent.returncode == 0:
            raise RuntimeError("JWT rotation helper must reject a missing environment file")

        valid_env_file = Path(temp_dir) / "valid.env"
        valid_env_file.write_text(f"JWT__Key={verification_key}\n", encoding="utf-8")
        valid = subprocess.run(
            ["bash", str(helper), str(valid_env_file)],
            cwd=REPOSITORY_ROOT,
            check=False,
            text=True,
            capture_output=True,
        )
        expected_fingerprint = hashlib.sha256(verification_key_bytes).hexdigest()
        if valid.returncode != 0 or valid.stdout.strip() != expected_fingerprint:
            raise RuntimeError("JWT rotation helper must fingerprint canonical key bytes exactly")

        legacy = subprocess.run(
            ["bash", str(helper), "--legacy-raw", str(valid_env_file)],
            cwd=REPOSITORY_ROOT,
            check=False,
            text=True,
            capture_output=True,
        )
        expected_legacy_fingerprint = hashlib.sha256(verification_key.encode("ascii")).hexdigest()
        if legacy.returncode != 0 or legacy.stdout.strip() != expected_legacy_fingerprint:
            raise RuntimeError("JWT rotation helper must fingerprint legacy literal key bytes")
        if legacy.stdout.strip() == valid.stdout.strip():
            raise RuntimeError("Legacy and canonical JWT fingerprints must use distinct bytes")

        invalid_cases = {
            "missing": "PROMPTLY_LLM_PROVIDER=openai\n",
            "empty": "JWT__Key=\n",
            "duplicate": f"JWT__Key={verification_key}\nJWT__Key={verification_key}\n",
            "malformed": "JWT__Key=not-base64\n",
            "invalid-characters": "JWT__Key=!!!!\n",
            "trailing-junk": "JWT__Key=YWJjZA==junk\n",
        }
        for key_case, contents in invalid_cases.items():
            invalid_env_file = Path(temp_dir) / f"{key_case}.env"
            invalid_env_file.write_text(contents, encoding="utf-8")
            rejected = subprocess.run(
                ["bash", str(helper), str(invalid_env_file)],
                cwd=REPOSITORY_ROOT,
                check=False,
                text=True,
                capture_output=True,
            )
            if rejected.returncode == 0:
                raise RuntimeError(f"JWT rotation helper must reject a {key_case} key entry")
            if verification_key in rejected.stdout or verification_key in rejected.stderr:
                raise RuntimeError("JWT rotation helper must not disclose signing key material")


def assert_image_sources(sources: list[Path]) -> str:
    image_user = run(
        ["docker", "image", "inspect", "--format", "{{.Config.User}}", IMAGE_TAG],
        capture=True,
    )
    if not image_user or image_user in {"0", "root"}:
        raise RuntimeError(f"Production image must run as non-root, found {image_user!r}")

    listing_code = (
        "import json; from pathlib import Path; root=Path('/app'); "
        "paths=[*root.glob('*.py'), *root.joinpath('routers').rglob('*.py')]; "
        "print(json.dumps(sorted(path.relative_to(root).as_posix() for path in paths)))"
    )
    image_sources = set(
        json.loads(
            run(
                [
                    "docker",
                    "run",
                    "--rm",
                    "--entrypoint",
                    "python",
                    IMAGE_TAG,
                    "-c",
                    listing_code,
                ],
                capture=True,
            )
        )
    )
    expected_sources = {path.as_posix() for path in sources}
    if image_sources != expected_sources:
        raise RuntimeError(
            f"Image source mismatch: expected={sorted(expected_sources)}, "
            f"actual={sorted(image_sources)}"
        )
    return image_user


def wait_for_container_endpoint(
    container_id: str,
    path: str,
    attempts: int = 30,
) -> dict[str, Any]:
    probe = (
        "import urllib.request; "
        f"print(urllib.request.urlopen('http://127.0.0.1:8000{path}', timeout=2)"
        ".read().decode('utf-8'))"
    )
    last_error = "container probe did not run"
    for _ in range(attempts):
        completed = subprocess.run(
            ["docker", "exec", container_id, "python", "-c", probe],
            cwd=WORKER_ROOT,
            check=False,
            text=True,
            capture_output=True,
        )
        if completed.returncode == 0:
            try:
                return json.loads(completed.stdout)
            except json.JSONDecodeError as exc:
                last_error = str(exc)
        else:
            last_error = completed.stderr.strip()
        time.sleep(1)
    raise RuntimeError(f"Container endpoint did not become healthy: {path}: {last_error}")


def smoke_test_container() -> dict[str, Any]:
    network_name = f"promptly-worker-verification-{uuid.uuid4().hex[:12]}"
    provider_id = ""
    container_ids: list[str] = []
    run(["docker", "network", "create", "--internal", network_name])
    try:
        provider_id = run(
            [
                "docker",
                "run",
                "--detach",
                "--rm",
                "--network",
                network_name,
                "--network-alias",
                "provider",
                "--mount",
                f"type=bind,source={WORKER_ROOT / 'scripts' / 'provider_stub.py'},"
                "target=/provider_stub.py,readonly",
                "--entrypoint",
                "python",
                IMAGE_TAG,
                "/provider_stub.py",
                "--bind-all",
            ],
            capture=True,
        )
        results: dict[str, Any] = {}
        provider_environments = {
            "openai": ["PROMPTLY_LLM_BASE_URL=http://provider:8080/v1"],
            "azureopenai": [
                "PROMPTLY_LLM_AZURE_ENDPOINT=http://provider:8080",
                "PROMPTLY_LLM_API_VERSION=2024-08-01-preview",
            ],
        }
        for provider, provider_environment in provider_environments.items():
            command = [
                "docker",
                "run",
                "--detach",
                "--rm",
                "--network",
                network_name,
                "--env",
                f"PROMPTLY_LLM_PROVIDER={provider}",
                "--env",
                "PROMPTLY_LLM_API_KEY=verification-only",
                "--env",
                "PROMPTLY_LLM_MODEL_DEFAULT=verification-model",
                "--env",
                "PROMPTLY_LLM_TIMEOUT_SECONDS=5",
            ]
            for environment_value in provider_environment:
                command.extend(("--env", environment_value))
            command.append(IMAGE_TAG)
            container_id = run(command, capture=True)
            container_ids.append(container_id)
            port_bindings = run(
                [
                    "docker",
                    "inspect",
                    "--format",
                    "{{json .HostConfig.PortBindings}}",
                    container_id,
                ],
                capture=True,
            )
            if port_bindings not in {"null", "{}"}:
                raise RuntimeError(f"Worker smoke published a host port: {port_bindings}")
            results[provider] = {
                "host_port_published": False,
                "liveness": wait_for_container_endpoint(container_id, "/health/live"),
                "readiness": wait_for_container_endpoint(container_id, "/health/ready"),
            }
        return results
    except Exception:
        for container_id in container_ids:
            logs = run(["docker", "logs", container_id], capture=True)
            if logs:
                print(logs, file=sys.stderr)
        raise
    finally:
        for running_container in (*container_ids, provider_id):
            if running_container:
                subprocess.run(
                    ["docker", "stop", running_container],
                    cwd=WORKER_ROOT,
                    check=False,
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                )
        subprocess.run(
            ["docker", "network", "rm", network_name],
            cwd=WORKER_ROOT,
            check=False,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )


def main() -> None:
    uv = shutil.which("uv")
    docker = shutil.which("docker")
    if uv is None:
        raise RuntimeError("uv 0.12.3 is required")
    if docker is None:
        raise RuntimeError("Docker with Compose v2 is required")

    uv_version = run([uv, "--version"], capture=True)
    if uv_version.split()[1] != "0.12.3":
        raise RuntimeError(f"uv 0.12.3 is required, found {uv_version}")

    prepare_artifacts()
    sources = production_sources()
    if not sources:
        raise RuntimeError("No production worker sources were found")
    source_arguments = [path.as_posix() for path in sources]
    coverage_targets = sorted(
        {
            path.parts[0] if len(path.parts) > 1 else path.stem
            for path in sources
            if path.name != "__init__.py" or len(path.parts) > 1
        }
    )
    ARTIFACT_ROOT.joinpath("quality", "production-sources.txt").write_text(
        "".join(f"{path}\n" for path in source_arguments), encoding="utf-8"
    )

    run([uv, "lock", "--check"])
    run([uv, "sync", "--frozen", "--all-groups"])
    run([uv, "run", "ruff", "format", "--check", "."])
    run(
        [
            uv,
            "run",
            "ruff",
            "check",
            "--output-format",
            "json",
            "--output-file",
            str(ARTIFACT_ROOT / "quality" / "ruff.json"),
            ".",
        ]
    )
    run(
        [
            uv,
            "run",
            "mypy",
            "--junit-xml",
            str(ARTIFACT_ROOT / "quality" / "mypy.xml"),
            "--junit-format",
            "per_file",
            *source_arguments,
        ]
    )
    run(
        [
            uv,
            "run",
            "mypy",
            "--strict",
            "--junit-xml",
            str(ARTIFACT_ROOT / "quality" / "mypy-tests.xml"),
            "--junit-format",
            "per_file",
            "tests",
        ]
    )
    run(
        [
            uv,
            "run",
            "bandit",
            "--configfile",
            "pyproject.toml",
            "--format",
            "json",
            "--output",
            str(ARTIFACT_ROOT / "security" / "bandit.json"),
            *source_arguments,
        ]
    )
    requirements_path = ARTIFACT_ROOT / "security" / "runtime-requirements.txt"
    run(
        [
            uv,
            "export",
            "--frozen",
            "--no-dev",
            "--no-emit-project",
            "--format",
            "requirements-txt",
            "--quiet",
            "--output-file",
            str(requirements_path),
        ]
    )
    run(
        [
            uv,
            "run",
            "pip-audit",
            "--strict",
            "--require-hashes",
            "--requirement",
            str(requirements_path),
            "--format",
            "json",
            "--output",
            str(ARTIFACT_ROOT / "security" / "pip-audit.json"),
        ]
    )
    verification_requirements_path = ARTIFACT_ROOT / "security" / "verification-requirements.txt"
    run(
        [
            uv,
            "export",
            "--frozen",
            "--all-groups",
            "--no-emit-project",
            "--format",
            "requirements-txt",
            "--quiet",
            "--output-file",
            str(verification_requirements_path),
        ]
    )
    run(
        [
            uv,
            "run",
            "pip-audit",
            "--strict",
            "--require-hashes",
            "--requirement",
            str(verification_requirements_path),
            "--format",
            "json",
            "--output",
            str(ARTIFACT_ROOT / "security" / "pip-audit-verification.json"),
        ]
    )
    run(
        [
            uv,
            "run",
            "pytest",
            f"--junitxml={ARTIFACT_ROOT / 'junit.xml'}",
            *(f"--cov={target}" for target in coverage_targets),
            "--cov-report=term-missing",
            "--cov-report=xml",
            f"--cov-report=json:{ARTIFACT_ROOT / 'coverage' / 'coverage.json'}",
            "--cov-fail-under=90",
        ]
    )

    summary = assert_tool_artifacts(sources)
    assert_jwt_rotation_helper()
    assert_compose_isolation()
    run(
        [
            "docker",
            "build",
            "--file",
            str(WORKER_ROOT / "Dockerfile"),
            "--tag",
            IMAGE_TAG,
            str(REPOSITORY_ROOT),
        ]
    )
    summary["image_user"] = assert_image_sources(sources)
    summary["container_smoke"] = smoke_test_container()
    summary["uv_version"] = uv_version
    summary["python_version"] = ".".join(str(part) for part in sys.version_info[:3])

    summary_path = ARTIFACT_ROOT / "verification-summary.json"
    summary_path.write_text(json.dumps(summary, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    assert_summary(summary_path)
    print(
        f"Verified {summary['tests']} tests across {len(sources)} production files; "
        f"lines={summary['line_coverage']:.2%}; branches={summary['branch_coverage']:.2%}",
        flush=True,
    )


if __name__ == "__main__":
    try:
        main()
    except (OSError, RuntimeError, subprocess.CalledProcessError, ET.ParseError) as exc:
        print(f"Worker verification failed: {exc}", file=sys.stderr)
        raise SystemExit(1) from exc
