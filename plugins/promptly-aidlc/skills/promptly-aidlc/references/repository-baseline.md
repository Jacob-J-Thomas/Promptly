# Promptly repository baseline

Use this reference to choose probes and to avoid importing assumptions from another repository. Revalidate every fact against the checkout and live stack before using it as acceptance evidence.

## Identity and shape

- Repository: `https://github.com/Jacob-J-Thomas/Promptly`.
- Initial inspected baseline: `849a00b113ae694f3e8997b212e7e8786378ad0c` on `main`.
- The live review snapshot also showed 15 draft pull requests in a stack (`48 -> 50 -> 51 -> 52 -> 53 -> 54 -> 55 -> 56 -> 57 -> 59 -> 60 -> 65 -> 66 -> 70 -> 71`). Re-query this stack before planning or touching a shared seam; draft status and ancestry are mutable evidence.
- Product shape: a black-box LLM test harness. The documented happy path is register/login -> project -> environment -> endpoint -> mapping wizard -> suite/tests -> queued run -> results/history.
- Control plane: `Promptly.Server`, `Promptly.Application`, `Promptly.Domain`, and `Promptly.Infrastructure`, targeting .NET 10 and PostgreSQL through EF Core.
- Worker: `Promptly.Worker`, Python 3.11/FastAPI, intended to expose mapping proposals and LLM judge/groundedness calls.
- UI: `Promptly.Web/src/web`, React + TypeScript + Vite + MUI. Run UI checks from this directory, not the repository root.
- Supporting clients: `Promptly.Sdk.DotNet` and `Promptly.Cli`; compose definition: `docker/docker-compose.yml`.

## Static signals from the initial baseline

These are useful leads, not substitutes for a live repro:

- `Promptly.Worker/main.py` was empty while `Promptly.Worker/Dockerfile` starts `uvicorn main:app`. The routers are present but not visibly mounted. A real worker health/import probe should establish the observed status before an issue is filed.
- `Promptly.Web/src/web/src/pages/SuiteDetail.tsx` contains an unimplemented test-edit action and a run configuration dialog that says its environment/endpoint/mapping selection is a placeholder. The dialog's selection state is never populated in the initial source snapshot.
- The same suite page creates a test with empty messages and expectations. Check whether the API accepts that record and whether the resulting test is useful before treating it as a defect.
- `RunDetail.tsx` renders fields such as `passedExpectations`, `failedExpectations`, `failureReason`, and `errorMessage`, while `TestRunResultResponse` exposes `MetricsJson`, `FailureReasonsJson`, and trace fields. Exercise a real completed run to distinguish a type-contract defect from dead code.
- The initial inventory had no `AGENTS.md`, `.github/workflows` files, or test projects. Re-query these paths on every delivery attempt; do not invent required check names or claim coverage receipts that do not exist.
- A diagnostics snapshot reported 23 TypeScript build errors and 46 lint errors plus 6 additional diagnostics on the web branch under review. Treat those counts as stale leads until the exact candidate SHA and command output are captured.
- The initial full-stack attempt was blocked before application execution by local Docker I/O corruption and low host disk headroom (about 352 MiB reported). No container health or browser fixture result from that attempt is authoritative. A route fixture may help explore UI shape, but it must be labeled a fixture and cannot certify the real API, worker, or Compose flow.
- A source pipeline overlay exists outside the published `main` history. Do not import or treat it as Promptly's central development policy unless the user explicitly admits it and the exact source is made reviewable.

## First live probes

Use the all-stack compose topology as the end-to-end authority when Docker is available. Before starting it, inspect existing containers and reserve a disposable project, database, volumes, and loopback ports. The baseline Compose file has fixed container names: a project flag alone does not isolate it. Create a temporary override with unique names and ports before running the following equivalent probes against that fixture. Do not print resolved Compose secrets; use `config --quiet` or `config --services`.

Set these variables from the isolated fixture you actually created; do not run the baseline stack without its override. Run from the repository root:

```bash
# qa_project: the reserved unique project name
# qa_override: absolute path of the generated isolated override
# qa_api_url and qa_worker_url: the fixture's reserved loopback URLs
: "${qa_project:?set the reserved project name}"
: "${qa_override:?set the generated override path}"
: "${qa_api_url:?set the fixture API URL}"
: "${qa_worker_url:?set the fixture worker URL}"
test -f "$qa_override" || exit 1
qa_compose=(docker compose -p "$qa_project" -f docker/docker-compose.yml -f "$qa_override")
"${qa_compose[@]}" config --quiet
"${qa_compose[@]}" build
"${qa_compose[@]}" up -d
"${qa_compose[@]}" ps
curl -fsS "$qa_api_url/health"
curl -i "$qa_worker_url/health"
curl -i -X POST "$qa_api_url/demo/chat" \
  -H 'Content-Type: application/json' \
  -d '{"messages":[{"role":"user","content":"hello"}]}'
```

Capture sanitized service logs and the exact compose file/commit when a probe fails. Stop only the scoped fixture with the same `qa_compose` invocation after preserving evidence. Do not delete volumes or retained data as routine cleanup. A local source build or a mocked worker response cannot certify an all-stack claim.

For focused checks, restore dependencies first, then run `dotnet build Promptly.slnx`, `dotnet test` when test projects exist, `npm ci && npm run build` from `Promptly.Web/src/web`, and Python syntax/import checks with a writable bytecode cache. Report missing dependencies as preflight state and continue with checks that are actually runnable.

Start each work session with a feasibility probe and a time box: verify free disk, Docker daemon/volume I/O, dependency caches, network/provider credentials, and available browser/runtime tooling before promising end-to-end execution. If boot cannot become healthy inside the time cap, preserve the exact diagnostics, continue with read-only source and contract checks, and keep the plan explicit about the blocked gate.

## Product boundary

Runtime mapping must be deterministic after a saved mapping spec. LLM calls belong to mapping proposal and explicit judge/groundedness expectations. Keep authentication, tenant ownership, encrypted environment headers, endpoint execution, run state, canonical traces, and raw response evidence in scope when a change touches them. Do not add a general MLOps control plane, adaptive approvals, deep MCP semantics, vector database integration, or a metric marketplace without explicit scope admission.
