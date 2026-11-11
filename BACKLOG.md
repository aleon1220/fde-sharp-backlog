# Engineering Findings & Iteration Backlog (`feature/connect-gemini-api`)

## 1. Runtime & Architecture Findings Gathered

During the implementation and verification of the **Runtime Gemini Secret API Key Injection** use case on branch `feature/connect-gemini-api`, the following technical findings were gathered:

### Finding 1: Multi-Scope OS Environment Variable Resolution on Windows
* **Observation:** When users register `GEMINI_API_KEY` on Windows via `setx GEMINI_API_KEY "..."` or the Windows System Properties dialog, the variable is persisted to `EnvironmentVariableTarget.User` or `EnvironmentVariableTarget.Machine` rather than already-running terminal sessions (`EnvironmentVariableTarget.Process`).
* **Action Taken:** `GeminiSecretProvider` now checks `EnvironmentVariableTarget.Process` first, falls back to `User` and `Machine` scopes when running on Windows (`OperatingSystem.IsWindows()`), and injects the resolved secret into the active process environment.
* **Supported Variables (in precedence order):**
  1. CLI Flag: `--api-key <key>`
  2. OS Environment Variable: `GEMINI_API_KEY` (Primary)
  3. OS Environment Variable: `GOOGLE_API_KEY` (Google AI SDK standard alias)
  4. OS Environment Variable: `AI_API_KEY` (Legacy/container fallback)

### Finding 2: Interactive CLI Prompt Gate vs. Headless Execution
* **Observation:** When a user has not registered a Gemini key in the OS or passed `--api-key`, interactive CLI runs must pause and ask the user for their key (directing them to `https://aistudio.google.com/api-keys`) before proceeding. However, automated CI/CD or Kubernetes runs (`--non-interactive` or redirected EOF streams) must never block indefinitely waiting for `Console.ReadLine()`.
* **Action Taken:** `GeminiSecretProvider.ResolveOrPrompt` loops on interactive input until a non-empty key is entered, and throws a fast, actionable `InvalidOperationException` referencing `https://aistudio.google.com/api-keys` if `--non-interactive` is enabled or `stdin` reaches EOF.

### Finding 3: Zero-Leakage Secret Masking in Runtime Telemetry
* **Observation:** Printing runtime environment diagnostics (OS, Build Info, Enabled AI Model, Key Source) risks leaking raw secrets into console logs, container stdout (`podman logs`), or generated artifacts.
* **Action Taken:** All console banners and orchestrator guidance messages strictly use `GeminiSecretProvider.MaskKey` (`****<last4>`), ensuring the plaintext secret never appears in `Console.Out` or saved files.

### Finding 4: Host .NET SDK Drift & Container Virtualization Diagnostics
* **Observation:** On the Windows test host (`Microsoft Windows 10.0.26100 | X64`), the global `C:\Program Files\dotnet` installation only contained the `.NET 8.0.33` runtime without a `.NET 10.0` SDK, and WSL2/Podman Machine was blocked by `HCS_E_HYPERV_NOT_INSTALLED`. Installing `.NET SDK 10.0.403` into the user-scoped `$HOME\.dotnet` directory enabled native `.NET 10` compilation and test execution without admin privileges.
* **Action Taken:** `RuntimeEnvironmentService` now inspects and prints OS platform/architecture, assembly build version/configuration (`Debug`/`Release`), `.NET` framework description, and the enabled AI model (`Google Gemini (gemini-2.5-flash)`) at startup so environment discrepancies are immediately visible.

---

## 2. Prioritized Backlog for Next Iteration

The following backlog items have been structured in Jira-ready `Type01` (`complete` — Given/When/Then) and `Type02` (`schedule` — Title + Description) formats for upcoming sprints:

### [SBA-101] Live Google Gemini `generateContent` API Integration (`Type01` — `complete` | Priority: P0)
* **User Story:** As an engineering lead, I want the orchestrator to send the rendered Markdown prompt to the live Google Gemini API (`gemini-2.5-flash`) using the runtime-injected `GEMINI_API_KEY`, so that unstructured discovery notes and noisy transcripts are dynamically transformed into validated Jira stories.
* **Acceptance Criteria (Given / When / Then):**
  * **Given** a valid `GEMINI_API_KEY` obtained from `https://aistudio.google.com/api-keys` is injected via OS environment variable or CLI prompt
  * **When** `AiModelOrchestrator` processes raw discovery notes with `AiProviderType.GoogleGemini`
  * **Then** the application invokes the Gemini endpoint with structured JSON output enforcement and binds the response to `ProcessedOutcome`

### [SBA-102] Masked Character Input (`ReadKey`) for Interactive CLI Secret Prompt (`Type01` — `complete` | Priority: P1)
* **User Story:** As a developer entering my Gemini API key in a shared terminal or screen-share, I want keystrokes masked with `*` during interactive CLI entry, so that shoulder-surfing or screen recordings do not expose my plaintext key.
* **Acceptance Criteria (Given / When / Then):**
  * **Given** `GEMINI_API_KEY` is not set in the OS environment and the user runs the CLI interactively
  * **When** the user types their Gemini API key at the CLI prompt
  * **Then** the terminal intercepts keystrokes without echoing plaintext characters and displays `****<last4>` upon confirmation

### [SBA-103] Kubernetes `SecretKeyRef` & `.NET User Secrets` Provider Support (`Type02` — `schedule` | Priority: P1)
* **Description:** Extend `GeminiSecretProvider` and `k8s/smart-backlog-pod.yaml` to consume Kubernetes `Secret` manifests (`valueFrom.secretKeyRef`), `.NET User Secrets` (`dotnet user-secrets`), and 1Password CLI (`op run`) environment injection so production containers and local workstations avoid storing plaintext keys in shell profiles.

### [SBA-104] Gemini API Key Pre-Flight Validation & Retry Policy (`Type01` — `complete` | Priority: P2)
* **User Story:** As a CLI user, I want immediate validation of my Gemini API key format and clear retry handling on transient `HTTP 429` / `503` responses, so that invalid keys or rate limits are diagnosed before full pipeline execution.
* **Acceptance Criteria (Given / When / Then):**
  * **Given** a user supplies a Gemini API key via OS environment variable or CLI prompt
  * **When** the Gemini API returns `400 API_KEY_INVALID` or `429 RESOURCE_EXHAUSTED`
  * **Then** the CLI logs a clear diagnostic message with a link to `https://aistudio.google.com/api-keys` or applies exponential backoff for transient errors

### [SBA-105] Developer Environment Bootstrap & SDK Pre-Flight Script (`Type02` — `schedule` | Priority: P2)
* **Description:** Provide cross-platform bootstrap scripts (`scripts/setup-dev-env.ps1` and `scripts/setup-dev-env.sh`) that verify `.NET 10.0 SDK` availability (configuring `$HOME/.dotnet` on `PATH` if needed), check `GEMINI_API_KEY` registration across OS scopes, and validate Podman/WSL2 virtualization readiness.
