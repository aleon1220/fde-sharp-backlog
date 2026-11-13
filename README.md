# fde-sharp-backlog (`SmartBacklogAssistant`)

.NET 10 CLI & TUI assistant for transforming unstructured engineering discovery notes into standardized Jira backlog items using an **AI-First SDLC** workflow.

## Executive Summary

**Smart Backlog Assistant** (`fde-sharp-backlog`) combines deterministic .NET 10 ingestion, schema validation, and cross-platform Windows/WSL workspace management with Google Gemini (`generateContent`) reasoning to eliminate backlog grooming overhead. Designed around ergonomic terminal standards inspired by **Gemini CLI (`agy`)**, **Astral `uv`**, and **`kubectl`**, it supports both a zero-config interactive colored TUI and composable single-shot CLI subcommands (`run`, `draft`, `models`, `doctor`).

* **GitHub Repository & Project**: [aleon1220/fde-sharp-backlog](https://github.com/aleon1220/fde-sharp-backlog) · [Project Backlog & Issues](https://github.com/aleon1220/fde-sharp-backlog/issues)
* **Active Release Milestone**: [Milestone #1 — `release-fde-version`](https://github.com/aleon1220/fde-sharp-backlog/milestone/1)
* **CLI & TUI Ergonomics Track**: [Issue #3 — Evaluate Contemporary CLI and TUI Tools & Implement Ergonomic Standards](https://github.com/aleon1220/fde-sharp-backlog/issues/3) · [Issue #5 — CLI in the style of `agy`, `uv`, and `kubectl`](https://github.com/aleon1220/fde-sharp-backlog/issues/5)

## Ergonomic CLI Subcommands (`agy` / `uv` / `kubectl` Style)

```bash
# Launch interactive colored TUI (default when run with no arguments)
dotnet run --project src/SmartBacklogAssistant

# Quick-draft through Gemini (set GEMINI_API_KEY first)
dotnet run --project src/SmartBacklogAssistant -- draft "Implement OAuth2 PKCE Login" -t 01 --non-interactive

# Process discovery notes offline with dry-run preview or combined (Type01 + Type02) output
dotnet run --project src/SmartBacklogAssistant -- run -i samples/raw-notes.txt -t both --dry-run --offline --non-interactive

# Pre-flight environment, workspace, & Gemini API key readiness check
dotnet run --project src/SmartBacklogAssistant -- doctor
```

## Workflow Overview

1. **Runtime Telemetry**: Displays active OS, build info (`v1.1.0-gemini-runtime`), .NET runtime, and Gemini live/offline mode. In live mode the model is selected from the models available to the configured API key.
2. **Gemini Secret Injection**: Resolves the API key. Obtained from [Google AI studio](https://aistudio.google.com/api-keys) in order:
   * CLI flag: `--api-key <key>`
   * OS environment variable: `GEMINI_API_KEY` (or `GOOGLE_API_KEY` / `AI_API_KEY`)
   * Interactive CLI prompt: Requests the key before proceeding if none is registered.
3. **Interactive Colored CLI Menu & Workspace Compatibility (`Windows` & `WSL`)**:
   * **Windows Default Workspace**: `c:\workspace\smart-backlog-assistant\in` -> `c:\workspace\smart-backlog-assistant\out`
   * **WSL Compatible Workspace**: `/mnt/c/workspace/smart-backlog-assistant/in` -> `/mnt/c/workspace/smart-backlog-assistant/out`
   * **Menu `[1]` Process Input Files**: Reads discovery files from `smart-backlog-assistant/in` (all files, single file, or dry-run preview) and outputs formatted Jira items to `smart-backlog-assistant/out`.
   * **Menu `[2]` Draft Backlog Item**: Enter a Goal or Title and select **`Type 01`** (`complete` — Given/When/Then), **`Type 02`** (`schedule` — Title + Description), or **Combined Output (Both)** ready to be added into `smart-backlog-assistant/out`.
   * **Menu `[3]` Workspace & Settings**:
     * `[1]` Toggle **JSON Export** (`.md` only by default, or `.md + .json`).
     * `[2]` **Inspect Directories** (`in` and `out` file listings & byte sizes).
     * `[3]` **Runtime & AI Diagnostics** (OS, Windows/WSL path mappings, and Gemini env vars).
     * `[4]` **Select CLI Theme**: Switch between **`agy` style (default)**, **`uv` style**, and **`kubectl` style** in the configuration sub-menu (persisted to `smart-backlog-assistant/settings.json` and configurable via `--theme <agy|uv|kubectl>`).
     * `[5]` Toggle live Gemini API generation (enabled by default) or local offline drafting.
     * `[6]` Fetch and select a Gemini model supported by the configured API key.
4. **Local Processing (`collect` -> `process` -> `validate`)**: Ingests raw discovery notes (`smart-backlog-assistant/in` or `samples/raw-notes.txt`), renders the Type01 or Type02 prompt, and validates the structured draft.
5. **Review & Jira Output (`Type01` | `Type02`)**: Formats `Type01` (`complete` — Given/When/Then) or `Type02` (`schedule` — Title + Description) to console and `smart-backlog-assistant/out` (or `output/`).

## Gemini API Options

Interactive and non-interactive workflows use the same prompt, Gemini model discovery, response validation, output formatting, and file-writing pipeline. Live Gemini generation is enabled by default; the first interactive draft asks for a key if needed, lists models that support `generateContent`, then sends the selected type-specific prompt to Gemini. The goal is passed as a single `Goal:` input rather than being expanded into invented context, requirements, and acceptance criteria. Use `--offline` or the interactive settings toggle to opt into deterministic local drafting without an API call. `prompts/prompt-goal-type01.md` and `prompts/prompt-goal-type02.md` define the response contracts; a custom `--prompt` path overrides the selected template.

```bash
# List models compatible with content generation for this API key
dotnet run --project src/SmartBacklogAssistant -- --list-models --api-key "$GEMINI_API_KEY"

# Generate with a selected model
dotnet run --project src/SmartBacklogAssistant -- \
  --gemini-live \
  --model gemini-3.8-flash \
  --api-key "$GEMINI_API_KEY" \
  --type Type01 \
  --non-interactive
```

For an offline draft that does not require an API key:

```bash
dotnet run --project src/SmartBacklogAssistant -- draft "Implement OAuth2 PKCE Login" -t 01 --offline
```

Model IDs and availability are determined at runtime from the Gemini API, and may vary by API key. See the [AI Studio model catalog](https://aistudio.google.com/docs/models) and [Gemini API model listing reference](https://aistudio.google.com/api-reference/models).

## Quick Start (.NET 10 SDK)

* [get dotnet](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

```powershell
winget install Microsoft.DotNet.SDK.10
```

* 1. Set an OS Environment Variable for live Gemini generation (or choose offline mode)

* Windows

```powershell
# Windows PowerShell
$env:GEMINI_API_KEY = "useYourGeminiAPI_Key"
```

* Linux / macOS / WSL

```bash
export GEMINI_API_KEY="useYourGeminiAPI_Key"
```

* 2. Run Interactively (Launches Colored CLI Menu & Prompts for Key if Unset)

```bash
dotnet run --project src/SmartBacklogAssistant
```

* 3. Quick-Add a Goal or Title (`Type 01` or `Type 02`)

```bash
dotnet run --project src/SmartBacklogAssistant -- \
  --workspace "/mnt/c/workspace" \
  --goal "Implement OAuth2 PKCE Login" \
  --type 01 \
  --api-key "$GEMINI_API_KEY" \
  --non-interactive
```

* 4. Run Non-Interactively

```bash
dotnet run --project src/SmartBacklogAssistant -- \
  --api-key "$GEMINI_API_KEY" \
  --type Type01 \
  --non-interactive
```

* Run Tests

```bash
dotnet test
```

## Containers & Podman Kubernetes

```bash
podman build -t localhost/smart-backlog-assistant:v1 -f Containerfile .
podman kube play k8s/smart-backlog-pod.yaml
podman logs smart-backlog-assistant-assistant
podman kube down k8s/smart-backlog-pod.yaml
```

## Findings & Iteration Backlog

See [BACKLOG.md](BACKLOG.md) for runtime findings and prioritized iteration items (`SBA-101`–`SBA-105`).
