# fde-sharp-backlog (`SmartBacklogAssistant`)

.NET 10 CLI application that helps create Jira backlogs following an **AI-First SDLC** paradigm (deterministic C# ingestion, runtime secret injection, validation, and formatting paired with AI prompt orchestration).

---

## Use Case 1: Practical Process As-Is v1 + Gemini Runtime Secret Injection

Implements the **as-is v1** backlog workflow with **Google Gemini runtime secret injection**:
1. **Runtime Environment & Build Telemetry**:
   - Inspects and displays at startup:
     - **OS Running**: Platform (`Windows`, `Linux`, `macOS`), OS version description, and OS/Process architecture (`X64`, `Arm64`).
     - **Build Info**: Application version (`v1.1.0-gemini-runtime`), build configuration (`Debug` / `Release`), and active .NET runtime (`.NET 10.0.x`).
     - **AI Model Enabled**: Defaults to **Google Gemini (`gemini-2.5-flash`)** for now, along with masked key status (`****<last4>`).
2. **Runtime Gemini Secret Injection (`GeminiSecretProvider`)**:
   - Users obtain their Gemini API key from: **https://aistudio.google.com/api-keys**
   - Resolves the secret at runtime in the following order:
     1. CLI flag (`--api-key <key>`)
     2. OS Environment Variables (`GEMINI_API_KEY`, `GOOGLE_API_KEY`, or `AI_API_KEY` across Process, User, and Machine scopes)
     3. **Interactive CLI Prompt**: When no key is entered or registered in the OS, the CLI prompts the user to enter their Gemini API key before proceeding.
3. **Local (`collect` -> `process` -> `validate`)**:
   - Reads raw discovery notes (`samples/raw-notes.txt`).
   - Loads a pre-designed prompt from a Markdown template (`prompts/as-is-v1-prompt.md`).
   - Routes through the **AI Model Orchestrator** (defaulting to `GoogleGemini` with the injected key, while also supporting `manual`, `azure`, and `claude`).
4. **Review (`review` -> `decide`) & Jira (`complete` | `schedule`)**:
   - Displays the validated draft and formats the Jira ticket as **`Type01`** (`complete` — Given / When / Then) or **`Type02`** (`schedule` — Title + Description) to console and `output/`.

---

## Run Natively (.NET 10 SDK)

### 1. Obtain & Register a Gemini API Key in the OS (Optional)
Obtain a Gemini API key from **https://aistudio.google.com/api-keys** and set it as an OS environment variable:

**Windows (PowerShell):**
```powershell
$env:GEMINI_API_KEY = "your-gemini-api-key"
```

**Linux / macOS (Bash/Zsh):**
```bash
export GEMINI_API_KEY="your-gemini-api-key"
```

### 2. Interactive Mode (Default — Prompts for Gemini Key if Not Set in OS)
If `GEMINI_API_KEY` is not registered in the OS environment or passed via `--api-key`, the CLI automatically asks for your Gemini API key before proceeding:
```bash
dotnet run --project src/SmartBacklogAssistant
```

### 3. Non-Interactive CLI Mode
Run with `GEMINI_API_KEY` registered in the OS or passed via `--api-key`:
```bash
dotnet run --project src/SmartBacklogAssistant -- \
  --input samples/raw-notes.txt \
  --prompt prompts/as-is-v1-prompt.md \
  --api-key "demo-key-1234" \
  --type Type01 \
  --non-interactive
```

Run in manual prompt mode (`--provider manual`):
```bash
dotnet run --project src/SmartBacklogAssistant -- \
  --provider manual \
  --type Type01 \
  --non-interactive
```

---

## Run Tests (Test Pyramid)

The test suite (`tests/SmartBacklogAssistant.Tests`) follows the **Test Pyramid**:
- **Unit Tests (`Unit/`)**: Fast, isolated tests for `GeminiSecretProvider`, `RuntimeEnvironmentService`, `RawDataCollector`, `PromptTemplateService`, `AiModelOrchestrator`, `JiraBacklogFormatter`, and CLI argument parsing.
- **Integration Tests (`Integration/`)**: End-to-end component tests for `AsIsV1Workflow` verifying runtime OS/build telemetry, OS environment variable secret injection, interactive CLI secret prompting when no key is registered, and artifact generation.

```bash
dotnet test
```

---

## Engineering Findings & Backlog

See [BACKLOG.md](BACKLOG.md) for engineering findings gathered during implementation and the prioritized iteration backlog (`SBA-101` through `SBA-105`).

---

## Run with Containers & Podman-Style Kubernetes

### 1. Build the Container Image
```bash
podman build -t localhost/smart-backlog-assistant:v1 -f Containerfile .
```

### 2. Run in a Podman Kubernetes Cluster (`podman kube play`)
Deploy the Kubernetes Pod manifest (`k8s/smart-backlog-pod.yaml`):
```bash
podman kube play k8s/smart-backlog-pod.yaml
```

Inspect the generated Jira backlog output in the pod logs:
```bash
podman logs smart-backlog-assistant-assistant
```

Tear down the pod when finished:
```bash
podman kube down k8s/smart-backlog-pod.yaml
```
