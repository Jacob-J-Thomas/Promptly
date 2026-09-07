# Promptly Worker

The Promptly Worker is the FastAPI service used for LLM-assisted evaluation and mapping.
Python 3.11.15, `uv` 0.12.3, Docker with Compose v2, and the .NET SDK pinned in
`../global.json` are the supported verification toolchain; `uv.lock` is the authoritative
Python dependency graph.

## Local development

From `Promptly.Worker`:

```bash
uv sync --frozen --all-groups
uv run uvicorn main:app --reload --port 8000
```

The liveness endpoint is `GET /health/live`. The dependency-aware readiness endpoint is
`GET /health/ready`; `GET /health` remains the compatibility health endpoint.
`PROMPTLY_LLM_TIMEOUT_SECONDS` bounds provider calls and defaults to 90 seconds; readiness
always uses the smaller five-second probe timeout.

## Required verification

From `Promptly.Worker`, run the Python/image verifier enforced by
`.github/workflows/worker-verification.yml`:

```bash
uv run --frozen --all-groups python scripts/verify.py
```

Then, from the repository root, run the mandatory live cross-language contracts:

```bash
dotnet restore tests/Promptly.Application.UnitTests/Promptly.Application.UnitTests.csproj --locked-mode
PROMPTLY_RUN_WORKER_CONTRACT_TESTS=1 \
PROMPTLY_UV_EXECUTABLE="$(command -v uv)" \
dotnet test tests/Promptly.Application.UnitTests/Promptly.Application.UnitTests.csproj \
  --configuration Release --no-restore -p:CollectCoverage=false \
  --filter FullyQualifiedName~PythonWorkerLiveContractTests
```

The live contract cohort must execute two tests with no skips. It starts only dynamic
loopback listeners and exercises the real .NET client, FastAPI app, OpenAI/Azure SDK wire
shapes, and deterministic local provider stub; it never contacts an external provider.

The verifier recreates `artifacts/test-results/python`, then checks the lock, formatting,
lint, strict typing for both production sources and tests, source security, runtime and full
verification dependencies, tests, coverage, Compose model, production image, and container
health. It fails on zero tests and independently requires at least 90% line and 90% branch
coverage across every Python module shipped in the image. The exact production-source manifest
drives its own mypy and Bandit gates and is reconciled against coverage, Bandit output, and the
built image; tests pass a separate strict mypy gate. The full locked dependency audit must
contain the runtime audit cohort plus the test-only `httpx2` required dynamically by Starlette.
The smoke containers publish no host ports and use an authenticated provider stub on an
internal-only Docker network, so both OpenAI and AzureOpenAI must pass liveness and
dependency-aware readiness without contacting an external service.

CI retains JUnit/TRX, Cobertura, coverage JSON, Ruff, the production and test mypy reports,
Bandit, the locked runtime and full verification dependency exports, both `pip-audit` reports,
the production-source manifest, and the verification summary for 14 days.

Runtime dependencies must be changed in `pyproject.toml` and locked with the repository's
exact `uv` version:

```bash
uv lock
```

Do not maintain a parallel `requirements.txt`; Docker and CI install directly from the lock.
