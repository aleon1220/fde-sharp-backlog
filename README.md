# fde-sharp-backlog (`SmartBacklogAssistant`)

.NET 10 CLI tool for generating Jira backlogs using an **AI-First SDLC** workflow.

## Workflow Overview

1. **Runtime Telemetry**: Displays active OS, build info (`v1.1.0-gemini-runtime`), .NET runtime, and enabled AI model (`Google Gemini / gemini-2.5-flash`).
2. **Gemini Secret Injection**: Resolves the API key (obtained from https://aistudio.google.com/api-keys) in order:
   - CLI flag: `--api-key <key>`
   - OS environment variable: `GEMINI_API_KEY` (or `GOOGLE_API_KEY` / `AI_API_KEY`)
   - Interactive CLI prompt: Requests the key before proceeding if none is registered.
3. **Interactive Colored CLI Menu & Workspace Compatibility (`Windows` & `WSL`)**:
   - **Windows Default Workspace**: `c:\workspace\smart-backlog-assistant\in` -> `c:\workspace\smart-backlog-assistant\out`
   - **WSL Compatible Workspace**: `/mnt/c/workspace/smart-backlog-assistant/in` -> `/mnt/c/workspace/smart-backlog-assistant/out`
   - **Menu `[1]` Process Input Directory**: Reads discovery files from `smart-backlog-assistant/in` and outputs formatted Jira items to `smart-backlog-assistant/out`.
   - **Menu `[2]` Add Goal or Title**: Enter a Goal or Title and select output **`Type 01`** (`complete` — Given/When/Then) or **`Type 02`** (`schedule` — Title + Description) ready to be added into `smart-backlog-assistant/out`.
   - **Menu `[3]` Workspace & Runtime Status**: Inspects OS, WSL/Windows path mappings, AI model, and directory contents.
4. **Local Processing (`collect` -> `process` -> `validate`)**: Ingests raw discovery notes (`smart-backlog-assistant/in` or `samples/raw-notes.txt`), renders `prompts/as-is-v1-prompt.md`, and validates the structured draft.
5. **Review & Jira Output (`Type01` | `Type02`)**: Formats `Type01` (`complete` — Given/When/Then) or `Type02` (`schedule` — Title + Description) to console and `smart-backlog-assistant/out` (or `output/`).

## Quick Start (.NET 10 SDK)

* [get dotnet](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

```powershell
winget install Microsoft.DotNet.SDK.10
```

* 1. Set OS Environment Variable (Optional)

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
