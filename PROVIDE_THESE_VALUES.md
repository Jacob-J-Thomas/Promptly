# 📋 Values You Need to Provide

## Summary

I've updated Promptly to use **Azure AI Foundry / Azure OpenAI as the default**. Here's exactly what you need to provide:

---

## 🔑 REQUIRED: Azure OpenAI Credentials

Create this file: **`docker/.env`**

```bash
# Paste your Azure OpenAI values here:
PROMPTLY_LLM_PROVIDER=azureopenai
PROMPTLY_LLM_API_KEY=________________________
PROMPTLY_LLM_AZURE_ENDPOINT=________________________
PROMPTLY_LLM_MODEL_DEFAULT=________________________
```

### Where to get these values:

1. **PROMPTLY_LLM_API_KEY**
   - Azure Portal → Your Azure OpenAI resource → "Keys and Endpoint" → Copy KEY 1 or KEY 2

2. **PROMPTLY_LLM_AZURE_ENDPOINT**
   - Azure Portal → Your Azure OpenAI resource → "Keys and Endpoint" → Copy "Endpoint"
   - Should look like: `https://your-resource-name.openai.azure.com`

3. **PROMPTLY_LLM_MODEL_DEFAULT**
   - Azure AI Studio → Go to "Deployments"
   - Copy the **deployment name** (NOT the model name!)
   - Example: If you deployed GPT-4o and named it "my-gpt4", use `my-gpt4`

---

## 🌐 OPTIONAL: Test Service URL & Credentials

**You have two options:**

### Option 1: Use Built-in Demo (Recommended for initial testing)
- ✅ No setup needed!
- The `/demo/chat` endpoint is already implemented
- Returns realistic LLM-style responses for testing

### Option 2: Test Your Own Service
If you want to test against your own LLM service:

**You'll configure this in the UI after starting Promptly:**
1. Navigate to Projects → Environments
2. Click "Create Environment"
3. Enter:
   - **Base URL**: Your service URL (e.g., `https://api.example.com`)
   - **Headers**: Any auth headers needed (e.g., `Authorization: Bearer YOUR_TOKEN`)
   - Headers are encrypted at rest automatically

**Example:**
```
Environment Name: My Production API
Base URL: https://api.mycompany.com
Headers:
{
  "Authorization": "Bearer abc123xyz",
  "X-Custom-Header": "value"
}
```

---

## 💾 Database

**✅ NO ACTION NEEDED**

The PostgreSQL database:
- Runs automatically in Docker
- Is already configured with credentials
- Will be created and initialized on first startup
- You don't need to set up anything!

---

## 📂 File Locations Summary

| File/Location | What | Status |
|---------------|------|--------|
| `docker/.env` | Azure OpenAI credentials | ⚠️ **YOU CREATE THIS** |
| `docker/docker-compose.yml` | Service configuration | ✅ Already configured |
| `docker/.env.example` | Template file | ✅ Reference available |
| Database | PostgreSQL | ✅ Auto-configured in Docker |
| Test endpoint | Demo or your service | ✅ Demo built-in / configure yours in UI |

---

## ✅ After You Provide the Values

Once you've created `docker/.env` with your Azure OpenAI credentials:

```bash
cd docker
docker compose up -d
```

Then verify:

```bash
# Check Python worker can authenticate to the configured provider
docker compose exec promptly-eval python -c \
  "import urllib.request; print(urllib.request.urlopen('http://127.0.0.1:8000/health/ready').read().decode())"

# Expected output:
{
  "status": "ready",
  "llm_provider": "azureopenai",
  "llm_configured": true
}
```

---

## 🎯 Quick Reference

**Minimum required to start testing:**
1. Create `docker/.env`
2. Add 3 Azure OpenAI values (key, endpoint, deployment name)
3. Run `docker compose up -d`
4. Open http://localhost:3000

**That's it!** Everything else is pre-configured.

---

## 📧 Questions?

- **Azure OpenAI API format**: See `CONFIGURATION.md` for detailed Azure setup
- **Using standard OpenAI instead**: See `.env.example` for OpenAI configuration
- **Troubleshooting**: See `CONFIGURATION.md` troubleshooting section

---

## 🚀 I'm Ready to Test!

Once you provide these values, I can:
- ✅ Start the entire system with `docker compose up`
- ✅ Test LLM judge evaluations
- ✅ Test mapping proposal (AI-powered)
- ✅ Test groundedness scoring
- ✅ Run end-to-end test scenarios
- ✅ Verify all components work together

**Please provide:**
1. Your Azure OpenAI API key
2. Your Azure OpenAI endpoint
3. Your deployment name

And let me know if you want to use the built-in demo endpoint or if you have your own service to test against!
