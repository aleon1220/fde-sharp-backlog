# fde-sharp-backlog (`SmartBacklogAssistant`)

.NET 10 hybrid CLI & interactive REPL/TUI assistant for transforming unstructured engineering discovery notes into standardized Jira backlog items using an **AI-First SDLC** workflow.

## Executive Summary

**Smart Backlog Assistant** (`fde-sharp-backlog`) combines deterministic .NET 10 ingestion, schema validation, and cross-platform Windows/WSL workspace management with a multi-provider AI orchestrator (**Google Gemini REST** and **`GitHub.Copilot.SDK`** with native Copilot CLI or BYOK providers). Modeled after two gold-standard terminal paradigms:

1. **Astral `uv` (Frictionless One-Shot Subcommand CLI):** Deterministic, verb-driven subcommands (`process`, `draft`, `status`, `config`, `models`) with elapsed step timers (`✓ Resolved ... in 1ms`), non-intrusive Braille progress spinners during async AI calls, actionable error + `hint:` diagnostics, and scannable `--help` output.
2. **Gemini CLI / Agy (Interactive REPL & Branching 3-Option TUI):** Launching with zero arguments opens an interactive session with a boxed status bar (`In: ./in | Out: ./out | Export: .md`), `/slash` commands, `@filename` context mentions, and our streamlined 3-option menu state machine.

* **GitHub Repository & Project**: [aleon1220/fde-sharp-backlog](https://github.com/aleon1220/fde-sharp-backlog) · [Project Backlog & Issues](https://github.com/aleon1220/fde-sharp-backlog/issues)
* **Active Release Milestone**: [Milestone #1 — `release-fde-version`](https://github.com/aleon1220/fde-sharp-backlog/milestone/1)
* **CLI & TUI Ergonomics Track**: [Issue #3](https://github.com/aleon1220/fde-sharp-backlog/issues/3) · [Issue #5](https://github.com/aleon1220/fde-sharp-backlog/issues/5) · [Issue #7](https://github.com/aleon1220/fde-sharp-backlog/issues/7)

## Ergonomic CLI Subcommands (`uv` Style — Headless Execution)

```bash
# Launch interactive Gemini Agy REPL & TUI (default when run with no arguments)
dotnet run --project src/SmartBacklogAssistant

# Process all queued files in ./in or a single file (dry-run preview + optional JSON export)
dotnet run --project src/SmartBacklogAssistant -- process all --dry-run --offline
dotnet run --project src/SmartBacklogAssistant -- process raw-notes.txt -t 01 --json

# Quick-draft a Type 01 (User Story + GWT), Type 02 (Schedule Item), or both without disk writes
dotnet run --project src/SmartBacklogAssistant -- draft "Implement OAuth2 PKCE Login" --type 01 --preview
dotnet run --project src/SmartBacklogAssistant -- draft "Check Geocaching 2026 hiding movie theme" -t 02 -d "Include rules and dates" --search
dotnet run --project src/SmartBacklogAssistant -- draft "Add Redis Distributed Cache" --type both --json

# Non-interactively configure persisted settings (stored in smart-backlog-assistant/settings.json)
dotnet run --project src/SmartBacklogAssistant -- config --json on --in ./in --out ./out --theme uv --provider copilot-sdk

# Crisp diagnostic table: WSL2/Windows paths, ./in count, export format, & AI provider matrix
dotnet run --project src/SmartBacklogAssistant -- status
```

Subcommand aliases preserved for backward compatibility: `run` (`process`), `add` (`draft`), `doctor` (`status`), `menu`/`tui`. Pass `<command> --help` (e.g. `process --help`, `config --help`) for command-specific help.

## Interactive REPL & Menu (Gemini CLI / Agy Style)

Running with zero arguments displays the compact boxed workspace header and supports three interaction styles seamlessly from `backlog > `:

* **Numeric Menu `[0-3]`:**
  * `[1] Process Input Files` — Queue inspection, process all, single file, or dry-run preview.
  * `[2] Draft Backlog Item` — Draft `Type01` (`complete` — Given/When/Then), `Type02` (`schedule` — Title + Description), `Both`, **`[4] Preview Draft (No Files Saved)` sub-menu** (`Type 01`, `Type 02`, or `Both` with interactive typing tips), or `[5] Guided Draft & Refine (Wizard)` with `[1-3]` Alternative selection, optional Google Search grounding, and `[A]ccept / [R]efine / [T]ype / [P]review / [C]ancel`. Bare Goals automatically trigger two-step INVEST Goal Expansion (`prompts/prompt-goal-expand.md`) to enhance the Title, synthesize User Story framing + structured Description, and present **3 Title & Description alternatives**.
  * `[3] Workspace & Settings` — Toggle JSON export, inspect `./in` & `./out`, run diagnostics, select CLI theme (`agy`, `uv`, `kubectl`), toggle Live AI, select model, `[7]` view `settings.json`, and `[8]` select active AI provider.
* **`/slash` Commands:**
  * `/process [all|<file>] [--dry-run] [--json]`, `/draft "<goal>" [-t 01|02|both] [--details "..."] [--search] [--preview]`, `/settings`, `/status`, `/config [--json on|off] [--in <dir>] [--out <dir>]`, `/provider <name>`, `/models`, `/theme <agy|uv|kubectl>`, `/json [on|off]`, `/live [on|off]`, `/help`, `/clear`, `/exit`.
* **`@filename` Context Mentions:**
  * Reference queued `./in` or `./samples` files directly in the REPL: `@raw-notes.txt` (processes file) or `@raw-notes.txt Implement OAuth2 PKCE` (attaches discovery context cleanly without hallucination). Type `@` or `@?` to list available context files.

## Multi-Provider AI Orchestration (`Gemini` & `GitHub.Copilot.SDK`)

Preserves strict **Zero Hallucination** prompt rules and Type `01`/`02` formatting across all providers (`--provider`):

1. **`gemini` (Default — Google Gemini REST):**
   * Resolves API key from `--api-key`, `GEMINI_API_KEY`, `GOOGLE_API_KEY`, `AI_API_KEY`, or interactive CLI prompt ([Google AI Studio](https://aistudio.google.com/api-keys)).
   * Use `--offline` or `/live off` for zero-API deterministic drafting.
2. **`copilot-sdk` (`GitHub.Copilot.SDK`):**
   * Uses the official `.NET` `GitHub.Copilot.SDK` (`CopilotClient` / `CopilotSession`) with `COPILOT_CLI_PATH`, `COPILOT_CLI_URL`, or `copilot` on `PATH` and `GITHUB_TOKEN`/`COPILOT_GITHUB_TOKEN` (or `copilot login`).
   * Build skips downloading the ~300 MB Copilot CLI binary by default (`CopilotSkipCliDownload=true`); pass `-p:CopilotSkipCliDownload=false` at build time if you want MSBuild to bundle it automatically.
3. **`azure` / `claude` (BYOK Live or Deterministic Mock):**
   * Routes live requests through `GitHub.Copilot.SDK` BYOK when `AZURE_OPENAI_ENDPOINT` + `AZURE_OPENAI_API_KEY` (or `OLLAMA_HOST` / `ANTHROPIC_API_KEY`) and Copilot runtime are available; falls back cleanly to deterministic mock mode otherwise.
4. **`manual`:**
   * Renders the selected Type 01/02 Markdown prompt to console alongside a deterministic baseline draft.

## Workspace Directory Mapping (Windows & WSL2)

* **Windows Default Root**: `c:\workspace\smart-backlog-assistant\{in,out}`
* **WSL2 Default Root**: `/mnt/c/workspace/smart-backlog-assistant/{in,out}`
* Custom `./in` and `./out` overrides set via `backlog config --in ... --out ...` or `/config` persist in `smart-backlog-assistant/settings.json` alongside CLI theme, export mode, active provider, live toggle, and model choice.

## Quick Start (.NET 10 SDK)

```bash
# Restore, build, and run full test suite (180 tests)
dotnet test

# Optional: configure Gemini or Copilot credentials
export GEMINI_API_KEY="useYourGeminiAPI_Key"
# or: export GITHUB_TOKEN="ghp_..."

# Run interactive REPL or headless CLI
dotnet run --project src/SmartBacklogAssistant
```

## Containers & Podman Kubernetes

```bash
podman build -t localhost/smart-backlog-assistant:v1 -f Containerfile .
podman kube play k8s/smart-backlog-pod.yaml
podman logs smart-backlog-assistant-assistant
podman kube down k8s/smart-backlog-pod.yaml
```

## Findings & Iteration Backlog

See [BACKLOG.md](BACKLOG.md) and [PLAN.md](PLAN.md) for architecture notes, runtime findings (`SBA-101`–`SBA-105`), and CLI/TUI UX standards.
