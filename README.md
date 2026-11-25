# fde-sharp-backlog (`SmartBacklogAssistant`)

.NET 10 hybrid CLI & interactive REPL/TUI assistant for transforming unstructured engineering discovery notes into standardized Jira backlog items using an **AI-First SDLC** workflow.

## Executive Summary

**Smart Backlog Assistant** (`fde-sharp-backlog`) combines deterministic .NET 10 ingestion, schema validation, and cross-platform Windows/WSL workspace management with a multi-provider AI orchestrator (**Google Gemini REST**, **OpenAI Gateway API** over `/v1/chat/completions` & `/v1/responses`, and **`Microsoft.Copilot.SDK` v2.0.1** supporting native FFI in-process, spawned stdio, attached TCP, and BYOK providers).

1. **Astral `uv`-Style Headless Subcommands:** Verb-driven commands (`process`, `draft`, `preview`, `status`, `config`, `models`) with step timers, progress spinners, actionable `error:` / `hint:` diagnostics, and per-command `--help`.
2. **Gemini CLI / Agy Interactive REPL:** Zero-arg execution opens a boxed workspace bar (`In: ./in | Out: ./out | Export: .md`), theme-matched option highlights (`↑`/`↓` + `Enter` or `[0-4]`), quick shortcuts (`[w/g/d/p/v/s/c/?/q]`), `/slash` commands (`/wizard`, `/generate`, `/preview`, `/provider`, `/status`), and `@filename` mentions.

* **GitHub Repository & Backlog**: [aleon1220/fde-sharp-backlog](https://github.com/aleon1220/fde-sharp-backlog) · [Releases](https://github.com/aleon1220/fde-sharp-backlog/releases) · [Issues](https://github.com/aleon1220/fde-sharp-backlog/issues) · [Milestone #1 (`release-fde-version`, due Nov 27)](https://github.com/aleon1220/fde-sharp-backlog/milestone/1) · [UAT Gate (#6)](https://github.com/aleon1220/fde-sharp-backlog/issues/6) · [UAT NuGet Restore Hardening (#57)](https://github.com/aleon1220/fde-sharp-backlog/issues/57) · [GitHub Binary Releases (#59)](https://github.com/aleon1220/fde-sharp-backlog/issues/59)

## Install & Run from Tested GitHub Binary

Pre-built, self-contained single-file binaries are published by [`.github/workflows/release-binaries.yml`](.github/workflows/release-binaries.yml) for `linux-x64`, `win-x64`, and `osx-arm64` **only after** all hardened NuGet restores, Release `-warnaserror` builds, unit/integration tests, and out-of-repo standalone binary smoke checks pass green. Built-in `prompts/*.md` templates are embedded inside the executable, so no `.NET SDK` or git checkout is required.

### 1. Verify Green CI Checks & Latest Release

```bash
# Confirm latest published releases and check that the release workflow passed green
gh release list --repo aleon1220/fde-sharp-backlog --limit 5
gh run list --repo aleon1220/fde-sharp-backlog --workflow release-binaries.yml --limit 5
```

Or open the repository **Releases** and **Actions** tabs on GitHub and confirm the matching tag run (`vX.Y.Z`) displays a green checkmark before downloading.

### 2. Download, Verify SHA-256 Checksum & Install

#### Linux / WSL2 (`linux-x64`) & macOS (`osx-arm64`)

```bash
# 1. Download latest green-checked release archive + checksums (set RID=osx-arm64 on Apple Silicon)
RID="linux-x64"   # or "osx-arm64"
TAG="$(gh release view --repo aleon1220/fde-sharp-backlog --json tagName --jq .tagName)"
mkdir -p /tmp/sba-install && cd /tmp/sba-install
gh release download "$TAG" --repo aleon1220/fde-sharp-backlog \
  --pattern "SmartBacklogAssistant-${TAG}-${RID}.tar.gz" \
  --pattern "SHA256SUMS.txt"

# Alternative without gh CLI (curl):
# curl -fsSLO "https://github.com/aleon1220/fde-sharp-backlog/releases/download/${TAG}/SmartBacklogAssistant-${TAG}-${RID}.tar.gz"
# curl -fsSLO "https://github.com/aleon1220/fde-sharp-backlog/releases/download/${TAG}/SHA256SUMS.txt"

# 2. Verify SHA-256 checksum (use 'shasum -a 256 -c' on macOS) and optional SLSA provenance
grep "SmartBacklogAssistant-${TAG}-${RID}.tar.gz" SHA256SUMS.txt | sha256sum -c
gh attestation verify "SmartBacklogAssistant-${TAG}-${RID}.tar.gz" --repo aleon1220/fde-sharp-backlog

# 3. Extract and install onto PATH (exposes both 'SmartBacklogAssistant' and 'backlog')
tar -xzf "SmartBacklogAssistant-${TAG}-${RID}.tar.gz"
mkdir -p "$HOME/.local/bin"
install -m 0755 "SmartBacklogAssistant-${TAG}-${RID}/SmartBacklogAssistant" "$HOME/.local/bin/SmartBacklogAssistant"
ln -sf "$HOME/.local/bin/SmartBacklogAssistant" "$HOME/.local/bin/backlog"
export PATH="$HOME/.local/bin:$PATH"   # persist in ~/.bashrc or ~/.zshrc if not already on PATH
```

#### Windows PowerShell (`win-x64`)

```powershell
# 1. Download latest green-checked Windows release archive + SHA256SUMS.txt
$Tag = gh release view --repo aleon1220/fde-sharp-backlog --json tagName --jq .tagName
$Asset = "SmartBacklogAssistant-$Tag-win-x64.zip"
$InstallDir = Join-Path $env:LOCALAPPDATA "Programs\SmartBacklogAssistant"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

# Using gh CLI (or Invoke-RestMethod / irm from github.com/.../releases/download/$Tag/$Asset):
gh release download $Tag --repo aleon1220/fde-sharp-backlog --dir $env:TEMP --pattern $Asset --pattern "SHA256SUMS.txt"

# Alternative without gh CLI (Invoke-RestMethod):
# $Latest = Invoke-RestMethod "https://api.github.com/repos/aleon1220/fde-sharp-backlog/releases/latest"
# $Tag = $Latest.tag_name; $Asset = "SmartBacklogAssistant-$Tag-win-x64.zip"
# Invoke-RestMethod "https://github.com/aleon1220/fde-sharp-backlog/releases/download/$Tag/$Asset" -OutFile (Join-Path $env:TEMP $Asset)
# Invoke-RestMethod "https://github.com/aleon1220/fde-sharp-backlog/releases/download/$Tag/SHA256SUMS.txt" -OutFile (Join-Path $env:TEMP "SHA256SUMS.txt")

# 2. Verify SHA-256 checksum
$Expected = (Select-String -Path (Join-Path $env:TEMP "SHA256SUMS.txt") -Pattern ([regex]::Escape($Asset))).Line.Split()[0].ToLowerInvariant()
$Actual = (Get-FileHash (Join-Path $env:TEMP $Asset) -Algorithm SHA256).Hash.ToLowerInvariant()
if ($Actual -ne $Expected) { throw "SHA-256 mismatch for $Asset" }

# 3. Extract, add to User PATH, and expose 'backlog' convenience copy
Expand-Archive (Join-Path $env:TEMP $Asset) -DestinationPath $InstallDir -Force
$BinDir = Join-Path $InstallDir "SmartBacklogAssistant-$Tag-win-x64"
Copy-Item (Join-Path $BinDir "SmartBacklogAssistant.exe") (Join-Path $BinDir "backlog.exe") -Force
$UserPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($UserPath -notlike "*$BinDir*") {
    [Environment]::SetEnvironmentVariable("Path", "$UserPath;$BinDir", "User")
    $env:Path = "$env:Path;$BinDir"
}
```

### 3. Run Standalone & Upgrade Cleanly

```bash
# Run from any standalone directory outside a git checkout
cd ~
backlog --version
backlog --help
backlog status
backlog draft "Add API Rate Limiting" --type 01 --preview --offline --non-interactive

# Launch interactive REPL / Wizard TUI
backlog
```

* **Upgrading:** Re-run the download + checksum + extract/overwrite steps above for a newer tag `vX.Y.Z`; workspace `settings.json`, `./in`, and `./out` remain preserved in your workspace root.
* **Publishing a Release (Maintainers):** Push an annotated semver tag (`git tag -a v1.1.0 -m "Release v1.1.0" && git push origin v1.1.0`) or run `workflow_dispatch` on [`.github/workflows/release-binaries.yml`](.github/workflows/release-binaries.yml) (`./scripts/package-release.sh` / `.\scripts\package-release.ps1` locally for dry-run packaging).

## Setup & Quick Start (Build from Source — `Microsoft.Copilot.SDK` & OpenAI Gateway)

### 1. Prerequisites & Hardened NuGet Restore (UAT & Pre-Production Runners)

* **.NET 10 SDK** (`dotnet --version` >= `10.0`)
* **Isolated Sources & Lockfile Determinism ([Issue #57](https://github.com/aleon1220/fde-sharp-backlog/issues/57)):**
  * Root [`nuget.config`](nuget.config) uses `<clear />` inside `<packageSources>` and `<disabledPackageSources>` while pinning `https://api.nuget.org/v3/index.json` (`protocolVersion="3"`), isolating restores from stale or disabled global/machine `NuGet.Config` entries.
  * [`Directory.Build.props`](Directory.Build.props) enables `RestorePackagesWithLockFile` (committed [`src/SmartBacklogAssistant/packages.lock.json`](src/SmartBacklogAssistant/packages.lock.json) and [`tests/SmartBacklogAssistant.Tests/packages.lock.json`](tests/SmartBacklogAssistant.Tests/packages.lock.json)) and automatically enforces `RestoreLockedMode=true` when `ContinuousIntegrationBuild=true`.
* **Recommended One-Command Hardened Restore (Linux / macOS / Windows):**

```bash
# Linux / macOS / WSL2 (enables HTTP retries, enforces --locked-mode, optionally repairs user NuGet.Config)
./scripts/restore-nuget.sh --locked-mode --fix-user-config

# Direct dotnet CLI equivalent (or register nuget.org globally for other repos)
dotnet nuget add source https://api.nuget.org/v3/index.json --name nuget.org
NUGET_ENABLE_ENHANCED_HTTP_RETRY=true dotnet restore SmartBacklogAssistant.slnx --locked-mode
```

```powershell
# Windows / PowerShell (hardened restore with HTTP retries + --locked-mode)
.\scripts\restore-nuget.ps1 -LockedMode $true -FixUserConfig
```

* **Upstream Caching Proxies, Local Fallback Feeds & Ephemeral Token Auth:**
  * **Caching Proxy & Fallback Feeds:** Set `NUGET_PROXY_FEED_URL` (e.g., GitHub Packages `https://nuget.pkg.github.com/<owner>/index.json` or Azure Artifacts upstream to `nuget.org`; add `NUGET_PROXY_ONLY=true` for proxy-only air-gapped runners), `NUGET_FALLBACK_FEED_DIR` (local `.nupkg` directory), and/or `NUGET_FALLBACK_PACKAGES_DIR` (or repo-local `.nuget/fallback` expanded cache).
  * **Token-Scoped / Workload Identity CI Auth (Zero Static PATs):** Supply short-lived credentials via `NUGET_AUTH_TOKEN`, `VSS_NUGET_ACCESSTOKEN`, or `GITHUB_TOKEN` (for GitHub Packages) — [`scripts/restore-nuget.sh`](scripts/restore-nuget.sh) and [`scripts/restore-nuget.ps1`](scripts/restore-nuget.ps1) pass credentials in memory via `NuGetPackageSourceCredentials_upstream_proxy` without persisting secrets to disk, and [`SecretRedactor`](src/SmartBacklogAssistant/Services/SecretRedactor.cs) redacts `NUGET_AUTH_TOKEN` / `VSS_NUGET_ACCESSTOKEN` from CLI `stderr` diagnostics.
  * **Pre-Production CI Gate:** [`.github/workflows/preprod-nuget-uat.yml`](.github/workflows/preprod-nuget-uat.yml) verifies lockfile-cached restores on `ubuntu-latest` and `windows-latest`, simulates poisoned user `NuGet.Config` drift, enables `id-token: write` for OIDC workload identity, and runs UAT smoke checks.

> [!NOTE]
> **Troubleshooting Non-Clean Setups & Lockfiles During UAT ([Issue #57](https://github.com/aleon1220/fde-sharp-backlog/issues/57)):**
> * If `dotnet nuget add source` reports that `nuget.org` already exists or is disabled in your user profile, inspect and re-enable it (or run `--fix-user-config` / `-FixUserConfig`):
>   ```bash
>   dotnet nuget list source
>   dotnet nuget enable source nuget.org
>   ```
> * If a stale HTTP cache or corrupted package folder blocks restore, run `./scripts/restore-nuget.sh --clear-cache` (or `.\scripts\restore-nuget.ps1 -ClearCache`), or manually clear and force-restore:
>   ```bash
>   dotnet nuget locals http-cache --clear
>   NUGET_ENABLE_ENHANCED_HTTP_RETRY=true dotnet restore --configfile nuget.config --locked-mode --force --no-cache
>   ```
> * When intentionally adding or upgrading a NuGet package, regenerate `packages.lock.json` cleanly without `--locked-mode` (`dotnet restore --force-evaluate`) and commit the updated lockfiles.

* **Build & Verify Locally for UAT** (runs all unit & integration tests with zero warnings, then checks live workspace readiness):

```bash
dotnet test --no-restore
dotnet run --project src/SmartBacklogAssistant -- status
```

* **AI Provider Credentials** (pick one or use `--offline` for deterministic drafting):
  * **Gemini (`gemini`, default):** `GEMINI_API_KEY` (or `--api-key` from [Google AI Studio](https://aistudio.google.com/api-keys)).
  * **OpenAI Gateway API (`openai`):** `OPENAI_API_KEY` (or `--api-key`) plus optional `OPENAI_BASE_URL` (`--gateway-url`, defaults to `https://api.openai.com/v1`), `OPENAI_WIRE_API` (`chat` or `responses`), and `OPENAI_MODEL` (defaults to `gpt-4o`). Supports OpenAI, Azure OpenAI/Foundry, LiteLLM, OpenRouter, vLLM, and Ollama `/v1` gateways.
  * **Copilot SDK (`copilot-sdk`):** GitHub Copilot CLI (`copilot` on `PATH` or `COPILOT_CLI_PATH`), a remote runtime (`COPILOT_CLI_URL`), or bundled runtime (`-p:CopilotSkipCliDownload=false`) + authentication (`copilot login`, `COPILOT_GITHUB_TOKEN`, `GITHUB_TOKEN`, or `GH_TOKEN`).
  * **Azure / BYOK (`azure` / `claude` / Ollama):** `AZURE_OPENAI_ENDPOINT` + `AZURE_OPENAI_API_KEY` (uses direct HTTP gateway fallback even without Copilot CLI), `ANTHROPIC_API_KEY`, or `OLLAMA_HOST` routed via `Microsoft.Copilot.SDK`.

### 2. Authentication, NuGet Restore & Runtime Environment Variables

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
| `GEMINI_API_KEY` | Google Gemini REST API key | Can also be persisted interactively or overridden via `--api-key` |
| `SBA_LOG_LEVEL` | Diagnostic `stderr` log severity | `Trace`, `Debug`, `Information`, `Warning` (default), `Error`, `Critical`, `None` (`Quiet`) |
| `SBA_LOG_FORMAT` | Diagnostic `stderr` log format | `text` (default timestamped) or `json` (structured NDJSON); always routed to `stderr` |
| `NUGET_PROXY_FEED_URL` | Upstream NuGet caching proxy URL | HTTPS feed URL (GitHub Packages, Azure Artifacts, Artifactory); set `NUGET_PROXY_ONLY=true` to disable direct `nuget.org` fallback |
| `NUGET_AUTH_TOKEN` / `VSS_NUGET_ACCESSTOKEN` | Ephemeral token-scoped NuGet feed auth | Supplied by CI OIDC / workload identity (`NUGET_AUTH_USER` defaults to `x-access-token` on GitHub Packages or `azdo`); redacted in `stderr` logs |
| `NUGET_FALLBACK_FEED_DIR` / `NUGET_FALLBACK_PACKAGES_DIR` | Local NuGet fallback feed or cache dir | Auto-detected by [`Directory.Build.props`](Directory.Build.props) and restore scripts only when directory exists on disk (also auto-uses `.nuget/fallback`) |
| `NUGET_ENABLE_ENHANCED_HTTP_RETRY` | Transient NuGet HTTP retry toggle | Set to `true` (`NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT=6`, `NUGET_RETRY_HTTP_429=true`, `NUGET_OBSERVE_RETRY_AFTER=true`) on pre-production runners |

> [!TIP]
> Builds skip downloading the native Copilot runtime bundle by default (`CopilotSkipCliDownload=true`). Pass `-p:CopilotSkipCliDownload=false` when building/publishing if you want MSBuild to bundle the platform FFI native library (`runtimes/<rid>/native/`) for zero-external-CLI in-process execution.

### 3. Provider Selection & Verification Commands

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

## CLI Subcommands (`uv` Style — Headless Execution)

```bash
# Launch interactive REPL & Wizard TUI (default with zero arguments)
dotnet run --project src/SmartBacklogAssistant

# Process all queued files in ./in or a single file (dry-run preview + optional JSON export)
dotnet run --project src/SmartBacklogAssistant -- process all --dry-run --offline
dotnet run --project src/SmartBacklogAssistant -- process raw-notes.txt -t 01 --provider copilot-sdk --json
dotnet run --project src/SmartBacklogAssistant -- process raw-notes.txt -t 01 --provider openai --gateway-url https://api.openai.com/v1 --wire-api chat

# Quick-draft Type 01 (User Story + GWT), Type 02 (Schedule Item), or both
dotnet run --project src/SmartBacklogAssistant -- draft "Implement OAuth2 PKCE Login" --type 01 --preview
dotnet run --project src/SmartBacklogAssistant -- draft "Implement OAuth2 PKCE Login" --type 01 --provider openai --gateway-url https://api.openai.com/v1 --wire-api responses --preview
dotnet run --project src/SmartBacklogAssistant -- draft "Check Geocaching 2026 hiding movie theme" -t 02 -d "Include rules and dates" --search
dotnet run --project src/SmartBacklogAssistant -- draft "Add Redis Distributed Cache" --type both --json

# Persist workspace settings in smart-backlog-assistant/settings.json
dotnet run --project src/SmartBacklogAssistant -- config --json on --in ./in --out ./out --theme uv --provider copilot-sdk --verbosity bal --log-level info --log-format text
dotnet run --project src/SmartBacklogAssistant -- config --provider openai --gateway-url https://proxy.internal/v1 --wire-api responses -m gpt-4.1-mini

# Control diagnostic stderr logging (keeps stdout clean for pipes; API keys & tokens always masked)
dotnet run --project src/SmartBacklogAssistant -- draft "Audit OAuth2 Flows" --preview --offline --log-level debug
dotnet run --project src/SmartBacklogAssistant -- preview --select --log-level trace --log-format json
SBA_LOG_LEVEL=debug SBA_LOG_FORMAT=json dotnet run --project src/SmartBacklogAssistant -- status

# Preview & interactively select saved outputs or copy clean plain text / Jira fields
dotnet run --project src/SmartBacklogAssistant -- preview --select
dotnet run --project src/SmartBacklogAssistant -- preview latest --copy simple
dotnet run --project src/SmartBacklogAssistant -- preview latest --copy clean
dotnet run --project src/SmartBacklogAssistant -- preview latest --copy title

# Workspace paths, active provider readiness, and diagnostics
dotnet run --project src/SmartBacklogAssistant -- status
```

Aliases preserved: `run` (`process`), `add` (`draft`), `doctor` (`status`), `view` (`preview`), `menu`/`tui`. Pass `<command> --help` (e.g. `draft --help`, `status --help`) for command-specific options.

### Configurable Logging (`stderr` & Secret Redaction)

Diagnostic telemetry uses standard `Microsoft.Extensions.Logging` (`LogLevel`: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`/`Quiet`), routed exclusively to `stderr` so `stdout` artifacts and piped selections (`preview --select`) stay uncorrupted:

* **Precedence:** `--log-level` → `--verbose`/`-V` (`Debug`) or `--quiet`/`-q` (`None`) → `SBA_LOG_LEVEL` → `settings.json` (`logLevel`) → `Warning` (default).
* **Formatting:** Timestamped `text` (default) or structured NDJSON (`--log-format json` / `SBA_LOG_FORMAT=json` / `settings.json`).
* **Secret Redaction:** Gemini keys (`AIza...`), OpenAI Gateway keys (`OPENAI_API_KEY` / `sk-...`), GitHub/Copilot tokens (`ghp_`, `gho_`, `github_pat_`), NuGet feed tokens (`NUGET_AUTH_TOKEN`, `VSS_NUGET_ACCESSTOKEN`), Anthropic/Azure/BYOK keys, and auth headers/passwords are masked via `GeminiSecretProvider.MaskKey` before writing to `stderr`.

### Interactive Secret Entry & Keystroke Masking

When API credentials (Google Gemini, OpenAI Gateway, Azure/BYOK) are prompted interactively in the CLI:
* **Keystroke Masking:** Typed characters are intercepted via `Console.ReadKey(intercept: true)` on interactive TTY sessions and rendered as `*`, protecting secrets against shoulder-surfing, screen-sharing, or recorded terminal sessions.
* **Terminal Editing:** Supports `Backspace` (`\b \b`) and `Escape` (clears buffer) during secret entry, while filtering non-printable control keys.
* **Redirected & Test Fallback:** Seamlessly falls back to `TextReader.ReadLine()` when standard input is redirected, piped, or invoked in automated tests.
* **Zero-Leak Confirmation:** Displays only `GeminiSecretProvider.MaskKey` (`****<last4>`) confirmation upon registration; plaintext credentials never appear in console output or persisted files.

## Interactive REPL & Menu (Gemini CLI / Agy Style)

Running with zero arguments displays the compact boxed workspace header and supports theme-matched highlight selection (`↑`/`↓` + `Enter`), numeric options (`[0-4]`), single-key shortcuts (`[w/g/d/p/v/s/c/?/q]`), `/slash` commands, and `@filename` mentions from `backlog > `:

* **`[1]` / `w` Backlog Assisted Orchestrator:** Unified end-to-end hub (`orchestrator > `, `/wizard`, `/orchestrator`, `/bao`) for drafting from Goals/Titles, processing `./in` files, previewing workspace artifacts, copying formats (`simple`, `clean`, `raw`, `md`, `title`, `story`, `desc`, `ac`, `alt1..alt3`), settings, and diagnostics.
* **`[2]` / `g` Create / Generate Backlog:** Combines creation entry points into a single source selector (`/generate`): `[1]` Draft from Goal / Title (`Type01`, `Type02`, `Both`; `Minimalistic <=25w`, `Balanced <=57w`, `Detailed <=95w`, or `[d]` direct), `[2]` Process Input Files (`./in` batch, single review, or dry run, or `[p]` direct), and `[3]` Dry Run / Scan Workspace.
* **`[3]` / `v` Preview:** General workspace previewer across active in-memory session drafts (`[session]`), saved Markdown outcomes (`./out/*.md`), JSON exports (`./out/*.json`), and raw input notes (`./in/*.txt`, `./in/*.md`) with fuzzy filtering, native/ANSI **OSC 52** clipboard copy, and `[E]` Orchestrator refinement/processing actions.
* **`[4]` / `s` Workspace & Settings:** Colourfully highlights the active/selected option (`↑`/`↓` + `Enter` or `[0-9]`, matched to `agy` Cyan, `uv` Green, or `kubectl` DarkCyan themes) and setting badges (`provider • model`, `ON`/`OFF`, verbosity, theme, log level): `[1]` **Select AI Provider** (`gemini`, `copilot-sdk`, `openai`, `azure`, `claude`, `manual`, `[M]` **AI Model Selection**, and `[G]` interactive OpenAI Gateway URL & `chat`/`responses` configuration), `[2]` Toggle Live AI API, `[3]` Default Outcome Verbosity, `[4]` Toggle JSON Export, `[5]` Select CLI Theme (`agy`, `uv`, `kubectl`), `[6]` Inspect Directories, `[7]` Runtime & AI Diagnostics, `[8]` View `settings.json`, `[9]` Select Log Level (`Trace`–`None` + `text`/`json` `stderr` format), `[0]` Back.
* **`/slash` Commands & `@filename` Mentions:** `/wizard` (`/orchestrator`, `/bao`), `/generate`, `/process`, `/draft`, `/preview`, `/copy`, `/verbosity`, `/log-level`, `/log-format`, `/verbose`, `/settings`, `/status`, `/config` (including `--gateway-url` & `--wire-api`), `/provider`, `/models`, `/theme`, `/json`, `/live`, `/help`, `/clear`, `/exit`, plus `@raw-notes.txt ...`.

## Multi-Provider AI Orchestration (`Gemini` & `Microsoft.Copilot.SDK`)

Preserves strict **Zero Hallucination** prompt rules and Type `01`/`02` formatting across all `--provider` targets:

1. **`gemini` (Default — Google Gemini REST):** Resolves keys from `--api-key`, `GEMINI_API_KEY`, `GOOGLE_API_KEY`, `AI_API_KEY`, or masked interactive prompt (`*` keystrokes via `ReadKey`); supports `--offline` / `/live off`.
2. **`copilot-sdk` (`Microsoft.Copilot.SDK` `2.0.1`):** Uses `CopilotClient` (`CopilotClientMode.CodingAgent`) and `CreateSessionOptions` with strict prompt isolation (disabled config discovery, skills, repository hooks, host git, and built-in tools). Supports in-process FFI, spawned stdio (`COPILOT_CLI_PATH` or `copilot` on `PATH`), and attached TCP (`COPILOT_CLI_URL`).
3. **`openai` (OpenAI Gateway API — Direct HTTP):** Native HTTP client ([`OpenAiGatewayClient`](src/SmartBacklogAssistant/Services/OpenAiGatewayClient.cs)) supporting `/v1/chat/completions` (`chat`, default) and `/v1/responses` (`responses`), custom OpenAI-compatible gateway URLs (`OPENAI_BASE_URL`, `--gateway-url`, or `openAiBaseUrl`/`openAiWireApi` in `settings.json`), `/models` catalog discovery (`vendor/model` gateway slugs included), Bearer / Azure `api-key` auth, masked interactive key prompt (`*` keystrokes via `ReadKey`; `Enter` for offline fallback), and deterministic offline fallback (`--offline` or when credentials are unset).
4. **`azure` / `claude` (Direct HTTP / BYOK Live or Deterministic Mock):** When `AZURE_OPENAI_ENDPOINT` + `AZURE_OPENAI_API_KEY` are configured without a Copilot CLI runtime, `azure` falls back to direct OpenAI Gateway HTTP deployment calls; with a Copilot runtime, `azure` / `claude` / Ollama route through `Microsoft.Copilot.SDK` `SessionInferenceOptions` (`NamedProviderConfig` with CAPI disabled), falling back deterministically otherwise.
5. **`manual`:** Renders the Type `01`/`02` Markdown prompt alongside a deterministic baseline draft.

## Workspace Directory Mapping (Windows & WSL2)

* **Windows Default Root:** `c:\workspace\smart-backlog-assistant\{in,out}`
* **WSL2 Default Root:** `/mnt/c/workspace/smart-backlog-assistant/{in,out}`
* Overrides (`backlog config --in ... --out ...`) persist in `smart-backlog-assistant/settings.json`.

## Containers & Podman Kubernetes

```bash
# Deterministic --locked-mode restore & --no-restore publish (optionally pass --build-arg NUGET_PROXY_FEED_URL=...)
podman build -t localhost/smart-backlog-assistant:v1 -f Containerfile .
podman kube play k8s/smart-backlog-pod.yaml
podman logs smart-backlog-assistant-assistant
podman kube down k8s/smart-backlog-pod.yaml
```

## Findings & Iteration Backlog

See [BACKLOG.md](BACKLOG.md) and [PLAN.md](PLAN.md) for architecture notes, runtime findings (`SBA-101`–`SBA-110`), and CLI/TUI UX standards.
