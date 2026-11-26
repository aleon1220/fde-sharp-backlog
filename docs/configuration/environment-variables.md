# Environment Variables

Reference for AI provider credentials, gateway overrides, diagnostic logging, NuGet proxy/fallback feeds, and GitHub Project V2 automation.

## Authentication, NuGet Restore & Runtime Environment Variables

| Variable | Purpose | Notes |
| --- | --- | --- |
| `OPENAI_API_KEY` | OpenAI Gateway / compatible proxy API key | Sent as `Authorization: Bearer` (or Azure `api-key` on Azure hosts); overridden by `--api-key`; never written to `settings.json` |
| `OPENAI_BASE_URL` | Custom OpenAI Gateway base URL | Defaults to `https://api.openai.com/v1`; supports bare domains, custom subpaths, and Azure endpoints; overridden by `--gateway-url` (`--base-url`) |
| `OPENAI_WIRE_API` | OpenAI Gateway wire protocol | `chat` (`/v1/chat/completions`, default) or `responses` (`/v1/responses`); alias `OPENAI_API_MODE`; overridden by `--wire-api` |
| `OPENAI_MODEL` | Default OpenAI Gateway model ID | Defaults to `gpt-4o`; supports standard IDs (`gpt-4.1-mini`) and `vendor/model` gateway slugs |
| `COPILOT_GITHUB_TOKEN` / `GITHUB_TOKEN` / `GH_TOKEN` | GitHub auth for Copilot SDK | Evaluated in listed order; falls back to `copilot login` keychain auth when unset (`GITHUB_TOKEN` also authenticates GitHub Packages NuGet proxies) |
| `COPILOT_CLI_PATH` | Explicit Copilot CLI / runtime path | Uses in-process FFI when native runtime (`libcopilot_runtime.*` / `runtime.node`) is adjacent; otherwise spawns `--stdio` |
| `COPILOT_CLI_URL` | Attached remote runtime over TCP | Accepts `host:port`, `http://host:port`, or `[::1]:port` (`RuntimeConnection.ForTcp`) |
| `COPILOT_SDK_DEFAULT_CONNECTION` | Default local transport | `inprocess` (SDK v2 default) or `stdio` |
| `COPILOT_MODEL` / `GEMINI_MODEL` | Copilot / Gemini model ID override | Defaults: `gpt-5-mini` (`copilot-sdk`), `gemini-3.8-flash` (`gemini`) |
| `GEMINI_API_KEY` | Google Gemini REST API key | Can also be managed via `keys` / `[K]` or overridden via `--api-key`; never written to `settings.json` |
| `AZURE_OPENAI_API_KEY` / `AZURE_OPENAI_ENDPOINT` | Azure OpenAI / AI Foundry key & endpoint | Managed via `[K]` / `keys set azure` or env vars; supports direct HTTP & BYOK Copilot SDK modes |
| `ANTHROPIC_API_KEY` / `ANTHROPIC_BASE_URL` | Anthropic Claude API key & optional base URL | Managed via `[K]` / `keys set claude` or env vars; supports live verification & model listing |
| `SBA_LOG_LEVEL` | Diagnostic `stderr` log severity | `Trace`, `Debug`, `Information`, `Warning` (default), `Error`, `Critical`, `None` (`Quiet`) |
| `SBA_LOG_FORMAT` | Diagnostic `stderr` log format | `text` (default timestamped) or `json` (structured NDJSON); always routed to `stderr` |
| `NUGET_PROXY_FEED_URL` | Upstream NuGet caching proxy URL | HTTPS feed URL (GitHub Packages, Azure Artifacts, Artifactory); set `NUGET_PROXY_ONLY=true` to disable direct `nuget.org` fallback |
| `NUGET_AUTH_TOKEN` / `VSS_NUGET_ACCESSTOKEN` | Ephemeral token-scoped NuGet feed auth | Supplied by CI OIDC / workload identity (`NUGET_AUTH_USER` defaults to `x-access-token` on GitHub Packages or `azdo`); redacted in `stderr` logs |
| `NUGET_FALLBACK_FEED_DIR` / `NUGET_FALLBACK_PACKAGES_DIR` | Local NuGet fallback feed or cache dir | Auto-detected by [`Directory.Build.props`](../../Directory.Build.props) and restore scripts only when directory exists on disk (also auto-uses `.nuget/fallback`) |
| `NUGET_ENABLE_ENHANCED_HTTP_RETRY` | Transient NuGet HTTP retry toggle | Set to `true` (`NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT=6`, `NUGET_RETRY_HTTP_429=true`, `NUGET_OBSERVE_RETRY_AFTER=true`) on pre-production runners |
| `PROJECT_TOKEN` / `GH_PROJECT_TOKEN` | GitHub Project V2 GraphQL auto-sync secret | `project`-scoped token used by [`.github/workflows/project-sync.yml`](../../.github/workflows/project-sync.yml) & [`scripts/setup-github-project.sh`](../../scripts/setup-github-project.sh); redacted in `stderr` logs |

See also: [providers.md](providers.md) · [logging-and-secrets.md](logging-and-secrets.md) · [build-from-source.md](../getting-started/build-from-source.md) · [github-project.md](../operations/github-project.md)
