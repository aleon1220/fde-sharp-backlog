# fde-sharp-backlog (`SmartBacklogAssistant`)

.NET 10 CLI application that helps create Jira backlogs following an **AI-First SDLC** paradigm (deterministic C# ingestion, validation, and formatting paired with AI prompt orchestration).

---

## Use Case 1: Practical Process As-Is v1

Implements the **as-is v1** backlog workflow:
1. **Local (`collect` -> `process` -> `validate`)**:
   - Reads raw discovery notes (`samples/raw-notes.txt`).
   - Loads a pre-designed prompt from a Markdown template (`prompts/as-is-v1-prompt.md`).
   - Routes through the **AI Model Orchestrator**:
     - **Default (`manual`)**: Renders the Markdown prompt with the raw data and suggests running it in your desired AI model (e.g., Microsoft Copilot, Azure OpenAI, Google Gemini, or Anthropic Claude).
     - **Mocked Providers (`azure`, `gemini`, `claude`)**: Simulates model selection with optional API key masking and deterministic outcome validation.
2. **Review (`review` -> `decide`)**:
   - Displays the validated draft and lets the user select the backlog type:
     - **`Type01`** -> `complete`: Formats Jira ticket with **Given / When / Then** acceptance criteria.
     - **`Type02`** -> `schedule`: Formats Jira ticket with **Title + Description**.
3. **Jira (`complete` | `schedule`)**:
   - Outputs Jira-ready Markdown and JSON to the console and `output/`.

---

## Run Natively (.NET 10 SDK)

### 1. Interactive Mode (Default)
Prompts you to choose an AI model provider (defaulting to the Markdown prompt suggestion) and select `Type01` or `Type02` during review:
```bash
dotnet run --project src/SmartBacklogAssistant
```

### 2. Non-Interactive CLI Mode
Run with explicit flags (e.g., default Markdown prompt + `Type01` Given/When/Then):
```bash
dotnet run --project src/SmartBacklogAssistant -- \
  --input samples/raw-notes.txt \
  --prompt prompts/as-is-v1-prompt.md \
  --provider manual \
  --type Type01 \
  --non-interactive
```

Run with a mocked AI model provider (`azure`, `gemini`, or `claude`) and `Type02` (Title + Description):
```bash
dotnet run --project src/SmartBacklogAssistant -- \
  --provider gemini \
  --api-key "demo-key-1234" \
  --type Type02 \
  --non-interactive
```

---

## Run Tests (Test Pyramid)

The test suite (`tests/SmartBacklogAssistant.Tests`) follows the **Test Pyramid**:
- **Unit Tests (`Unit/`)**: Fast, isolated tests for `RawDataCollector`, `PromptTemplateService`, `AiModelOrchestrator`, `JiraBacklogFormatter`, and CLI argument parsing.
- **Integration Tests (`Integration/`)**: End-to-end component tests for `AsIsV1Workflow` verifying file I/O and interactive/non-interactive streams.

```bash
dotnet test
```

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
