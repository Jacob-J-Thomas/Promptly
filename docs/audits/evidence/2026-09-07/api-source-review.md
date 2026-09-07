# Promptly API QA report

Audit target: `main` at `849a00b113ae694f3e8997b212e7e8786378ad0c` in `/Users/jake/Repos/Promptly`.

Scope was read-only review of the ASP.NET API, application/domain/infrastructure services, Python worker contract, SDK/CLI, and targeted static/runtime probes. No repository or GitHub writes were made. Existing backlog issue numbers below are from `/private/tmp/promptly-audit-20260907/backlog/issues-open.json`; all findings are already represented there and should be deduplicated rather than opened again.

## Confirmed from source

| Priority | Existing issue | Finding and evidence |
|---|---|---|
| P0 | #25 | `Promptly.Worker/main.py` is 0 bytes (`wc -c` = 0), but `Promptly.Worker/Dockerfile:18` starts `uvicorn main:app`. The worker image cannot import `app`; `/mapping` and `/eval` routers are therefore unreachable. `python3 -m compileall` passes only because an empty module is valid syntax. |
| P0 | #26 | Nested resource routes are `[Authorize]` but carry no owner/project scope. `EnvironmentService.cs:27-39,70-135`, `EndpointService.cs:20-31,59-97`, `MappingService.cs:441-514`, `TestSuiteService.cs:39-82`, and `TestCaseService.cs:49-102` query by child IDs only. `EnvironmentsController.GetEnvironment` then decrypts and returns headers (`EnvironmentsController.cs:52-75`). Any authenticated user who obtains/guesses a GUID can read or mutate another user's resources. |
| P0 | #27 | `TestRunService.QueueRunAsync` persists the four caller-supplied IDs without checking they form one owned project graph (`TestRunService.cs:21-45`). A suite, environment, endpoint, and mapping from different projects can be combined and then executed. |
| P0 | #28 | `EndpointExecutor.cs:63-64,99-104` constructs an outbound request from user-controlled `Environment.BaseUrl` and `Endpoint.Path` without a destination policy; absolute endpoint paths can override `BaseAddress`. This is an SSRF/private-network egress surface. |
| P1 | #37 / #40 | API-key support is incomplete: `Program.cs:72-92` selects JWT as the default scheme and only registers an unused `ApiKey` scheme; no controller issues/lists/revokes keys. `PromptlyClient.cs:30` and the CLI send `Authorization: Bearer <api-key>`, while `ApiKeyAuthenticationHandler.cs:15,30` expects `X-API-Key`. |
| P1 | #37 | Web/API endpoint field names disagree. `MappingWizard.tsx:129-134` sends `method`, while the server request model requires `HttpMethod`; selected non-POST methods are silently dropped. `endpoints.ts:3-19` expects/uses `method`, while the server emits `httpMethod` (`EndpointsController.cs:31-38`). |
| P2 | #37 | SDK/CLI base URL contract is inconsistent. `PromptlyClient` appends `/runs` and `/suites/...` to `_baseUrl` (`PromptlyClient.cs:14-17,60-68`), but README commands use `--base-url http://localhost:5000` (`README.md:287-298`) while API routes are under `/api`; documented calls hit `/runs` and 404. |
| P1 | #38 | Queue claiming is select-then-update with no row lock, concurrency token, or conditional update (`TestRunService.cs:82-117`). Multiple server replicas can claim and execute the same queued run. Fire-and-forget scheduling in `TestRunWorkerService.cs:42-64` also has no lease/heartbeat/recovery. |
| P1 | #41 | `TestRunProcessor` treats a test with zero expectations as `Pass` (`TestRunProcessor.cs:224-237`). Python evaluator failures return HTTP 200 with score 0 (`Promptly.Worker/routers/evaluation.py:148-153,228-233`), turning infrastructure errors into assertion failures. Groundedness with no documents returns score 1.0 (`evaluation.py:175-179`). `ToolSequenceExpectation.ExactSequence` is declared but ignored: evaluator checks only a prefix (`ExpectationEvaluator.cs:253-291`). |
| P1 | #42 | Executor measures actual request latency but never adds it to the canonical trace. `TestRunProcessor.cs:98-115` then looks for lowercase `usage`, `totalTokens`, `cost`, and `latencyMs` in a default PascalCase `JsonSerializer.Serialize(trace)` result (`:221-222`), so normal runs report zero latency/tokens/cost. |
| P1 | #44 | Test input/expectation JSON is persisted without semantic validation (`TestModels.cs:27-43`; `TestsController.cs:30-68`). YAML ignores unknown properties (`YamlService.cs:28-31`), duplicate IDs reach the database unique constraint, and these errors become generic 500 responses. |
| P1 | #1 / #37 | Mapping runtime silently accepts unusable specs. `MappingService.ApplyMapping` always returns `Success=true` after extraction (`MappingService.cs:35-63`); missing JSONPath matches become empty lists and `ExtractMessages` returns before trying fallback (`:81-105`). It also deserializes without web/case-insensitive naming options (`:39`), while the documented mapping JSON uses camelCase (`README.md:246-274`). |
| P1 | #31 | User regex patterns are compiled with no timeout or length budget (`ExpectationEvaluator.cs:120-157,183-227`), allowing catastrophic backtracking to consume a shared run-worker slot. |
| P2 | #43 | Executor maps unsupported methods to POST (`EndpointExecutor.cs:89-97`), attaches JSON content to every method, and relies on ambiguous URI resolution. API models do not restrict `HttpMethod` to the documented POST-only v1 contract. |
| P2 | #37 | Registration accepts a display name but does not persist it: register returns `request.Name`, login returns `user.UserName` (email) (`AuthController.cs:48-70,104-115`; `User.cs:5-11`). |

## Static/runtime probes

- `git status --short --branch` showed the target clean relative to main at the start; parent agent work has since added unrelated untracked audit artifacts in the shared checkout. No files were changed by this lane.
- `git rev-parse HEAD` returned `849a00b113ae694f3e8997b212e7e8786378ad0c`.
- `git diff --check` passed.
- `PYTHONPYCACHEPREFIX=/private/tmp/promptly-audit-20260907/api/pycache python3 -m compileall -q Promptly.Worker` passed (`compileall_exit=0`). This validates syntax only; it does not make the empty worker entry point runnable.
- Initial compile without an isolated cache failed because the host Python 3.9 attempted to write Apple cache paths with `PermissionError: [Errno 1] Operation not permitted`.
- `dotnet build Promptly.Server/Promptly.Server.csproj --configuration Release --no-restore --no-incremental --warnaserror` failed before compilation: `NETSDK1004` because `Promptly.Server/obj/project.assets.json` is absent. Restore/build was not retried because the host had approximately 422 MiB free and shared agents were already handling dependency work.
- `cd Promptly.Web/src/web && npm run lint` failed with `sh: eslint: command not found` in this lane; no duplicate frontend dependency install was attempted.
- `gh issue list` could not reach `api.github.com`; deduplication used the live issue snapshot supplied by the parent at `/private/tmp/promptly-audit-20260907/backlog/issues-open.json`.

## Suggested work grouping

The existing backlog already has coherent seams: worker runtime #25; tenant/resource authorization #26; run graph #27; SSRF #28; API contracts and API-key lifecycle #37/#40; queue reliability #38; evaluation semantics #41; measured metrics #42; HTTP behavior #43; test/YAML validation #44; docs/runtime #45; regex budget #31. These should be implemented as individually reviewed PRs in dependency order, with #37 used as the cross-client contract source before SDK/CLI and UI fixes.

Audit integration note: table rows group related observations by existing issue ownership. Each causal mechanism needs its own validation/disposition; a single repaired mechanism does not close the others. Priority here is the current observation assessment; api-findings.json also preserves inherited tracker priority. No live issue priority or closure was changed.
