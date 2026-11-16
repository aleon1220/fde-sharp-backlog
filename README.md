# fde-sharp-backlog (`SmartBacklogAssistant`)

.NET 10 hybrid CLI & interactive REPL/TUI assistant for transforming unstructured engineering discovery notes into standardized Jira backlog items using an **AI-First SDLC** workflow.

## Executive Summary

**Smart Backlog Assistant** (`fde-sharp-backlog`) combines deterministic .NET 10 ingestion, schema validation, and cross-platform Windows/WSL workspace management with a multi-provider AI orchestrator (**Google Gemini REST** and **`GitHub.Copilot.SDK`** with native Copilot CLI or BYOK providers). Modeled after two gold-standard terminal paradigms:

1. **Astral `uv` (Frictionless One-Shot Subcommand CLI):** Deterministic, verb-driven subcommands (`process`, `draft`, `preview`, `status`, `config`, `models`) with elapsed step timers (`✓ Resolved ... in 1ms`), non-intrusive Braille progress spinners during async AI calls, actionable error + `hint:` diagnostics, and scannable `--help` output.
2. **Gemini CLI / Agy (Interactive REPL & Streamlined TUI):** Launching with zero arguments opens a compact, scannable interactive session with a boxed workspace bar (`In: ./in | Out: ./out | Export: .md`), single-key shortcuts (`[0-4]` or `[p/d/w/s/v/c/?/q]`), universal `[b]ack`/`[q]uit` submenu navigation, `/slash` commands (including `/wizard`), and `@filename` context mentions.

* **GitHub Repository & Project**: [aleon1220/fde-sharp-backlog](https://github.com/aleon1220/fde-sharp-backlog) · [Project Backlog & Issues](https://github.com/aleon1220/fde-sharp-backlog/issues)
* **Active Release Milestone**: [Milestone #1 — `release-fde-version`](https://github.com/aleon1220/fde-sharp-backlog/milestone/1)
* **CLI & TUI Ergonomics Track**: [Issue #3](https://github.com/aleon1220/fde-sharp-backlog/issues/3) · [Issue #5](https://github.com/aleon1220/fde-sharp-backlog/issues/5) · [Issue #7](https://github.com/aleon1220/fde-sharp-backlog/issues/7) · [Issue #8](https://github.com/aleon1220/fde-sharp-backlog/issues/8) · [Issue #11](https://github.com/aleon1220/fde-sharp-backlog/issues/11) · [Issue #12](https://github.com/aleon1220/fde-sharp-backlog/issues/12)

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
dotnet run --project src/SmartBacklogAssistant -- config --json on --in ./in --out ./out --theme uv --provider copilot-sdk --verbosity bal

# Preview & interactively fuzzy-select saved outputs or copy simple / clean plain text / Jira fields
dotnet run --project src/SmartBacklogAssistant -- preview --select
dotnet run --project src/SmartBacklogAssistant -- preview latest --copy simple
dotnet run --project src/SmartBacklogAssistant -- preview latest --copy clean
dotnet run --project src/SmartBacklogAssistant -- preview latest --copy title

# Crisp diagnostic table: WSL2/Windows paths, ./in count, export format, & AI provider matrix
dotnet run --project src/SmartBacklogAssistant -- status
```

Subcommand aliases preserved for backward compatibility: `run` (`process`), `add` (`draft`), `doctor` (`status`), `menu`/`tui`. Pass `<command> --help` (e.g. `process --help`, `config --help`) for command-specific help.

## Interactive REPL & Menu (Gemini CLI / Agy Style)

Running with zero arguments displays the compact boxed workspace header (`In`, `Out`, `Export`, `Provider`, `Live`, `Theme`) and supports numeric options, single-key quick shortcuts, `/slash` commands, and `@filename` mentions seamlessly from `backlog > `:

* **Numeric Menu `[0-5]` & Quick Shortcuts `[w/d/p/v/s/c/?/q]`:**
  * `[1]` / `w` **Wizard Orchestrator (All Options)** — Unified hub (`wizard > `, `/wizard`) offering ALL interactive flows in one place: `[1]` Draft from Goal / Title, `[2]` Process Input Files (`./in`, with single-file review in the Wizard), `[3]` Preview, Refine & Copy saved `./out/*.md` (`OpenSavedMarkdownInWizardReview`), `[4]` Quick Copy Last Outcome (Simple `[N]`, Markdown, Raw Table, Clean, or Jira fields), `[5]` Workspace & Settings, `[6]` Status & AI Diagnostics, plus direct `@filename` review and `/slash` commands.
  * `[2]` / `d` **Draft Goal / Title** — Launch the unified Guided Draft & Review Wizard directly from Main Menu: supports `Type01` (`complete`), `Type02` (`schedule`), or `Both`, three Outcome Verbosity levels (`[1] Minimalistic <=25w`, `[2] Balanced <=57w`, `[3] Detailed <=95w`), two-step INVEST Goal Expansion (`prompts/prompt-goal-expand.md`), `[N]` Simple Unformatted Text copy (`Title` / `---` / `Description`), `[M]` Markdown, `[W]` Raw Table Text, `[S]` Field & Clean Text picker, `[1-3]` Alternative selection, `[A]ccept / [R]efine / [V]erbosity / [T]ype / [P]review / [C]ancel`.
  * `[3]` / `p` **Process Input Files** — Batch or single file from `./in` (`all`, `single` via Wizard Review, or `dry` preview).
  * `[4]` / `v` **Preview & Copy (.md)** — Fuzzy-filter or pick saved `./out/*.md` outcomes, inspect in a unified 2-column colored table, copy Raw Text (`raw`), Markdown (`md`), Clean Plain Text (`clean`), Simple Unformatted (`simple` — `Title / --- / Description`), or individual Jira fields (`title`, `story`, `desc`, `ac`, `alt1..alt3`), or press `[E]` to promote the file directly into Wizard Review (`Refine/Verbosity in Wizard`). Native clipboards (`Set-Clipboard`/`clip.exe`, `pbcopy`, `wl-copy`/`xclip`/`xsel`) fall back automatically to ANSI **OSC 52** (with `tmux` passthrough) over SSH/headless terminals.
  * `[5]` / `s` **Workspace & Settings** — Compact 3-line summary (`Paths`, `Active`, `Config`) with `[1]` JSON Export toggle, `[2]` Inspect Directories, `[3]` full Runtime & AI Diagnostics, `[4]` CLI Theme (`agy`, `uv`, `kubectl`), `[5]` Live AI toggle, `[6]` Select AI Model, `[7]` View `settings.json`, `[8]` Select AI Provider, and `[9]` Default Outcome Verbosity (`Minimalistic` / `Balanced` / `Detailed`).
  * `[0]` / `q` **Exit** — Universal navigation across all submenus: enter `0` / `b` / `back` to return to the parent menu, or `q` / `quit` to exit anytime.
* **`/slash` Commands:**
  * `/wizard` (`/guided`), `/process [all|<file>] [--dry-run] [--json]`, `/draft "<goal>" [-t 01|02|both] [--details "..."] [--search] [--preview]`, `/preview [<file>]`, `/copy [simple|raw|md|clean|title|story|desc|ac|alt1..]`, `/verbosity [min|bal|det]`, `/settings`, `/status`, `/config [--json on|off] [--in <dir>] [--out <dir>] [--verbosity min|bal|det]`, `/provider <name>`, `/models`, `/theme <agy|uv|kubectl>`, `/json [on|off]`, `/live [on|off]`, `/help`, `/clear`, `/exit`.
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
# Restore, build, and run full test suite (206 tests)
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
