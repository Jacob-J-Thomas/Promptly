# ✅ Promptly Setup Checklist

## What You Need to Provide

### 1. Azure OpenAI Configuration (REQUIRED)

Create `docker/.env` file with:

```bash
PROMPTLY_LLM_PROVIDER=azureopenai
PROMPTLY_LLM_API_KEY=___________________  # ← PASTE YOUR KEY HERE
PROMPTLY_LLM_AZURE_ENDPOINT=___________________  # ← PASTE YOUR ENDPOINT HERE
PROMPTLY_LLM_MODEL_DEFAULT=___________________  # ← YOUR DEPLOYMENT NAME HERE
JWT__Key=___________________  # ← OUTPUT OF: openssl rand -base64 48
# Leave at 1 until Promptly has a shared authentication limiter store.
AuthenticationAbuse__ApiReplicaCount=1
# Combined zero-queue registration/login in-flight ceiling (validated 1-256).
AuthenticationAbuse__MaximumConcurrentAuthenticationRequests=16
```

**Where to find these:**
- **API Key**: Azure Portal → Your OpenAI Resource → Keys and Endpoint → KEY 1
- **Endpoint**: Azure Portal → Your OpenAI Resource → Keys and Endpoint → Endpoint
  - Format: `https://your-resource-name.openai.azure.com`
- **Deployment Name**: Azure AI Studio → Deployments → Your deployment name
  - Example: `gpt-4o-deployment` or `gpt-35-turbo`

### 2. Test Service/Endpoint (OPTIONAL - Built-in demo available)

**Option A**: Use built-in demo at `/demo/chat` - No setup needed! ✅

**Option B**: Test your own service - Configure after startup in the UI:
- You'll add these in the Environments section
- Base URL: Your service URL
- Headers: Any auth tokens needed (encrypted at rest)

### 3. Database (NO ACTION NEEDED)

✅ PostgreSQL runs in Docker automatically - already configured!

---

## Quick Start

1. **Create the .env file** with your Azure OpenAI credentials
2. **Start services**:
   ```bash
   cd docker
   docker compose up -d
   ```
3. **Wait 30 seconds** for services to initialize
4. **Open browser**: http://localhost:3000

---

## Verification Steps

### Check Python Worker Readiness
```bash
docker compose exec promptly-eval python -c \
  "import urllib.request; print(urllib.request.urlopen('http://127.0.0.1:8000/health/ready').read().decode())"
```

**Expected output:**
```json
{
  "status": "ready",
  "llm_provider": "azureopenai",
  "llm_configured": true
}
```

### Test Demo Endpoint
```bash
curl -X POST http://localhost:5000/demo/chat \
  -H "Content-Type: application/json" \
  -d '{"messages":[{"role":"user","content":"Hello"}]}'
```

### Access Swagger UI
Open: http://localhost:5000/swagger

---

## Summary of Configuration Locations

| What | Where | Status |
|------|-------|--------|
| **Azure OpenAI Key** | `docker/.env` | ⚠️ YOU PROVIDE |
| **Azure Endpoint** | `docker/.env` | ⚠️ YOU PROVIDE |
| **Deployment Name** | `docker/.env` | ⚠️ YOU PROVIDE |
| **Database** | Docker container | ✅ AUTO-CONFIGURED |
| **JWT signing key** | untracked `docker/.env` / secret manager | ⚠️ YOU GENERATE |
| **API replica count** | `AuthenticationAbuse__ApiReplicaCount=1` | ✅ REQUIRED FOR CURRENT LIMITER |
| **Authentication concurrency** | `AuthenticationAbuse__MaximumConcurrentAuthenticationRequests=16` | ✅ BOUNDED DEFAULT |
| **Test Endpoint** | Built-in or UI | ✅ BUILT-IN DEMO AVAILABLE |

---

## That's It!

Provide the three Azure OpenAI values plus a unique JWT signing key. Promptly fails startup
if the key is missing or insecure; see `CONFIGURATION.md` for rotation guidance.

See `CONFIGURATION.md` for detailed explanation of each setting.
