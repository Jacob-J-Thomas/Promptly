# Promptly Configuration Guide

This guide explains all the configuration values needed to run Promptly with Azure AI Foundry / Azure OpenAI.

---

## 🔑 Required Configuration

### 1. **Azure OpenAI / Azure AI Foundry Configuration**

Create a `.env` file in the `docker` directory with these values:

```bash
# Azure OpenAI Configuration
PROMPTLY_LLM_PROVIDER=azureopenai
PROMPTLY_LLM_API_KEY=<YOUR_AZURE_OPENAI_API_KEY>
PROMPTLY_LLM_AZURE_ENDPOINT=<YOUR_AZURE_ENDPOINT>
PROMPTLY_LLM_API_VERSION=2024-08-01-preview
PROMPTLY_LLM_MODEL_DEFAULT=<YOUR_DEPLOYMENT_NAME>
```

#### Where to find these values:

| Variable | Where to Find It |
|----------|-----------------|
| **PROMPTLY_LLM_API_KEY** | Azure Portal → Your Azure OpenAI resource → Keys and Endpoint → KEY 1 or KEY 2 |
| **PROMPTLY_LLM_AZURE_ENDPOINT** | Azure Portal → Your Azure OpenAI resource → Keys and Endpoint → Endpoint<br/>Example: `https://your-resource-name.openai.azure.com` |
| **PROMPTLY_LLM_MODEL_DEFAULT** | Azure AI Studio → Your deployment name (NOT the model name)<br/>Example: `gpt-4o-deployment` or `gpt-35-turbo` |

**Important**: In Azure OpenAI, you specify the **deployment name**, not the model ID. When you deploy a model in Azure AI Studio, you give it a deployment name - that's what goes here.

---

### 2. **Database Configuration** (Already Configured ✅)

The database runs in Docker and is **already configured** with default values:

```bash
POSTGRES_DB=promptly
POSTGRES_USER=promptly
POSTGRES_PASSWORD=promptly_dev_password
ConnectionStrings__Default=Host=postgres;Database=promptly;Username=promptly;Password=promptly_dev_password
```

**You don't need to set up a separate database!** Docker Compose automatically creates and initializes the PostgreSQL database.

---

### 3. **Test Service / Endpoint to Test Against**

You have **two options**:

#### Option A: Use the Built-in Demo Endpoint (Recommended for Initial Testing)

The system includes a demo endpoint at `/demo/chat` that simulates an LLM service. No additional setup needed.

**Test it:**
```bash
curl -X POST http://localhost:5000/demo/chat \
  -H "Content-Type: application/json" \
  -d '{
    "messages": [
      {"role": "user", "content": "Hello!"}
    ]
  }'
```

#### Option B: Point to Your Own Service

If you have your own LLM service to test, you'll configure it in the UI after starting Promptly:

1. Navigate to Environments
2. Set **Base URL** to your service URL (e.g., `https://your-api.example.com`)
3. Add any required **Headers** (like `Authorization: Bearer YOUR_TOKEN`)
   - Headers are encrypted at rest using ASP.NET Core Data Protection

**Example Environment Configuration:**
- **Name**: Production API
- **Base URL**: `https://api.example.com`
- **Headers**:
  ```json
  {
    "Authorization": "Bearer YOUR_API_TOKEN",
    "X-Custom-Header": "value"
  }
  ```

---

### 4. **JWT Signing Key** (Required)

Generate a unique 48-byte key for each environment and put it in that environment's
secret manager or untracked `docker/.env` file:

```bash
openssl rand -base64 48
# Copy the output into docker/.env:
JWT__Key=<BASE64_OUTPUT>
```

Promptly has no fallback signing key. Startup fails in every environment when the value is
missing, is the historic public default, is not canonical base64, decodes to fewer than 32
bytes, has a recognized low-entropy/predictable structure, or matches a configured retired-key
fingerprint. A validator cannot prove how an otherwise plausible sample was generated, so
operators must still use the documented cryptographically secure generator. Never reuse a
development or test signing key in Production.

### 5. **Authentication Abuse Controls**

Promptly enables persisted ASP.NET Identity lockout and bounded process-local fixed-window
limits for registration and login requests. Defaults are fail-closed and can be overridden
with the usual double-underscore environment-variable syntax:

| Setting | Default | Purpose |
|---|---:|---|
| `AuthenticationAbuse__ApiReplicaCount` | `1` | Startup guard for the process-local implementation; any other value is rejected. |
| `AuthenticationAbuse__IdentityMaxFailedAccessAttempts` | `5` | Failed passwords before persisted account lockout. |
| `AuthenticationAbuse__IdentityLockoutSeconds` | `900` | Automatic lockout recovery interval. |
| `AuthenticationAbuse__LoginIpPermitLimit` / `LoginIpWindowSeconds` | `30` / `60` | Login attempts per client and window. |
| `AuthenticationAbuse__RegistrationIpPermitLimit` / `RegistrationIpWindowSeconds` | `5` / `900` | Registrations per client and window. |
| `AuthenticationAbuse__LoginAccountPermitLimit` / `LoginAccountWindowSeconds` | `10` / `900` | Login attempts per normalized account and window. |
| `AuthenticationAbuse__RegistrationAccountPermitLimit` / `RegistrationAccountWindowSeconds` | `3` / `900` | Registration attempts per normalized account and window. |
| `AuthenticationAbuse__PasswordSprayDistinctAccountLimit` | `10` | Distinct failed account targets from one client before blocking it. |
| `AuthenticationAbuse__PasswordSprayWindowSeconds` / `PasswordSprayBlockSeconds` | `600` / `900` | Spray observation and client-block periods. |
| `AuthenticationAbuse__MaximumTrackedPartitions` | `10000` | Hard memory bound split across independently reserved login-client, registration-client, login-account/spray, and registration-account cohorts; new partitions fail closed within their cohort at capacity. |
| `AuthenticationAbuse__MaximumTrackedSprayAccountEntries` | `50000` | Hard global bound for distinct hashed account keys retained by password-spray tracking; new distinct entries fail closed at capacity. |
| `AuthenticationAbuse__AccountLockStripeCount` | `256` | Bounded zero-queue synchronization stripes for Identity updates; a busy stripe returns a generic one-second `429` instead of accumulating waiters. |
| `AuthenticationAbuse__MaximumRetryAfterSeconds` | `900` | Maximum integer `Retry-After` returned to clients. |

Limiter keys are one-way, process-keyed HMAC values; raw IP addresses and normalized account
identifiers are not retained in limiter state. A restart clears request-limit state, while
Identity lockout remains in PostgreSQL. This release deliberately supports one Server API
replica: horizontal API scaling requires a shared, atomic limiter store and must not be enabled
by bypassing the startup validator.

Promptly ignores `X-Forwarded-For` by default. If a reverse proxy is the Server's only direct
peer, add each exact canonical CIDR as an indexed value such as
`AuthenticationAbuse__TrustedProxyNetworks__0=10.20.0.0/16`. Only a single unscoped unicast IP
literal from an explicitly trusted direct peer is accepted; lists, ports, malformed values,
and headers from untrusted peers are ignored. At most 256 trusted proxy networks are accepted.
Publicly reachable proxies must be configured as exact `/32` or `/128` host routes; broader
CIDRs are accepted only inside validated private and special-use ranges. IPv4 clients are
partitioned by exact address; IPv6 clients are deliberately aggregated by canonical `/64`
prefix so ordinary privacy-address rotation cannot bypass client or password-spray limits.

Throttled authentication requests return `429 application/problem+json`, `Cache-Control:
no-store`, a bounded integer `Retry-After`, and stable problem code
`authentication_rate_limited`. Missing users, incorrect passwords, and locked accounts all
return the same generic `401` response.

Authentication JSON bodies are capped at 8 KiB before model binding. Email addresses are capped
at 256 characters to match the ASP.NET Identity persistence boundary; registration display names
have a separate 256-character input bound. Passwords are capped at 128 characters while
registration retains its 8-character minimum. Overlong fields are rejected before account
partitioning, password verification, or user persistence.

---

## 📝 Complete .env File Template

Create `docker/.env` with these values:

```bash
# ========================================
# REQUIRED: Azure OpenAI Configuration
# ========================================
PROMPTLY_LLM_PROVIDER=azureopenai
PROMPTLY_LLM_API_KEY=<PASTE_YOUR_AZURE_OPENAI_KEY_HERE>
PROMPTLY_LLM_AZURE_ENDPOINT=<PASTE_YOUR_AZURE_ENDPOINT_HERE>
PROMPTLY_LLM_API_VERSION=2024-08-01-preview
PROMPTLY_LLM_MODEL_DEFAULT=<YOUR_DEPLOYMENT_NAME>

# ========================================
# Database (Leave as-is for development)
# ========================================
POSTGRES_DB=promptly
POSTGRES_USER=promptly
POSTGRES_PASSWORD=promptly_dev_password
ConnectionStrings__Default=Host=postgres;Database=promptly;Username=promptly;Password=promptly_dev_password

# ========================================
# JWT (required; unique per environment)
# ========================================
JWT__Key=<OUTPUT_OF_OPENSSL_RAND_BASE64_48>
# Optional comma-separated SHA-256 fingerprints of retired keys
JWT__RetiredKeyFingerprints=
JWT__Issuer=Promptly
JWT__Audience=Promptly
JWT__ExpiryMinutes=60

# Authentication abuse controls are process-local and require one API replica.
AuthenticationAbuse__ApiReplicaCount=1

# ========================================
# Optional: Test Runner Settings
# ========================================
TestRunner__PollingIntervalSeconds=5
TestRunner__MaxConcurrentRuns=2
```

---

## 🎯 What Each Service Needs

### **C# API (promptly-server)**
- ✅ Database connection string (configured)
- ⚠️ JWT signing key (YOU GENERATE and inject)
- ✅ Data Protection path (configured)
- ✅ Python worker URL (configured via Docker network)

### **Python Worker (promptly-eval)**
- ⚠️ **Azure OpenAI API Key** (YOU PROVIDE)
- ⚠️ **Azure Endpoint** (YOU PROVIDE)
- ⚠️ **Deployment Name** (YOU PROVIDE)
- ✅ API Version (configured with sensible default)

### **PostgreSQL Database**
- ✅ Runs in Docker (no external setup needed)

### **React Frontend (promptly-web)**
- ✅ API base URL (configured)

---

## 🚀 Quick Start After Configuration

Once you've created the `.env` file:

```bash
cd docker
docker compose up -d
```

The Server refuses to start when `JWT__Key` is absent or empty.

Wait ~30 seconds for services to start, then:

- **Web UI**: http://localhost:3000
- **API**: http://localhost:5000
- **Swagger**: http://localhost:5000/swagger
- **Python Worker**: internal service at `http://promptly-eval:8000`

---

## JWT key rotation and incident response

Treat the signing key as a production secret. Prefer a per-environment secret-manager
injection over a dotenv file; restrict read access, avoid shell history and logs, and use
an independent key for each deployment.

For a planned rotation:

1. Generate a new key with `openssl rand -base64 48`. Before replacing the old value,
   calculate its fingerprint from the authoritative secret-manager value. For the local
   `docker/.env` path, the checked helper rejects missing, empty, duplicate, or non-canonical
   Base64 entries, reads the value without printing it or embedding it in shell history,
   and prints only its SHA-256 fingerprint:

   ```bash
   docker/fingerprint-jwt-key.sh docker/.env
   ```

   The first upgrade from a Promptly version that treated `JWT__Key` as literal UTF-8 text
   must fingerprint those exact legacy bytes instead. Run the helper once in legacy mode
   before replacing the old value, then use the default canonical-Base64 mode for every
   later rotation:

   ```bash
   docker/fingerprint-jwt-key.sh --legacy-raw docker/.env
   ```

2. Add that 64-character digest to the comma-separated
   `JWT__RetiredKeyFingerprints` value, inject the new `JWT__Key`, and restart every Server
   replica together. Compose forwards both settings. Startup rejects accidental reuse of
   any listed key.
3. Existing JWTs immediately become invalid. Notify users that they must sign in again and
   monitor authentication failures for expected convergence.
4. Retain fingerprints—not old keys—in the deployment's security record.

If the historic public key or any active key may have been used or disclosed, treat it as
an authentication compromise: rotate immediately, invalidate all sessions by restarting
on the new key, inspect authentication and sensitive-resource access logs from the exposure
window, notify affected operators/users, and rotate downstream credentials that may have
been exposed. Never commit the replacement key.

HMAC remains the only supported signing mode in this release because Promptly currently
issues and validates its own tokens. First-class asymmetric signing requires a separately
designed issuer/trust and rotation contract; do not assume an external identity provider is
compatible until that integration exists and is tested. Environments requiring independent
signer/verifier custody should treat that missing integration as a deployment blocker.

---

## 🔍 Verifying Configuration

### Test Azure OpenAI Connection

```bash
# Check Python worker readiness (authenticates to the configured provider)
docker compose exec promptly-eval python -c \
  "import urllib.request; print(urllib.request.urlopen('http://127.0.0.1:8000/health/ready').read().decode())"

# Should return:
{
  "status": "ready",
  "llm_provider": "azureopenai",
  "llm_configured": true
}
```

### Test Database Connection

```bash
# Check if database is accepting connections
docker exec promptly-postgres pg_isready -U promptly

# Should return:
/var/run/postgresql:5432 - accepting connections
```

### Test Demo Endpoint

```bash
curl -X POST http://localhost:5000/demo/chat \
  -H "Content-Type: application/json" \
  -d '{"messages":[{"role":"user","content":"Hello"}]}'
```

---

## ❓ FAQ

### Q: Do I need an Azure subscription?
**A:** Yes, you need an Azure OpenAI resource deployed in your Azure subscription.

### Q: Can I use OpenAI instead of Azure?
**A:** Yes! Change `PROMPTLY_LLM_PROVIDER=openai` and use `PROMPTLY_LLM_API_KEY=sk-...` with OpenAI key.

### Q: What if my deployment name has spaces?
**A:** That's fine, Azure handles it. Just use the exact deployment name as shown in Azure AI Studio.

### Q: Do I need to expose my database publicly?
**A:** No! The database runs in Docker's internal network. It's not exposed outside your local machine (unless you explicitly configure port forwarding in production).

### Q: What Azure OpenAI models are supported?
**A:** Any chat completion model: GPT-4, GPT-4 Turbo, GPT-4o, GPT-3.5-Turbo. Make sure your deployment supports `response_format={"type": "json_object"}`.

---

## 🆘 Troubleshooting

### "PROMPTLY_LLM_API_KEY environment variable is not set"
- You forgot to create the `.env` file or it's in the wrong location
- Should be in `docker/.env`

### "PROMPTLY_LLM_AZURE_ENDPOINT is required"
- Your `.env` file is missing the `PROMPTLY_LLM_AZURE_ENDPOINT` variable
- Format: `https://your-resource-name.openai.azure.com`

### "Model not found" or "Deployment not found"
- Your `PROMPTLY_LLM_MODEL_DEFAULT` doesn't match an actual deployment name
- Go to Azure AI Studio → Deployments and verify the exact name

### Database connection errors
- Run `docker compose logs postgres` to see database logs
- Ensure PostgreSQL container is running: `docker ps | grep postgres`

---

## 📧 Need Help?

1. Check logs: `docker compose logs <service-name>`
2. Verify configuration without printing resolved secrets: `docker compose config --quiet`
3. Restart services: `docker compose restart`
