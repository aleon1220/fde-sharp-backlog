# Multi-Provider AI Orchestration (`Gemini` & `Microsoft.Copilot.SDK`)

Configure, authenticate, discover models for, and verify all supported AI providers under strict Zero Hallucination and Type `01`/`02` formatting rules.

## Supported Providers

Preserves strict **Zero Hallucination** prompt rules and Type `01`/`02` formatting across all `--provider` targets:

1. **`gemini` (Default — Google Gemini REST):** Resolves keys from `--api-key`, `GEMINI_API_KEY`, `GOOGLE_API_KEY`, `AI_API_KEY`, or masked interactive prompt (`*` keystrokes via `ReadKey`); supports `--offline` / `/live off`.
2. **`copilot-sdk` (`Microsoft.Copilot.SDK` `2.0.1`):** Uses `CopilotClient` (`CopilotClientMode.CodingAgent`) and `CreateSessionOptions` with strict prompt isolation (disabled config discovery, skills, repository hooks, host git, and built-in tools). Supports in-process FFI, spawned stdio (`COPILOT_CLI_PATH` or `copilot` on `PATH`), and attached TCP (`COPILOT_CLI_URL`).
3. **`openai` (OpenAI Gateway API — Direct HTTP):** Native HTTP client ([`OpenAiGatewayClient`](../../src/SmartBacklogAssistant/Services/OpenAiGatewayClient.cs)) supporting `/v1/chat/completions` (`chat`, default) and `/v1/responses` (`responses`), custom OpenAI-compatible gateway URLs (`OPENAI_BASE_URL`, `--gateway-url`, or `openAiBaseUrl`/`openAiWireApi` in `settings.json`), `/models` catalog discovery (`vendor/model` gateway slugs included), Bearer / Azure `api-key` auth, masked interactive key prompt (`*` keystrokes via `ReadKey`; `Enter` for offline fallback), and deterministic offline fallback (`--offline` or when credentials are unset).
4. **`azure` / `claude` (Direct HTTP / BYOK Live or Deterministic Mock):** When `AZURE_OPENAI_ENDPOINT` + `AZURE_OPENAI_API_KEY` are configured without a Copilot CLI runtime, `azure` falls back to direct OpenAI Gateway HTTP deployment calls; with a Copilot runtime, `azure` / `claude` / Ollama route through `Microsoft.Copilot.SDK` `SessionInferenceOptions` (`NamedProviderConfig` with CAPI disabled), falling back deterministically otherwise. Both providers support live key verification and model discovery when configured.
5. **`manual`:** Renders the Type `01`/`02` Markdown prompt alongside a deterministic baseline draft.

## Credentials

Pick one provider or use `--offline` for deterministic drafting:

* **Gemini (`gemini`, default):** `GEMINI_API_KEY` (or `--api-key` from [Google AI Studio](https://aistudio.google.com/api-keys)).
* **OpenAI Gateway API (`openai`):** `OPENAI_API_KEY` (or `--api-key`) plus optional `OPENAI_BASE_URL` (`--gateway-url`, defaults to `https://api.openai.com/v1`), `OPENAI_WIRE_API` (`chat` or `responses`), and `OPENAI_MODEL` (defaults to `gpt-4o`). Supports OpenAI, Azure OpenAI/Foundry, LiteLLM, OpenRouter, vLLM, and Ollama `/v1` gateways.
* **Copilot SDK (`copilot-sdk`):** GitHub Copilot CLI (`copilot` on `PATH` or `COPILOT_CLI_PATH`), a remote runtime (`COPILOT_CLI_URL`), or bundled runtime (`-p:CopilotSkipCliDownload=false`) + authentication (`copilot login`, `COPILOT_GITHUB_TOKEN`, `GITHUB_TOKEN`, or `GH_TOKEN`).
* **Azure / BYOK (`azure` / `claude` / Ollama):** `AZURE_OPENAI_ENDPOINT` + `AZURE_OPENAI_API_KEY` (uses direct HTTP gateway fallback even without Copilot CLI), `ANTHROPIC_API_KEY`, or `OLLAMA_HOST` routed via `Microsoft.Copilot.SDK`.

## Provider Selection & Verification Commands

```bash
# Persist OpenAI Gateway API (or Copilot SDK) with optional custom URL & wire protocol
dotnet run --project src/SmartBacklogAssistant -- config --provider openai --gateway-url https://api.openai.com/v1 --wire-api chat
dotnet run --project src/SmartBacklogAssistant -- config --provider copilot-sdk

# 1. Verify paths, masked credentials, wire API/URL readiness, and 6-provider AI Matrix
dotnet run --project src/SmartBacklogAssistant -- status --provider openai
dotnet run --project src/SmartBacklogAssistant -- status --provider copilot-sdk

# 2. Discover compatible models from OpenAI Gateway (/v1/models) or Copilot SDK
dotnet run --project src/SmartBacklogAssistant -- models --provider openai --gateway-url https://api.openai.com/v1
dotnet run --project src/SmartBacklogAssistant -- models --provider copilot-sdk

# 3. Preview a live draft with OpenAI Gateway (/chat/completions or /responses) or --offline fallback
dotnet run --project src/SmartBacklogAssistant -- draft "Implement OAuth2 PKCE Login" --type 01 --provider openai --wire-api responses --preview
dotnet run --project src/SmartBacklogAssistant -- draft "Implement OAuth2 PKCE Login" --type 01 --provider openai --preview --offline
dotnet run --project src/SmartBacklogAssistant -- draft "Implement OAuth2 PKCE Login" --type 01 --provider copilot-sdk --preview --offline
```

See also: [environment-variables.md](environment-variables.md) · [logging-and-secrets.md](logging-and-secrets.md) · [build-from-source.md](../getting-started/build-from-source.md)
