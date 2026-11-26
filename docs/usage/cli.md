# CLI Subcommands (`uv` Style — Headless Execution)

Run `SmartBacklogAssistant` non-interactively via verb-driven subcommands, piping, and per-command `--help`.

## Subcommand Examples

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

# Manage AI provider API keys interactively (masked '*'), validate format, verify live, or clear
dotnet run --project src/SmartBacklogAssistant -- keys status
dotnet run --project src/SmartBacklogAssistant -- keys status --json
dotnet run --project src/SmartBacklogAssistant -- keys set gemini
dotnet run --project src/SmartBacklogAssistant -- keys set openai --verify --scope user
dotnet run --project src/SmartBacklogAssistant -- keys set azure --gateway-url https://<resource>.openai.azure.com --verify
dotnet run --project src/SmartBacklogAssistant -- keys validate claude
dotnet run --project src/SmartBacklogAssistant -- keys verify all
dotnet run --project src/SmartBacklogAssistant -- keys clear openai --scope all

# Workspace paths, active provider readiness, and diagnostics
dotnet run --project src/SmartBacklogAssistant -- status
```

## Aliases & Help

Aliases preserved: `run` (`process`), `add` (`draft`), `doctor` (`status`), `view` (`preview`), `key` (`keys`), `menu`/`tui`. Pass `<command> --help` (e.g. `draft --help`, `keys --help`, `status --help`) for command-specific options.

See also: [interactive-repl.md](interactive-repl.md) · [providers.md](../configuration/providers.md) · [logging-and-secrets.md](../configuration/logging-and-secrets.md) · [workspace.md](../configuration/workspace.md)
