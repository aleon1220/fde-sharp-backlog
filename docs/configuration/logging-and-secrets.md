# Configurable Logging & Secret Hygiene

Diagnostic `stderr` logging, secret redaction rules, and interactive `*`-masked API key management.

## Configurable Logging (`stderr` & Secret Redaction)

Diagnostic telemetry uses standard `Microsoft.Extensions.Logging` (`LogLevel`: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`/`Quiet`), routed exclusively to `stderr` so `stdout` artifacts and piped selections (`preview --select`) stay uncorrupted:

* **Precedence:** `--log-level` → `--verbose`/`-V` (`Debug`) or `--quiet`/`-q` (`None`) → `SBA_LOG_LEVEL` → `settings.json` (`logLevel`) → `Warning` (default).
* **Formatting:** Timestamped `text` (default) or structured NDJSON (`--log-format json` / `SBA_LOG_FORMAT=json` / `settings.json`).
* **Secret Redaction:** Gemini keys (`AIza...`), OpenAI Gateway keys (`OPENAI_API_KEY` / `sk-...`), GitHub/Copilot tokens (`ghp_`, `gho_`, `github_pat_`), NuGet feed tokens (`NUGET_AUTH_TOKEN`, `VSS_NUGET_ACCESSTOKEN`), Anthropic/Azure/BYOK keys, and auth headers/passwords are masked via `GeminiSecretProvider.MaskKey` before writing to `stderr`.

## Interactive Secret Entry, Validation & Keystroke Masking ([Issue #64](https://github.com/aleon1220/fde-sharp-backlog/issues/64))

When API credentials (`gemini`, `openai`, `azure`, `claude`, `copilot-sdk`) are entered interactively via `[K] Manage AI Provider API Keys`, `/keys`, or `backlog keys`:

* **Keystroke Masking:** Typed characters are intercepted via `Console.ReadKey(intercept: true)` on interactive TTY sessions and rendered as `*`, protecting secrets against shoulder-surfing, screen-sharing, or recorded terminal sessions.
* **Terminal Editing & Test Fallback:** Supports `Backspace` (`\b \b`) and `Escape` (clears buffer) during secret entry, while seamlessly falling back to `TextReader.ReadLine()` when standard input is redirected or invoked in automated tests.
* **Offline Syntax & Cross-Provider Validation:** Validates non-empty keys before saving and rejects placeholders (`YOUR_API_KEY`, `****...`), `Bearer `/`ENV_VAR=` prefixes, URLs/quotes, and cross-provider key mismatches (e.g., `AIza...` pasted into `openai` or `sk-ant-...` pasted into `gemini`) with actionable portal/prefix hints.
* **Optional Live Endpoint Verification (`--verify`):** Verifies keys live against Google Gemini, OpenAI Gateway (`/models`), Azure OpenAI / Foundry (`/models`), Anthropic Claude (`/v1/models`), or GitHub Copilot SDK / `api.github.com/user` with `SecretRedactor`-protected error reporting.
* **Session-First Storage & Zero-Leak Confirmation:** Stores keys in `SessionEnvironmentStore` (`session` runtime overlay by default, or optional OS User environment persistence `--scope user` on Windows with POSIX `export` guidance on Linux/macOS), displays only `****<last4>` confirmations, immediately reflects changes in `/status`, `models`, `draft`, and `process`, and NEVER writes plaintext secrets to `settings.json` or `stderr`.

See also: [providers.md](providers.md) · [environment-variables.md](environment-variables.md) · [workspace.md](workspace.md) · [engineering-principles.md](../project/engineering-principles.md)
