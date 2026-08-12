# AI CODING AGENT BUILD SCRIPT — Promptly v1 (One-shot Build Instructions)

You are an AI coding agent. Your job is to generate a working, runnable, end-to-end application for **Promptly v1** as specified below.

## Absolute priorities
1) Ship a usable product (onboarding → add endpoint → mapping wizard → create tests → run suite → view results/history).
2) Keep scope tight and opinionated; avoid building a general MLOps suite.
3) Determinism in runtime:
   - LLMs may be used ONLY during onboarding wizard (to propose mappings) and for explicit judge/groundedness expectations.
   - No “LLM guesses the JSON every run”.
4) **Split architecture**:
   - **C# control plane** for product, storage, UI APIs, deterministic checks, orchestration.
   - **Python evaluation worker** for LLM judges + RAG metrics execution (and any future metric expansion).
   - The system must run end-to-end locally via Docker Compose.

---

# 0) Tech stack (use exactly this unless blocked)

## Control plane (Backend API)
- Language: C#
- Runtime: .NET 8 (LTS) using ASP.NET Core Web API
- ORM: EF Core
- Auth: ASP.NET Core Identity (email/password) + JWT for SPA
- DB: PostgreSQL
- Background runs: in-process worker + DB-backed queue (no Hangfire unless necessary)
- JSON parsing: System.Text.Json
- JSONPath: JsonPath.Net (or a well-maintained alternative)
- YAML support: YamlDotNet
- OpenAPI: Swashbuckle
- Encryption: ASP.NET Core Data Protection (persisted key ring)

## Evaluation worker (Metrics/Judges)
- Language: Python 3.11+
- Framework: FastAPI (HTTP service) OR a worker-only process (pick one):
  - Preferred for one-shot: **FastAPI** service `promptly-eval` with endpoints called by C#.
- LLM client: OpenAI / Azure OpenAI compatible HTTP client (use `openai` python package OR raw HTTP; choose stable).
- JSON/YAML: pydantic + PyYAML
- URL parsing: standard library + `tldextract` optional

## Frontend
- React + TypeScript + Vite
- UI library: Material UI (MUI) (or Mantine; pick one and be consistent)
- HTTP client: fetch or axios
- Routing: react-router
- Monaco editor for YAML/JSON editing (optional; if too heavy, use a simple textarea + validation)

## Dev environment & deployment
- Docker Compose: control plane + eval worker + postgres + (optional) pgadmin
- Migrations: EF Core
- Seed: optional demo project on first run

---

# 1) Repo structure

Create a single monorepo:

/src
  /server
    /Promptly.Server (ASP.NET Core Web API)
    /Promptly.Domain (domain models)
    /Promptly.Application (services, orchestration, deterministic evaluation)
    /Promptly.Infrastructure (EF Core, encryption, external clients)
    /Promptly.Sdk.DotNet (minimal client + optional CLI)
  /worker
    /promptly_eval (FastAPI app + evaluation logic)
  /web
    (React app)
/docker
  docker-compose.yml
README.md

---

# 2) Product definition (what to build)

**Promptly** is a black-box test harness and evaluation platform for LLM-powered systems.

It tests user-defined scenarios against a user-provided HTTP endpoint, parses the endpoint response into a canonical trace using a MappingSpec, evaluates expectations (deterministic + judge/groundedness via Python worker), stores run history, and displays results in a simple UI.

Key differentiators:
- Works against ANY endpoint if we can map request/response into a canonical trace.
- LLM-assisted mapping wizard proposes mapping once; runtime uses saved mapping spec deterministically.
- Agent/tool-call aware assertions (tool called / sequence) based on parsed tool calls in response.
- RAG groundedness supported if response includes retrieved docs (or doc IDs + content); we do not integrate vector DBs.

Non-goals for v1:
- No production prompt/config control plane (read-only config snapshots only).
- No “adaptive tool approvals” or runtime approvals.
- No deep MCP semantics; only check that tool calls exist and optionally argument-shape patterns (string/regex).
- No “metric plugin marketplace”.

---

# 3) System architecture (split responsibilities)

## 3.1 C# Control plane responsibilities
- Auth, tenancy, API keys
- CRUD: projects, environments, endpoints, mapping specs, suites, tests
- Run orchestration: queue, scheduling, run state machine
- HTTP execution against customer endpoint (send test messages, capture response JSON)
- Deterministic parsing via MappingSpec into CanonicalTrace
- Deterministic expectations evaluation (text/regex/link/tool-called/sequence)
- Persist runs/results/traces to DB
- UI APIs for runs/history/results
- Call Python evaluation worker for judge/groundedness expectations

## 3.2 Python Evaluation Worker responsibilities
- LLM judge scoring (rubric-based)
- Groundedness scoring against retrieved docs
- Strict JSON output contract: { score: float 0..1, reason: string }
- Retry and error handling; do not crash; return structured errors

## 3.3 Communication between C# and Python
- C# calls Python worker via HTTP over internal docker network.
- Python worker base URL configured as env var (e.g., `PROMPTLY_EVAL_BASE_URL=http://promptly-eval:8000`)
- C# sends requests containing:
  - expectation type payload
  - canonical trace
  - docs (if needed)
  - config (model name, provider base url, key reference name)
- Python returns:
  - score + reason (or error object)

NOTE: For simplicity, the Python worker reads its LLM provider API keys from environment variables, not from DB.
Later you can add per-project encrypted provider config. For v1, allow per-project provider config as optional, but do not block v1 on it.

---

# 4) Core domain model (C# + Postgres via EF Core)

Implement the following entities.

## Identity
- User (ASP.NET Identity)

## Core
- Project
  - Id, Name, Description, OwnerUserId, CreatedAt
- Environment
  - Id, ProjectId, Name, BaseUrl, DefaultHeadersEncryptedJson, CreatedAt
- Endpoint
  - Id, EnvironmentId, Name, Path, HttpMethod (POST only in v1), TimeoutSeconds
- MappingSpec
  - Id, EndpointId, Name, SpecJson, CreatedAt, UpdatedAt, IsDefault

- TestSuite
  - Id, ProjectId, Name, Description, CreatedAt
- TestCase
  - Id, SuiteId, ExternalId (string unique within suite), Name, Description, InputSpecJson, ExpectationsJson, CreatedAt, UpdatedAt

- TestRun
  - Id (GUID), SuiteId, EnvironmentId, EndpointId, MappingSpecId
  - Status: Queued/Running/Completed/Failed
  - CreatedAt, StartedAt, CompletedAt
  - CreatedByUserId
  - GitCommitHash (nullable)
  - ConfigSnapshotJson (nullable)  // user-provided metadata like model name, temp, etc.
  - SummaryJson (pass counts, avg scores, latency, tokens, cost)
  - ErrorMessage (nullable)

- TestRunResult
  - Id, RunId, TestCaseId, Status: Pass/Fail/Error
  - MetricsJson (map string->number)
  - FailureReasonsJson (array of strings)
  - TraceJson (CanonicalTrace json)  // store canonical trace + raw response excerpt for debugging
  - CreatedAt

## Optional settings (v1-lite)
- ProjectSettings
  - ProjectId
  - JudgeModelDefault (string nullable)
  - JudgeProvider (string nullable: "openai" | "azureopenai")
  - (Optional) JudgeProviderConfigEncryptedJson (only if you implement per-project keys)
  - ModelPricingJson (optional per model pricing to compute cost if tokens exist)

---

# 5) CanonicalTrace and MappingSpec format (VERY IMPORTANT)

Runtime must be deterministic.

## CanonicalTrace (internal; stored in TraceJson)
- messages: array of { role, content }
- toolCalls: array of { name, argumentsJson }
- usage: { promptTokens?, completionTokens?, totalTokens?, cost?, latencyMs? }
- retrievedDocs: array of { id?, title?, content, metadata? }
- rawResponse: store capped raw JSON string (e.g., max 200KB)

## MappingSpec (saved and applied deterministically)
Use a JSON schema that supports extracting and normalizing from arbitrary response JSON.

### MappingSpec JSON format (implement exactly):
{
  "version": 1,
  "messages": {
    "itemsPath": "<jsonpath to array of message objects>",
    "rolePath": "<jsonpath relative to each message item; default $.role>",
    "contentPath": "<jsonpath relative to each message item; default $.content>"
  },
  "toolCalls": {
    "itemsPath": "<jsonpath to array of tool call objects>",
    "namePath": "<jsonpath relative; default $.name>",
    "argumentsPath": "<jsonpath relative; default $.arguments>"
  },
  "usage": {
    "objectPath": "<jsonpath to usage object>",
    "promptTokensPath": "<jsonpath relative; e.g. $.prompt_tokens>",
    "completionTokensPath": "<jsonpath relative>",
    "totalTokensPath": "<jsonpath relative>",
    "costPath": "<jsonpath relative>",
    "latencyMsPath": "<jsonpath relative>"
  },
  "retrievedDocs": {
    "itemsPath": "<jsonpath to array of doc objects>",
    "idPath": "<jsonpath relative>",
    "titlePath": "<jsonpath relative>",
    "contentPath": "<jsonpath relative>",
    "metadataPath": "<jsonpath relative to metadata object>"
  },
  "fallback": {
    "singleAssistantContentPath": "<jsonpath to a single assistant content string if no messages array exists>"
  }
}

Rules:
- Any section may be omitted or null.
- If messages.content is not a string (e.g., array), stringify or join.
- If toolCalls.arguments is an object, serialize to JSON string.
- If mapping fails, return a structured error explaining which path failed.
- Fallback behavior:
  - If messages mapping is missing or fails but fallback.singleAssistantContentPath is present, create a canonical messages array with one assistant message using that content.

---

# 6) LLM-assisted mapping wizard (setup-time only)

Goal: Given a sample response JSON (and optional sample request JSON), propose a MappingSpec that will parse messages/tool calls/usage/docs.

## Wizard Flow (UI)
1) User inputs:
   - base URL, endpoint path
   - headers (auth, etc.) stored encrypted
   - sample request JSON (optional)
   - sample response JSON (required)
2) System calls backend endpoint: POST /api/mapping/propose with sample response JSON
3) C# backend calls Python worker endpoint `/mapping/propose` OR calls LLM directly in C# (choose one):
   - Preferred split: C# forwards to Python worker because Python prompt parsing/validation is easier.
4) Backend returns:
   - proposed MappingSpec JSON
   - parsed preview (CanonicalTrace from applying the spec to the sample response)
5) UI shows:
   - editable spec JSON
   - preview output
   - “Validate again” button
6) User saves MappingSpec as default for endpoint.

## IMPORTANT: runtime mapping does not call LLM.
LLM is used only in propose step.

---

# 7) Test DSL (YAML/JSON)

Store tests in DB but support import/export.

## TestCase format
id: string (ExternalId)
name: string
description: string
input:
  messages: [{role, content}]
expectations: array of expectation objects

## Expectation types (v1)

### Deterministic (C# control plane)
- contains_text
- banned_text
- regex_match
- link_pattern
- tool_called
- tool_sequence

### Non-deterministic / judge-based (Python worker)
- llm_judge
- groundedness

Judge response contract:
- Must parse JSON: { "score": number, "reason": string }
- If judge fails: mark expectation as error; do not crash entire run.

---

# 8) Test execution engine (orchestration)

## Triggering a run
- UI: user selects suite + environment + endpoint + mapping spec and clicks Run.
- API: POST /api/runs triggers and returns runId.
- CI: same endpoint authenticated via Project API key.

## Run processing (C# background worker)
- Runs are queued in DB (Status=Queued).
- A BackgroundService polls queued runs (concurrency limit configurable, default 2).
- Atomically claim runs using row-level lock or update-where-status pattern.
- For each test case:
  1) Build request payload to target endpoint:
     - default: { "messages": [ ... ] }
     - optional: allow Environment.RequestTemplateJson later; skip unless easy.
  2) Send HTTP request with headers; measure latency.
  3) Parse JSON response.
  4) Apply MappingSpec -> CanonicalTrace.
  5) Evaluate deterministic expectations in C#.
  6) For each judge-based expectation:
     - call Python worker with canonical trace + expectation payload + optional docs.
     - record returned score/reason.
  7) Persist TestRunResult with TraceJson + failure reasons.
- Compute run summary and set run status Completed/Failed.

---

# 9) Python evaluation worker API (FastAPI)

Implement these endpoints:

## POST /eval/llm-judge
Request:
{
  "rubric": "string",
  "min_score": 0.8,
  "trace": { CanonicalTrace },
  "model": "optional-string",
  "provider": "openai|azureopenai|optional",
  "metadata": { optional }
}
Response:
{ "score": 0.87, "reason": "string" }
On error:
{ "error": { "message": "string", "details": "optional" } }

## POST /eval/groundedness
Request:
{
  "min_score": 0.8,
  "trace": { CanonicalTrace },
  "docs": [{ id?, title?, content, metadata? }],
  "model": "optional-string",
  "provider": "optional"
}
Response:
{ "score": 0.75, "reason": "string" }

## POST /mapping/propose (optional but preferred)
Request:
{
  "sample_response_json": "string (raw json)",
  "sample_request_json": "optional string",
  "hints": { optional }  // e.g., “tool calls appear under trace.tools”
}
Response:
{ "mappingSpec": { MappingSpec }, "reason": "short explanation optional" }

Rules:
- Force strict output schema for mappingSpec.
- If mapping spec cannot be confidently produced, return an error message.

LLM provider config for worker:
- Environment variables:
  - PROMPTLY_LLM_PROVIDER
  - PROMPTLY_LLM_API_KEY
  - PROMPTLY_LLM_BASE_URL
  - PROMPTLY_LLM_MODEL_DEFAULT

---

# 10) API surface (C# backend)

Implement REST endpoints with JWT auth for UI and API keys for CI.

## Auth
- POST /api/auth/register
- POST /api/auth/login -> returns JWT

## Projects
- GET /api/projects
- POST /api/projects
- GET /api/projects/{projectId}

## Environments
- POST /api/projects/{projectId}/environments
- GET /api/projects/{projectId}/environments
- PUT /api/environments/{envId}

## Endpoints
- POST /api/environments/{envId}/endpoints
- GET /api/environments/{envId}/endpoints
- PUT /api/endpoints/{endpointId}

## Mapping
- POST /api/endpoints/{endpointId}/mapping/propose   (calls Python worker mapping/propose OR LLM)
- POST /api/endpoints/{endpointId}/mapping/validate  (apply spec to sample response and return preview)
- POST /api/endpoints/{endpointId}/mapping           (save)
- GET /api/endpoints/{endpointId}/mapping
- PUT /api/mapping/{mappingId} (edit)
- POST /api/mapping/{mappingId}/set-default

## Suites and tests
- POST /api/projects/{projectId}/suites
- GET /api/projects/{projectId}/suites
- GET /api/suites/{suiteId}
- POST /api/suites/{suiteId}/tests/import (YAML/JSON upload)
- GET /api/suites/{suiteId}/tests/export (download)
- POST /api/suites/{suiteId}/tests
- PUT /api/tests/{testCaseId}
- DELETE /api/tests/{testCaseId}

## Runs
- POST /api/runs  (body: suiteId, envId, endpointId, mappingSpecId, gitCommitHash?, configSnapshotJson?)
- GET /api/runs/{runId}
- GET /api/suites/{suiteId}/runs
- GET /api/runs/{runId}/results
- GET /api/runs/{runId}/results/{resultId}

## CI Auth (API Keys)
- POST /api/projects/{projectId}/api-keys  (create)
- GET /api/projects/{projectId}/api-keys
- DELETE /api/api-keys/{keyId}

---

# 11) Frontend UX (must implement)

Pages:
1) Login / Register
2) Projects list
3) Project detail:
   - Environments section
   - Suites section
4) Environment detail:
   - Base URL + headers form (headers stored securely)
   - Endpoints list + “Add endpoint”
5) Add endpoint wizard:
   - Step 1: endpoint basics
   - Step 2: paste sample response JSON (and optional request)
   - Step 3: “Propose mapping” (calls backend)
   - Step 4: show editable MappingSpec + preview trace
   - Step 5: save mapping and set default
6) Suites:
   - Create suite
   - List tests
   - Create/edit test:
     - Basic form UI for input messages + expectation list
     - YAML editor toggle (optional)
   - Import/export tests
7) Runs:
   - Run suite form (select env/endpoint/mapping)
   - Runs list (filter: status, date)
   - Run detail:
     - summary (pass rate, avg score, latency, tokens/cost if present)
     - results table: failed first
     - click a test -> show:
       - input messages
       - assistant output
       - tool calls list
       - retrieved docs (collapsible)
       - expectation breakdown with pass/fail + metrics + judge reason

UX requirements:
- Always surface “why” for failures.
- Make mapping preview obvious and debuggable.
- Avoid complex graphs; tables and deltas are enough for v1.

---

# 12) SDKs / CI helpers (minimal but real)

## .NET SDK
- Provide PromptlyClient with methods:
  - TriggerRunAsync(...)
  - GetRunAsync(...)
  - WaitForCompletionAsync(timeout)
- Provide a small dotnet CLI tool in Promptly.Sdk.DotNet:
  - promptly trigger --base-url ... --api-key ... --suite ... --env ... --endpoint ... --mapping ... --commit ...
  - promptly wait --run-id ... --timeout ...

## Python helper
- Not required since Python worker is internal.
- Provide one sample Python script for CI to call Promptly API (optional).

---

# 13) Security requirements (do not skip)

- Encrypt environment headers at rest:
  - Use ASP.NET Core Data Protection (IDataProtector) with a persisted key ring (mounted volume in docker).
  - Store headers as encrypted JSON string.
- Never log sensitive header values.
- API keys stored hashed. UI shows only last 4 chars.

Data retention:
- Store conversation content as part of results.
- Provide a per-project setting to cap retained runs (optional).
- Document that no customer content is used to train models.

---

# 14) Configuration

## C# control plane env vars
- ConnectionStrings__Default
- JWT__Issuer, JWT__Audience, JWT__Key
- DATA_PROTECTION_PATH
- PROMPTLY_EVAL_BASE_URL (e.g., http://promptly-eval:8000)

## Python worker env vars
- PROMPTLY_LLM_PROVIDER (openai|azureopenai)
- PROMPTLY_LLM_API_KEY
- PROMPTLY_LLM_BASE_URL
- PROMPTLY_LLM_MODEL_DEFAULT

---

# 15) Deliverables (Definition of Done)

Repository includes:
- Working Docker Compose stack to run:
  - promptly-server (C#)
  - promptly-eval (Python)
  - postgres
  - promptly-web
- EF Core migrations + README with run steps.
- Backend API + Swagger.
- Frontend UI implementing required pages.
- Background worker that processes runs.
- Mapping wizard with LLM propose + preview + save.
- Test import/export in YAML.
- Execution engine implementing all expectation types listed, delegating judge/groundedness to Python worker.
- Minimal .NET CLI tool to trigger + wait.
- A demo toy endpoint in the repo to validate end-to-end behavior.

Acceptance demo scenario:
1) Start the stack with Docker Compose.
2) Use a sample “toy chat endpoint” in repo that returns:
   - messages array, tool calls, usage, retrieved docs (fake).
3) Onboard endpoint via UI; mapping wizard proposes spec; user saves it.
4) Create a suite with 3 tests:
   - deterministic contains_text and link_pattern
   - tool_called
   - llm_judge (if LLM configured)
5) Run suite and inspect results (failures and traces).

---

# 16) Implementation notes to avoid scope creep

Do NOT add:
- model registry, dataset labeling UI
- multi-org RBAC beyond basic owner
- inline production approvals
- plugin system
- vector DB connector zoo

Keep graphs minimal; focus on debuggability and speed-to-value.

Mapping must be explicit, saved, deterministic. No runtime “LLM parsing”.

---

# 17) Execution order (build plan)

1) Scaffold monorepo, docker compose, postgres.
2) Implement C# auth + JWT + CRUD (projects/environments/endpoints).
3) Implement encryption for headers.
4) Implement MappingSpec application engine + validate endpoint.
5) Implement Python worker (FastAPI) with llm_judge + groundedness + mapping/propose.
6) Implement C# mapping propose endpoint (call Python worker) + UI wizard.
7) Implement suites/tests CRUD + YAML import/export.
8) Implement run queue + background worker + HTTP executor.
9) Implement deterministic expectations evaluation.
10) Implement C# → Python delegation for llm_judge/groundedness expectations.
11) Build frontend pages progressively:
    auth → projects → env/endpoints → mapping wizard → suites/tests → runs/results
12) Add demo endpoint + README demo flow.
13) Add .NET CLI tool.

---

# 18) Deliver the final output

Output the complete codebase in this repo structure, with:
- no placeholders for required features
- README to run locally
- sensible default configuration for local dev
- minimal but correct comments only where necessary

END.
