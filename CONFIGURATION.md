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

### 4. **JWT Secret** (Already Configured ✅)

The JWT secret is already set in docker-compose.yml:

```bash
JWT__Key=YourSuperSecretJWTKeyThatShouldBeAtLeast32CharactersLongForProduction
```

For development/testing, this default is fine. For production, change it to a secure random string (32+ characters).

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
# JWT (Leave as-is for development)
# ========================================
JWT__Key=YourSuperSecretJWTKeyThatShouldBeAtLeast32CharactersLongForProduction
JWT__Issuer=Promptly
JWT__Audience=Promptly
JWT__ExpiryMinutes=60

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
- ✅ JWT settings (configured)
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

Wait ~30 seconds for services to start, then:

- **Web UI**: http://localhost:3000
- **API**: http://localhost:5000
- **Swagger**: http://localhost:5000/swagger
- **Python Worker**: internal service at `http://promptly-eval:8000`

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
2. Verify configuration: `docker compose config`
3. Restart services: `docker compose restart`
