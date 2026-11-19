# fde-sharp-backlog (`SmartBacklogAssistant`)

.NET 10 hybrid CLI & interactive REPL/TUI assistant for transforming unstructured engineering discovery notes into standardized Jira backlog items using an **AI-First SDLC** workflow.

## Executive Summary

**Smart Backlog Assistant** (`fde-sharp-backlog`) combines deterministic .NET 10 ingestion, schema validation, and cross-platform Windows/WSL workspace management with a multi-provider AI orchestrator (**Google Gemini REST** and **`Microsoft.Copilot.SDK` v2.0.1** supporting native FFI in-process, spawned stdio, attached TCP, and BYOK providers).

1. **Astral `uv`-Style Headless Subcommands:** Verb-driven commands (`process`, `draft`, `preview`, `status`, `config`, `models`) with step timers, progress spinners, actionable `error:` / `hint:` diagnostics, and per-command `--help`.
2. **Gemini CLI / Agy Interactive REPL:** Zero-arg execution opens a boxed workspace bar (`In: ./in | Out: ./out | Export: .md`), theme-matched option highlights (`↑`/`↓` + `Enter` or `[0-4]`), quick shortcuts (`[w/g/d/p/v/s/c/?/q]`), `/slash` commands (`/wizard`, `/generate`, `/preview`, `/provider`, `/status`), and `@filename` mentions.

* **GitHub Repository & Backlog**: [aleon1220/fde-sharp-backlog](https://github.com/aleon1220/fde-sharp-backlog) · [Issues](https://github.com/aleon1220/fde-sharp-backlog/issues) · [Milestone #1 (`release-fde-version`)](https://github.com/aleon1220/fde-sharp-backlog/milestone/1)

## Setup & Quick Start (`Microsoft.Copilot.SDK`)

### 1. Prerequisites

* **.NET 10 SDK** (`dotnet --version` >= `10.0`)
* **Build & Verify Locally** (runs all 261 unit & integration tests with zero warnings):

```bash
dotnet test
```

* **AI Provider Credentials** (pick one or use `--offline` for deterministic drafting):
  * **Copilot SDK (`copilot-sdk`):** GitHub Copilot CLI (`copilot` on `PATH` or `COPILOT_CLI_PATH`), a remote runtime (`COPILOT_CLI_URL`), or bundled runtime (`-p:CopilotSkipCliDownload=false`) + authentication (`copilot login`, `COPILOT_GITHUB_TOKEN`, `GITHUB_TOKEN`, or `GH_TOKEN`).
  * **Gemini (`gemini`, default):** `GEMINI_API_KEY` (or `--api-key` from [Google AI Studio](https://aistudio.google.com/api-keys)).
  * **BYOK (`azure` / `claude` / Ollama):** `AZURE_OPENAI_ENDPOINT` + `AZURE_OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, or `OLLAMA_HOST` routed via `Microsoft.Copilot.SDK`.

### 2. Authentication & Runtime Environment Variables

| Variable | Purpose | Notes |
| --- | --- | --- |
| `COPILOT_GITHUB_TOKEN` / `GITHUB_TOKEN` / `GH_TOKEN` | GitHub auth for Copilot SDK | Evaluated in listed order; falls back to `copilot login` keychain auth when unset |
| `COPILOT_CLI_PATH` | Explicit Copilot CLI / runtime path | Uses in-process FFI when native runtime (`libcopilot_runtime.*` / `runtime.node`) is adjacent; otherwise spawns `--stdio` |
| `COPILOT_CLI_URL` | Attached remote runtime over TCP | Accepts `host:port`, `http://host:port`, or `[::1]:port` (`RuntimeConnection.ForTcp`) |
| `COPILOT_SDK_DEFAULT_CONNECTION` | Default local transport | `inprocess` (SDK v2 default) or `stdio` |
| `COPILOT_MODEL` / `GEMINI_MODEL` | Default model ID override | Defaults: `gpt-5-mini` (`copilot-sdk`), `gemini-3.8-flash` (`gemini`) |
| `GEMINI_API_KEY` | Google Gemini REST API key | Can also be persisted interactively or overridden via `--api-key` |

> [!TIP]
> Builds skip downloading the native Copilot runtime bundle by default (`CopilotSkipCliDownload=true`). Pass `-p:CopilotSkipCliDownload=false` when building/publishing if you want MSBuild to bundle the platform FFI native library (`runtimes/<rid>/native/`) for zero-external-CLI in-process execution.

### 3. Provider Selection & Verification Commands

```bash
# Select Copilot SDK for a single command or persist it as your default provider
dotnet run --project src/SmartBacklogAssistant -- config --provider copilot-sdk

# 1. Verify paths, token detection, SDK connection mode, and AI Provider Matrix
dotnet run --project src/SmartBacklogAssistant -- status --provider copilot-sdk

# 2. List available Copilot SDK models from the active runtime
dotnet run --project src/SmartBacklogAssistant -- models --provider copilot-sdk

# 3. Preview a draft with Copilot SDK (add --offline to test locally without API calls)
dotnet run --project src/SmartBacklogAssistant -- draft "Implement OAuth2 PKCE Login" --type 01 --provider copilot-sdk --preview
dotnet run --project src/SmartBacklogAssistant -- draft "Implement OAuth2 PKCE Login" --type 01 --provider copilot-sdk --preview --offline
```

## CLI Subcommands (`uv` Style — Headless Execution)

```bash
# Launch interactive REPL & Wizard TUI (default with zero arguments)
dotnet run --project src/SmartBacklogAssistant

# Process all queued files in ./in or a single file (dry-run preview + optional JSON export)
dotnet run --project src/SmartBacklogAssistant -- process all --dry-run --offline
dotnet run --project src/SmartBacklogAssistant -- process raw-notes.txt -t 01 --provider copilot-sdk --json

# Quick-draft Type 01 (User Story + GWT), Type 02 (Schedule Item), or both
dotnet run --project src/SmartBacklogAssistant -- draft "Implement OAuth2 PKCE Login" --type 01 --preview
dotnet run --project src/SmartBacklogAssistant -- draft "Check Geocaching 2026 hiding movie theme" -t 02 -d "Include rules and dates" --search
dotnet run --project src/SmartBacklogAssistant -- draft "Add Redis Distributed Cache" --type both --json

# Persist workspace settings in smart-backlog-assistant/settings.json
dotnet run --project src/SmartBacklogAssistant -- config --json on --in ./in --out ./out --theme uv --provider copilot-sdk --verbosity bal

# Preview & interactively select saved outputs or copy clean plain text / Jira fields
dotnet run --project src/SmartBacklogAssistant -- preview --select
dotnet run --project src/SmartBacklogAssistant -- preview latest --copy simple
dotnet run --project src/SmartBacklogAssistant -- preview latest --copy clean
dotnet run --project src/SmartBacklogAssistant -- preview latest --copy title

# Workspace paths, active provider readiness, and diagnostics
dotnet run --project src/SmartBacklogAssistant -- status
```

Aliases preserved: `run` (`process`), `add` (`draft`), `doctor` (`status`), `view` (`preview`), `menu`/`tui`. Pass `<command> --help` (e.g. `draft --help`, `status --help`) for command-specific options.

## Interactive REPL & Menu (Gemini CLI / Agy Style)

Running with zero arguments displays the compact boxed workspace header and supports theme-matched highlight selection (`↑`/`↓` + `Enter`), numeric options (`[0-4]`), single-key shortcuts (`[w/g/d/p/v/s/c/?/q]`), `/slash` commands, and `@filename` mentions from `backlog > `:

* **`[1]` / `w` Backlog Assisted Orchestrator:** Unified end-to-end hub (`orchestrator > `, `/wizard`, `/orchestrator`, `/bao`) for drafting from Goals/Titles, processing `./in` files, previewing workspace artifacts, copying formats (`simple`, `clean`, `raw`, `md`, `title`, `story`, `desc`, `ac`, `alt1..alt3`), settings, and diagnostics.
* **`[2]` / `g` Create / Generate Backlog:** Combines creation entry points into a single source selector (`/generate`): `[1]` Draft from Goal / Title (`Type01`, `Type02`, `Both`; `Minimalistic <=25w`, `Balanced <=57w`, `Detailed <=95w`, or `[d]` direct), `[2]` Process Input Files (`./in` batch, single review, or dry run, or `[p]` direct), and `[3]` Dry Run / Scan Workspace.
* **`[3]` / `v` Preview:** General workspace previewer across active in-memory session drafts (`[session]`), saved Markdown outcomes (`./out/*.md`), JSON exports (`./out/*.json`), and raw input notes (`./in/*.txt`, `./in/*.md`) with fuzzy filtering, native/ANSI **OSC 52** clipboard copy, and `[E]` Orchestrator refinement/processing actions.
* **`[4]` / `s` Workspace & Settings:** Colourfully highlights the active/selected option (`↑`/`↓` + `Enter` or `[0-8]`, matched to `agy` Cyan, `uv` Green, or `kubectl` DarkCyan themes) and setting badges (`provider • model`, `ON`/`OFF`, verbosity, theme): `[1]` **Select AI Provider** (with curated recommendations and a dedicated **AI Model Selection** submenu that flags unsupported/retired/wrong-modality models with reasons, recommendations, and links to official provider docs), `[2]` Toggle Live AI API, `[3]` Default Outcome Verbosity, `[4]` Toggle JSON Export, `[5]` Select CLI Theme (`agy`, `uv`, `kubectl`), `[6]` Inspect Directories, `[7]` Runtime & AI Diagnostics, `[8]` View `settings.json`, `[0]` Back.
* **`/slash` Commands & `@filename` Mentions:** `/wizard` (`/orchestrator`, `/bao`), `/generate`, `/process`, `/draft`, `/preview`, `/copy`, `/verbosity`, `/settings`, `/status`, `/config`, `/provider`, `/models`, `/theme`, `/json`, `/live`, `/help`, `/clear`, `/exit`, plus `@raw-notes.txt ...`.

## Multi-Provider AI Orchestration (`Gemini` & `Microsoft.Copilot.SDK`)

Preserves strict **Zero Hallucination** prompt rules and Type `01`/`02` formatting across all `--provider` targets:

1. **`gemini` (Default — Google Gemini REST):** Resolves keys from `--api-key`, `GEMINI_API_KEY`, `GOOGLE_API_KEY`, `AI_API_KEY`, or interactive prompt; supports `--offline` / `/live off`.
2. **`copilot-sdk` (`Microsoft.Copilot.SDK` `2.0.1`):** Uses `CopilotClient` (`CopilotClientMode.CodingAgent`) and `CreateSessionOptions` with strict prompt isolation (disabled config discovery, skills, repository hooks, host git, and built-in tools). Supports in-process FFI, spawned stdio (`COPILOT_CLI_PATH` or `copilot` on `PATH`), and attached TCP (`COPILOT_CLI_URL`).
3. **`azure` / `claude` (BYOK Live or Deterministic Mock):** Routes through `Microsoft.Copilot.SDK` `SessionInferenceOptions` (`NamedProviderConfig` with CAPI disabled) when `AZURE_OPENAI_ENDPOINT` + `AZURE_OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, or `OLLAMA_HOST` and a Copilot runtime are configured; falls back deterministically otherwise.
4. **`manual`:** Renders the Type `01`/`02` Markdown prompt alongside a deterministic baseline draft.

## Workspace Directory Mapping (Windows & WSL2)

* **Windows Default Root:** `c:\workspace\smart-backlog-assistant\{in,out}`
* **WSL2 Default Root:** `/mnt/c/workspace/smart-backlog-assistant/{in,out}`
* Overrides (`backlog config --in ... --out ...`) persist in `smart-backlog-assistant/settings.json`.

## Containers & Podman Kubernetes

```bash
podman build -t localhost/smart-backlog-assistant:v1 -f Containerfile .
podman kube play k8s/smart-backlog-pod.yaml
podman logs smart-backlog-assistant-assistant
podman kube down k8s/smart-backlog-pod.yaml
```

## Findings & Iteration Backlog

See [BACKLOG.md](BACKLOG.md) and [PLAN.md](PLAN.md) for architecture notes, runtime findings (`SBA-101`–`SBA-105`), and CLI/TUI UX standards.
