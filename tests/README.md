# Test suites

Promptly's automated test foundation is being added in dependency-ordered slices under issue #18.

The expanding C# gate exercises named Application services, every production source in `Promptly.Infrastructure`, and the authentication/controller plus tenant/security Server sources listed by the application-verification workflow. It fails zero-test runs and enforces at least 90% line and branch coverage for each measured assembly, named top-level class, and measured source:

```bash
dotnet test tests/Promptly.Application.UnitTests/Promptly.Application.UnitTests.csproj --configuration Release --settings tests/Promptly.runsettings --logger "trx;LogFileName=Promptly.Application.UnitTests.trx" --results-directory artifacts/test-results/csharp
```

The ignored `artifacts/test-results/csharp` directory receives the TRX result plus Cobertura and JSON coverage reports. GitHub Actions runs the same gate for every pull request and `main` push, verifies the exact measured assembly/source/class cohorts, and retains those artifacts for 14 days.

The application workflow also fails unexpected skips or changes to the named high-risk
authentication concurrency, capacity, cancellation, proxy, and private-response test cohort.
The two opt-in live Python worker contracts are the only acknowledged skips in this unit-test
job; the worker workflow executes those contracts separately against the real worker.

The web workflow enforces the aggregate 90% line/branch threshold and the same threshold for
the authentication error parser plus Login and Register pages individually.

## Browser end-to-end tests

The first checked-in Playwright slice proves the authentication/session boundary against a
standalone production-composed stack: anonymous redirect, real UI registration/logout/login,
and an expired signed JWT producing a real API 401, redirect, and storage cleanup. It does not
claim the still-blocked project-to-result, CRUD/navigation, demo, or mapping journeys in #24.

Run the exact gate from the web directory:

```bash
cd Promptly.Web/src/web
npm ci
npx playwright install chromium
npm run test:e2e
```

The suite has a schema-versioned exact inventory, zero retries, no quarantine path, same-origin
browser egress and error guards, per-run secrets and Compose state, bounded readiness, and
unconditional cleanup. JUnit, JSON, HTML, failure media, sanitized provider evidence, Compose
diagnostics, and topology/cleanup attestations are stored under
`artifacts/test-results/e2e`. Retained trace archives are recursively inspected and sanitized
before the workflow can create its upload-safe marker. See `Promptly.Web/src/web/e2e/README.md`
for the complete policy.

This expanding gate is not evidence of repository-wide 90% C# coverage; Application services and Server sources outside the named cohorts, plus Domain, SDK, and CLI, remain outside its denominator. Issues #18 and #19 remain open until every unit-testable production area is included in the required aggregate gates.

## Integration tests

The skip-free integration suite exercises the real ASP.NET Core request pipeline against a disposable PostgreSQL 18.4 database and the real FastAPI worker. Mapping proposals traverse Server -> FastAPI -> a checked-in deterministic OpenAI-compatible provider stub; no external credentials or paid services are used.

Covered foundation contracts include:

- empty-database migration and health startup;
- registration, login failure, anonymous denial, and top-level two-user project isolation;
- persisted authentication lockout/recovery; generic missing, wrong-password, and locked
  responses; bounded client/account/registration/password-spray throttles; concurrency and
  partition isolation; stable `429` retry contracts; trusted-proxy spoof resistance; and
  pre-Identity authentication field/body limits;
- owned suite YAML import persistence (semantic YAML export round-tripping remains tracked separately in issue #44);
- real mapping proposal across Server, FastAPI, and the provider stub;
- safe Server responses when the worker is unavailable or returns malformed JSON.
- fail-closed JWT configuration in Development, test, and Production, plus healthy startup
  with an injected high-entropy signing key.

The suite intentionally does not assert current cross-tenant behavior of nested resources. Those authorization contracts belong after issue #26 closes, so this foundation does not canonize a known vulnerability.

Docker must be available. Run the locked suite from the repository root:

```bash
dotnet restore Promptly.slnx --locked-mode
dotnet build Promptly.slnx --configuration Release --no-restore
dotnet test tests/Promptly.IntegrationTests/Promptly.IntegrationTests.csproj \
  --configuration Release \
  --no-build \
  --logger "trx;LogFileName=Promptly.IntegrationTests.trx" \
  --results-directory artifacts/test-results/integration
```

The harness builds `Promptly.Worker/Dockerfile` by default, pins PostgreSQL by tag and multi-platform digest, and stores the TRX plus Server, PostgreSQL, FastAPI, and provider diagnostics under `artifacts/test-results/integration`. The provider binds port `0` itself and reports the kernel-selected port to the harness, avoiding free-port reservation races. Its `provider-requests.jsonl` artifact records only structured request evidence (monotonic sequence, method, path, request kind, authorization result, model/message count, and a test correlation ID); it does not record credentials or prompt bodies. The mapping test proves that its API call generated a correlated `/v1/chat/completions` request after readiness probes. CI independently validates that evidence, prebuilds the worker image once, fails skipped or zero-test runs, and retains the artifact bundle for 14 days.

All external-resource and process cleanup steps have explicit time bounds, including synchronous delegate execution. Teardown attempts every step and aggregates secondary cleanup failures; if setup or a test operation already failed, that primary exception remains the first preserved exception.

Production defaults remain unchanged. `Startup:ApplyDatabaseMigrations` and `TestRunner:Enabled` default to `true`; specialized test hosts can explicitly control them.
