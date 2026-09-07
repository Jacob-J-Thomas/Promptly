# Promptly worker QA (main `849a00b113ae694f3e8997b212e7e8786378ad0c`)

Scope: read-only QA of `Promptly.Worker`, using fixture payloads and a fake SDK client. No production files or GitHub issues were changed. The temporary Python environment is under `/private/tmp/promptly-audit-20260907/worker`.

## Executed reproductions

1. `Promptly.Worker/main.py` is zero bytes. Running `uvicorn main:app --host 127.0.0.1 --port 18000` exits with `ERROR: Error loading ASGI app. Attribute "app" not found in module "main"`. This blocks every worker route, including documented health routes.
2. An in-memory FastAPI app including the current routers returned HTTP 200 and `{score: 0.0, reason: "Evaluation failed: PROMPTLY_LLM_API_KEY environment variable is not set"}` for both evaluator endpoints when no key is configured. The C# client treats any 2xx as `Success=true`, so an unavailable evaluator becomes an ordinary failed expectation.
3. With a fake provider, `/mapping/propose` accepted `{}`, `{"foo":"bar"}`, and `{"version":"oops"}` as successful mapping specs. The declared `MappingSpecSchema` is not used to validate provider output. A list output instead caused a 500 response-model validation error. This violates the DesignSpec strict MappingSpec and structured error contract.
4. `/eval/groundedness` calls `get_llm_client()` before its no-document short circuit. With no key and `docs=[]`, it returns the same HTTP 200 evaluator failure instead of the documented no-doc policy.
5. With fake client constructors, `get_llm_client("AzureOpenAI")` and `get_llm_client("bogus")` both constructed the OpenAI client. Provider overrides are not normalized or rejected.
6. The synchronous `client.chat.completions.create(...)` call is inside `async def` handlers. Two concurrent fake requests whose provider call sleeps 250 ms took 0.512 s (serial execution), proving event-loop blocking.
7. The mapping fake captured a prompt containing response JSON and hints but no supplied `sample_request_json`, so that accepted field is ignored.

## Static findings / unavailable evidence

- Current `Promptly.Worker/Dockerfile` runs `uvicorn ... --reload` as the default root user and has no healthcheck. `docker/docker-compose.yml` exposes port 8000 and uses `depends_on: promptly-eval: condition: service_started`; there is no worker healthcheck/readiness dependency. Docker runtime testing was blocked by permission denied on `~/.colima/default/docker.sock`.
- Worker clients are never closed after synchronous provider calls; there is no timeout/retry policy in the worker and no request-size cap before prompt construction.
- There are no worker tests, `pyproject.toml`, or portable worker test command on main. The Python `requirements.txt` also omits the test/runtime validation tooling.
- Rubrics, assistant text, tool arguments, and retrieved documents are interpolated directly into LLM instruction prompts; this is an injection/integrity risk once the missing entrypoint is restored.

## Backlog reconciliation

The main failures are already represented by open issues: #25 (runnable worker/runtime/health), #41 (evaluator error semantics/no-doc policy), #37 (server-worker contracts), #1/#13/#14 (mapping/Azure/provider behavior), #46 (blocking calls/timeouts/readiness), #20 (integration coverage), #33 (prompt isolation), #32 (resource caps), and #68/#34 (container hardening/exposure). The ignored `sample_request_json` context and missing worker request-size/concurrency budget are candidate subfindings to attach to #1/#25/#46 rather than duplicate issues; no standalone issue was created.

## Existing candidate implementation

`origin/codex/25-worker-runtime` contains `ff228062c94c21f6b903b6bf881b0da2710eae3d` (plus `972ab56` docs), four commits ahead of main. It restores `main.py`, adds `llm.py`, strict mapping/evaluation models and tests, and adds Docker health/readiness wiring. Treat it as an implementation candidate requiring fresh exact-head review and hosted evidence; do not assume it is accepted merely because it exists on a remote branch.

## Pipeline gate implications

Do not admit worker-dependent product PRs as green until a clean exact-head worker import/startup smoke test, deterministic contract tests for success and failure, strict provider/score validation, and composed-stack readiness evidence pass. Keep provider-backed tests on a deterministic stub; live paid-provider behavior remains untested in this pass.
