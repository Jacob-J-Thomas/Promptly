# Promptly v1

**Black-box test harness for LLM-powered systems**

Promptly is a comprehensive testing platform for LLM applications. It captures LLM interactions through HTTP endpoints, evaluates responses against expectations using both deterministic rules and LLM judges, and provides detailed test results with full trace visibility.

## Project Status

Promptly is an experimental prototype, not a polished product or actively maintained service. It is public because the project captures two useful lines of work:

1. designing a black-box evaluation harness for LLM-powered applications; and
2. testing an AI-assisted delivery model where tickets, requirements, acceptance criteria, review notes, and targeted QA became the primary interface between a human product owner and an LLM coding agent.

Read the repository as a product/engineering experiment. The implementation explores a real full-stack shape, but the most important learning was how much structure an LLM agent needs before it can reliably implement a non-trivial app from ticket-level direction.

## Development Approach

Most implementation work was driven through an LLM coding agent. My role was closer to product manager, architect, and reviewer than line-by-line implementer:

- break the application into tickets and incremental slices;
- write requirements and acceptance criteria for each slice;
- review generated code and UI behavior against the intended workflow;
- redirect the agent when architecture, data model, or UX decisions drifted;
- use browser checks, API checks, and database inspection to validate behavior;
- track where AI-assisted development accelerated delivery and where it created integration, coherence, or quality-control risk.

The experiment also used a multi-agent review loop rather than a single prompt-to-code pass:

- a Codex implementer agent worked GitHub issues into application changes;
- a senior agent reviewer inspected the resulting code and architecture;
- a Playwright MCP QA agent exercised code deployed to the real website and reported product/behavior defects;
- a project-manager agent translated QA feedback into organized GitHub issues and sent the next batch back to the implementer.

That loop was designed to run on a recurring schedule, pause for my approval before merging, and route either approval or corrective feedback back into the next implementation cycle.

That process is part of what Promptly is meant to demonstrate. The repository is useful both as an LLM evaluation prototype and as evidence for how ticket-driven AI-assisted software delivery behaves in practice.

## Features

- **Multi-Environment Testing**: Test across development, staging, and production environments
- **Flexible Response Mapping**: Define JSONPath-based mappings to extract canonical traces from any API response format
- **Deterministic & LLM-Based Evaluations**: Combine regex, text matching, tool call verification with AI-powered judgement
- **Test Suite Management**: Organize tests with YAML import/export, Git integration, and historical tracking
- **Comprehensive Reporting**: View pass rates, failure reasons, latency, token usage, and cost metrics
- **Background Test Execution**: Queue runs and process them asynchronously with configurable concurrency
- **SDK & CLI**: Integrate Promptly into CI/CD pipelines with the .NET SDK and CLI tool

## Architecture

```
┌─────────────────────┐     ┌──────────────────────┐     ┌────────────────────┐
│   React Frontend    │────▶│  ASP.NET Core API    │────▶│   PostgreSQL DB    │
│  (Material UI)      │     │  (.NET 10)           │     │                    │
└─────────────────────┘     └──────────────────────┘     └────────────────────┘
                                      │
                                      │ HTTP
                                      ▼
                            ┌──────────────────────┐
                            │  Python Worker       │
                            │  (FastAPI)           │
                            │  - LLM Judges        │
                            │  - Mapping Proposals │
                            └──────────────────────┘
```

**C# Control Plane**: Authentication, CRUD APIs, deterministic evaluation, test orchestration, background worker
**Python Worker**: LLM judges, groundedness scoring, mapping proposals via OpenAI API
**React Frontend**: Material UI, mapping wizard, test management, results visualization
**Database**: PostgreSQL with EF Core

## Quick Start

### Prerequisites

- Docker & Docker Compose
- Azure OpenAI resource (or OpenAI API key)
- .NET 10 SDK (for local development - optional)
- Node.js 18+ (for local frontend development - optional)

### Running with Docker Compose

1. **Clone the repository**
   ```bash
   git clone <repository-url>
   cd Promptly
   ```

2. **Configure Azure OpenAI (or OpenAI)**

   Create a `.env` file in the `docker` directory:

   **For Azure OpenAI (Recommended):**
   ```env
   # Azure OpenAI Configuration - REQUIRED
   PROMPTLY_LLM_PROVIDER=azureopenai
   PROMPTLY_LLM_API_KEY=your_azure_openai_api_key
   PROMPTLY_LLM_AZURE_ENDPOINT=https://your-resource-name.openai.azure.com
   PROMPTLY_LLM_API_VERSION=2024-08-01-preview
   PROMPTLY_LLM_MODEL_DEFAULT=your-deployment-name
   ```

   **For standard OpenAI:**
   ```env
   PROMPTLY_LLM_PROVIDER=openai
   PROMPTLY_LLM_API_KEY=sk-your-openai-api-key
   PROMPTLY_LLM_MODEL_DEFAULT=gpt-4o-mini
   ```

   Generate a unique JWT signing key and add it to the same untracked `docker/.env` file:

   ```bash
   if grep -q '^JWT__Key=' docker/.env; then
     echo "docker/.env already contains JWT__Key; replace that single value explicitly" >&2
     exit 1
   fi
   printf 'JWT__Key=%s\n' "$(openssl rand -base64 48)" >> docker/.env
   ```

   Promptly intentionally refuses to render the Compose service or start the Server when
   this key is missing or insecure. Database settings remain pre-configured; see
   `CONFIGURATION.md` for JWT rotation/incident response and `SETUP_CHECKLIST.md` for details.

3. **Start all services**
   ```bash
   cd docker
   docker compose up -d
   ```

4. **Access the application**
   - **Web UI**: http://localhost:3000
   - **API**: http://localhost:5000
   - **Swagger**: http://localhost:5000/swagger
   - **Python Worker**: internal service at `http://promptly-eval:8000`

5. **Initialize database**

   The database will be automatically migrated on first startup.

## Configuration

### C# Control Plane (Promptly.Server)

Configuration via `appsettings.json` or environment variables:

- **ConnectionStrings:Default**: PostgreSQL connection string
- **JWT:Key**: Required canonical-base64 JWT signing key (at least 32 decoded random bytes;
  generate a unique per-environment value with `openssl rand -base64 48`)
- **JWT:Issuer**: JWT issuer
- **JWT:Audience**: JWT audience
- **JWT:ExpiryMinutes**: Token expiry time (default: 60)
- **JWT:RetiredKeyFingerprints**: Optional comma-separated SHA-256 fingerprints of retired
  signing keys; startup rejects reuse (see `CONFIGURATION.md` for rotation and incident response)
- **AuthenticationAbuse:…**: Persisted account lockout plus bounded per-client,
  per-account, registration, login, password-spray, and combined in-flight controls. The
  zero-queue aggregate ceiling defaults to 16 and rejects excess work before reading its body.
  The current implementation supports exactly one API replica and refuses unsafe multi-replica
  configuration; see `CONFIGURATION.md` for thresholds, trusted-proxy handling, bounded metrics,
  and production Kestrel/reverse-proxy guidance.
- **DATA_PROTECTION_PATH**: Path for Data Protection keys persistence
- **PROMPTLY_EVAL_BASE_URL**: Python worker base URL
- **TestRunner:PollingIntervalSeconds**: Background worker polling interval (default: 5)
- **TestRunner:MaxConcurrentRuns**: Max concurrent test runs (default: 2)

### Python Worker (Promptly.Worker)

Configuration via environment variables:

**For Azure OpenAI:**
- **PROMPTLY_LLM_PROVIDER**: `azureopenai`
- **PROMPTLY_LLM_API_KEY**: Your Azure OpenAI API key
- **PROMPTLY_LLM_AZURE_ENDPOINT**: Your Azure endpoint (e.g., `https://your-resource.openai.azure.com`)
- **PROMPTLY_LLM_API_VERSION**: API version (default: `2024-08-01-preview`)
- **PROMPTLY_LLM_MODEL_DEFAULT**: **Your deployment name** (NOT model ID - use the name you gave the deployment in Azure AI Studio)

**For standard OpenAI:**
- **PROMPTLY_LLM_PROVIDER**: `openai`
- **PROMPTLY_LLM_API_KEY**: Your OpenAI API key (starts with `sk-`)
- **PROMPTLY_LLM_MODEL_DEFAULT**: Model ID (e.g., `gpt-4o-mini`)

**Important for Azure**: Use your deployment name, not the model name. If you deployed GPT-4o and named it "my-gpt4-deployment", use `my-gpt4-deployment` as the model default.

### React Frontend (Promptly.Web)

Configuration via environment variables:

- **VITE_API_BASE_URL**: Base URL for C# API (default: `http://localhost:5000`)

## Usage

### 1. Register & Login

Navigate to http://localhost:3000 and create an account.

### 2. Create a Project

Projects organize your testing efforts. Create a project for each application you're testing.

### 3. Set Up Environment

Define environments (dev, staging, prod) with base URLs and headers:
- **Base URL**: `http://localhost:5000/demo`
- **Headers**: Add authentication headers if needed (encrypted at rest)

### 4. Add Endpoint & Mapping

Create an endpoint pointing to your LLM application's API:
- **Path**: `/chat`
- **Method**: `POST`
- **Timeout**: 30s

Use the **Mapping Wizard** to define how to extract canonical traces from responses:
1. Provide a sample response JSON
2. AI proposes a mapping spec with JSONPath expressions
3. Validate the mapping against the sample
4. Save as default

### 5. Create Test Suite

Organize tests into suites. Import tests from YAML:

```yaml
- id: test-1
  name: Greeting Test
  description: Verify the assistant greets users properly
  input:
    messages:
      - role: user
        content: Hello!
  expectations:
    - type: contains_text
      text: "Hello"
      case_insensitive: true
    - type: banned_text
      text: "error"
      case_insensitive: true
```

Expectation types:
- **contains_text**: Substring search
- **banned_text**: Ensure text is NOT present
- **regex_match**: Pattern matching
- **link_pattern**: Verify URLs match pattern
- **tool_called**: Check if specific tool was called
- **tool_sequence**: Verify tool call order
- **llm_judge**: AI-based scoring with custom rubric
- **groundedness**: Check response is grounded in retrieved docs

### 6. Run Tests

Queue a test run:
- Select environment, endpoint, and mapping spec
- Optionally tag with Git commit hash
- View live progress and results

### 7. Analyze Results

View detailed results:
- **Summary**: Pass rate, latency, tokens, cost
- **Per-Test Results**: Status, expectations breakdown, failure reasons
- **Canonical Trace**: Messages, tool calls, usage, retrieved docs
- **Raw Response**: Original JSON for debugging

## MappingSpec Format

Define how to parse arbitrary JSON into canonical traces:

```json
{
  "version": 1,
  "messages": {
    "itemsPath": "$.choices[*].message",
    "rolePath": "$.role",
    "contentPath": "$.content"
  },
  "toolCalls": {
    "itemsPath": "$.tool_calls[*]",
    "namePath": "$.function.name",
    "argumentsPath": "$.function.arguments"
  },
  "usage": {
    "objectPath": "$.usage",
    "promptTokensPath": "$.prompt_tokens",
    "completionTokensPath": "$.completion_tokens",
    "totalTokensPath": "$.total_tokens"
  },
  "retrievedDocs": {
    "itemsPath": "$.retrieved_docs[*]",
    "contentPath": "$.content",
    "titlePath": "$.title",
    "idPath": "$.id"
  },
  "fallback": {
    "singleAssistantContentPath": "$.response"
  }
}
```

## CLI Tool

Install the CLI tool:

```bash
dotnet tool install --global Promptly.Cli
```

Trigger runs from CI/CD:

```bash
promptly trigger \
  --base-url http://localhost:5000 \
  --api-key <your-api-key> \
  --suite <suite-id> \
  --env <environment-id> \
  --endpoint <endpoint-id> \
  --mapping <mapping-id> \
  --commit $(git rev-parse HEAD)

promptly wait --base-url http://localhost:5000 --api-key <your-api-key> --run-id <run-id>
```

Exit code:
- **0**: All tests passed
- **1**: One or more tests failed

## Development

### Running Locally (Without Docker)

**C# API**:
```bash
cd Promptly.Server
dotnet ef database update
dotnet run
```

**Python Worker**:
```bash
cd Promptly.Worker
uv sync --frozen --all-groups
uv run uvicorn main:app --reload --port 8000
```

See [`Promptly.Worker/README.md`](Promptly.Worker/README.md) for the authoritative locked
dependency and verification workflow.

**React Frontend**:
```bash
cd Promptly.Web/src/web
npm install
npm run dev
```

### Database Migrations

Create a new migration:
```bash
cd Promptly.Server
dotnet ef migrations add <MigrationName>
dotnet ef database update
```

## API Documentation

Swagger UI is available at http://localhost:5000/swagger with JWT bearer authentication.

Key endpoints:
- **POST /api/auth/register**: Create account
- **POST /api/auth/login**: Get JWT token
- **POST /api/projects**: Create project
- **POST /api/environments**: Create environment
- **POST /api/endpoints/{id}/mapping/propose**: AI-powered mapping proposal
- **POST /api/suites**: Create test suite
- **POST /api/suites/{id}/tests/import**: Import tests from YAML
- **POST /api/runs**: Queue test run
- **GET /api/runs/{id}/results**: Get run results

Authentication throttles return `429 application/problem+json`, a bounded integer
`Retry-After` header, and the stable code `authentication_rate_limited`. Clients should wait
for that interval before retrying. Credential failures remain a generic `401` so missing,
wrong-password, and locked accounts are not distinguished.

## Troubleshooting

### Database Connection Issues

Ensure PostgreSQL is running:
```bash
docker compose ps postgres
```

Check logs:
```bash
docker compose logs postgres
```

### Python Worker Errors

Check LLM API key is set:
```bash
docker compose exec promptly-eval python -c \
  "import os, sys; sys.exit(0 if os.environ.get('PROMPTLY_LLM_API_KEY') else 1)"
```

View logs:
```bash
docker compose logs promptly-eval
```

### Frontend Connection Issues

Ensure VITE_API_BASE_URL points to the correct API URL. Check browser console for errors.

### Background Worker Not Processing Runs

Check worker logs:
```bash
docker compose logs promptly-server | grep TestRunWorkerService
```

Verify TestRunner configuration in appsettings.json.

## Tech Stack

- **Backend**: ASP.NET Core 10, Entity Framework Core, PostgreSQL
- **Worker**: Python 3.11+, FastAPI, OpenAI SDK
- **Frontend**: React 19, TypeScript, Material-UI, Vite
- **Auth**: ASP.NET Identity, JWT, API Keys
- **Security**: Data Protection API for encrypted storage
- **Mapping**: JsonPath.Net for deterministic parsing
- **Evaluation**: Regex, LLM judges (OpenAI/Azure)

## License

[Specify your license here]

## Contributing

[Specify contribution guidelines]

## Support

For issues and questions, please open a GitHub issue.
