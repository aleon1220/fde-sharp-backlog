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

### Finding 5: Non-Clean Workstation NuGet Source Drift & Pre-Production Restore Hardening ([Issue #57](https://github.com/aleon1220/fde-sharp-backlog/issues/57) · Child of [#6](https://github.com/aleon1220/fde-sharp-backlog/issues/6))
* **Observation:** During live UAT and dogfooding on a non-clean workstation environment, package restore for `Microsoft.Copilot.SDK` (`2.0.1`) and test runners failed because the host user's `NuGet.Config` lacked an active `nuget.org` v3 feed registration (or carried stale/disabled feeds).
* **Action Taken:**
  * Hardened [`nuget.config`](nuget.config) with `<clear />` inside `<packageSources>` and `<disabledPackageSources>` alongside `https://api.nuget.org/v3/index.json` (`protocolVersion="3"`), preventing host `NuGet.Config` drift from poisoning repo restores.
  * Added [`Directory.Build.props`](Directory.Build.props) (`RestorePackagesWithLockFile=true`, CI `RestoreLockedMode=true`) and committed deterministic lockfiles ([`src/SmartBacklogAssistant/packages.lock.json`](src/SmartBacklogAssistant/packages.lock.json) and [`tests/SmartBacklogAssistant.Tests/packages.lock.json`](tests/SmartBacklogAssistant.Tests/packages.lock.json)).
  * Added cross-platform restore scripts ([`scripts/restore-nuget.sh`](scripts/restore-nuget.sh), [`scripts/restore-nuget.ps1`](scripts/restore-nuget.ps1)) supporting `--fix-user-config`, `--clear-cache`, `NUGET_ENABLE_ENHANCED_HTTP_RETRY=true` exponential backoff, upstream caching proxies (`NUGET_PROXY_FEED_URL` / `NUGET_PROXY_ONLY`), local fallback caches (`NUGET_FALLBACK_FEED_DIR` / `NUGET_FALLBACK_PACKAGES_DIR` / `.nuget/fallback`), and ephemeral token-scoped auth (`NUGET_AUTH_TOKEN` / `VSS_NUGET_ACCESSTOKEN` / `GITHUB_TOKEN`) via `NuGetPackageSourceCredentials_upstream_proxy`.
  * Updated [`Containerfile`](Containerfile) (`--locked-mode` restore, `--no-restore` publish, retry env vars), [`SecretRedactor`](src/SmartBacklogAssistant/Services/SecretRedactor.cs) (`NUGET_AUTH_TOKEN` / `VSS_NUGET_ACCESSTOKEN` masking), [`.github/workflows/preprod-nuget-uat.yml`](.github/workflows/preprod-nuget-uat.yml), [`README.md`](README.md), and [`NuGetHardeningContractTests`](tests/SmartBacklogAssistant.Tests/Unit/NuGetHardeningContractTests.cs).

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

### [SBA-106] Unified Wizard Orchestrator & Quick-Action Navigation (`Type01` — `complete` | Priority: P1)
* **User Story:** As a CLI user running Smart Backlog Assistant interactively, I want `[1] Wizard Orchestrator` at the top of the main menu offering every workflow, preview/refine/copy, settings, and diagnostic option in one cohesive hub with single-key shortcuts (`w/d/p/v/s/c/?/q`) and universal `[b]ack`/`[q]uit` navigation, so that common backlog workflows work end-to-end from one orchestrator.
* **Acceptance Criteria (Given / When / Then):**
  * **Given** the user launches the interactive REPL (`dotnet run --project src/SmartBacklogAssistant`)
  * **When** the user navigates via `[0-5]`, enters `[1] Wizard Orchestrator` (`[0-6]`, `/wizard`, `@filename`), or promotes from `[4] Preview & Copy` via `[E]`
  * **Then** the Wizard Orchestrator handles goal drafting, single-file review, saved `.md` reconstruction/refinement (`Verbosity`, `Refine`, `Type`), quick copy (`Simple`, `Raw`, `Markdown`, `Clean`, `Fields`), workspace settings (`[9]` Default Verbosity, `/verbosity`), and runtime diagnostics cleanly.

### [SBA-107] Interactive Output Selection, Clean Plain Text & OSC 52 Clipboard (`Type01` — `complete` | Priority: P1 — Issue #12)
* **User Story:** As a CLI operator, I want to fuzzy-select saved outcomes and individual Jira fields (`simple`, `raw`, `md`, `clean`, `title`, `story`, `desc`, `ac`, `alt1..alt3`) with keyboard navigation, route picker prompts to `stderr`, and copy clean text via native clipboards or OSC 52 fallback, so that pasting into Jira or piping `stdout` requires zero manual cleanup.
* **Acceptance Criteria (Given / When / Then):**
  * **Given** generated or saved Markdown outcomes exist in `./out`
  * **When** the user runs `preview --select`, `--copy <simple|clean|title|story|desc|ac|alt1>`, `/copy <target>`, `[4] Preview & Copy`, or `[S]`/`[N]` in the Guided Wizard
  * **Then** `InteractiveSelector` fuzzy-matches files and Jira fields (with `stderr` prompt routing in `--select` mode and graceful non-interactive exit), `MarkdownOutcomeRenderer` supports unformatted Simple (`Title / --- / Description`) and border-free clean text, `OsClipboardService` falls back to OSC 52 (`tmux`-aware) when native tools are unavailable, and all automated tests pass.

### [SBA-108] Simpler `Type01` & `Type02` CLI/TUI Output Format & Verbosity Modes (`Type01` — `complete` | Priority: P1 — Issue #8)
* **User Story:** As a CLI/TUI operator, I want `Type01` (`complete`) and `Type02` (`schedule`) outputs to render with clean terminal cells, selectable verbosity modes (`Minimalistic <=25w`, `Balanced <=57w`, `Detailed <=95w`), zero live-vs-saved drift, and quiet-by-default step chrome, so that reviewing and copying backlog items is immediate and scannable.
* **Acceptance Criteria (Given / When / Then):**
  * **Given** a user drafts or processes a `Type01`, `Type02`, or `Both` item via CLI or TUI
  * **When** the outcome is rendered in the terminal and saved to `./out`
  * **Then** `Type01` persists and renders `Title`, `User Story`, concise `Description`, `Acceptance`, and `Alternatives`; `Type02` renders strictly `Title`, concise `Description`, and `Alternatives` without injecting `User Story`; verbosity adapts cleanly (`Minimalistic`, `Balanced`, `Detailed`) with persisted `defaultVerbosity`; terminal cells strip `# [Jira - ...]`, `###`, and `**` clutter while preserving full Markdown in `.md` files and `md` copy; and pre-table output condenses to compact `✓ Collected`, `✓ Expanded`, and `✓ Generated` lines.

### [SBA-109] Harden Pre-Production NuGet Restores to Prevent UAT and Dogfooding Outages (`Type02` — `schedule` | Priority: P1 — Issue #57, Child of #6 — Implemented)
* **Objective:** Harden NuGet feed reliability, source registration, and restore reproducibility across pre-production runners and non-clean developer/UAT workstations to eliminate build interruptions.
* **Implemented Hardening Deliverables:**
  * **Isolated Repository `nuget.config`:** Pins `https://api.nuget.org/v3/index.json` (`protocolVersion="3"`) with `<clear />` inside `<packageSources>` and `<disabledPackageSources>` plus `<packageRestore>` defaults.
  * **Lockfile Determinism (`Directory.Build.props` + `packages.lock.json`):** Enables `RestorePackagesWithLockFile=true` and CI `RestoreLockedMode=true`, with committed lockfiles for `SmartBacklogAssistant` and `SmartBacklogAssistant.Tests`.
  * **Caching Proxies & Local Fallback Feeds:** Conditional MSBuild properties (`NUGET_PROXY_FEED_URL`, `NUGET_PROXY_ONLY`, `NUGET_FALLBACK_FEED_DIR`, `NUGET_FALLBACK_PACKAGES_DIR`, `.nuget/fallback`) and managed restore scripts ([`scripts/restore-nuget.sh`](scripts/restore-nuget.sh), [`scripts/restore-nuget.ps1`](scripts/restore-nuget.ps1)).
  * **Ephemeral Token-Scoped CI/CD Auth & Secret Redaction:** Uses `NUGET_AUTH_TOKEN`, `VSS_NUGET_ACCESSTOKEN`, or `GITHUB_TOKEN` (plus OIDC `id-token: write` readiness in [`.github/workflows/preprod-nuget-uat.yml`](.github/workflows/preprod-nuget-uat.yml)) passed in memory via `NuGetPackageSourceCredentials_upstream_proxy`, with `SecretRedactor` masking tokens in `stderr` logs.
  * **HTTP Retries & Container/Docs Parity:** Configures `NUGET_ENABLE_ENHANCED_HTTP_RETRY=true` (6 retries, HTTP 429 + `Retry-After` handling), hardens [`Containerfile`](Containerfile) (`--locked-mode` + `--no-restore`), and documents recovery workflows in [`README.md`](README.md).

### [SBA-110] Execute CLI Tool from Versioned GitHub Binary (`Type01` — `complete` | Priority: P0 — Issue #59, Milestone #1 — Implemented)
* **User Story:** As a CLI user, I want to download and execute versioned, CI-verified `SmartBacklogAssistant` binaries directly from GitHub Releases using clear instructions in `README.md`, so that I can trust green-checked builds and follow a fast, repeatable setup without building from source.
* **Implemented Deliverables:**
  * **Embedded Prompt Parity & Diagnostics ([`SmartBacklogAssistant.csproj`](src/SmartBacklogAssistant/SmartBacklogAssistant.csproj), [`PromptTemplateService`](src/SmartBacklogAssistant/Services/PromptTemplateService.cs), [`WorkspaceDiagnosticService`](src/SmartBacklogAssistant/Services/WorkspaceDiagnosticService.cs), [`AsIsV1Workflow`](src/SmartBacklogAssistant/Workflow/AsIsV1Workflow.cs)):** Embeds built-in `prompts/*.md` resources inside the compiled binary (`IncludeNativeLibrariesForSelfExtract=true`) and updates `Directory.Build.props` so standalone single-file binaries run full prompts and report `(bundled in binary)` outside any git checkout.
  * **Cross-Platform Packaging & Out-of-Repo Smoke Scripts ([`scripts/package-release.sh`](scripts/package-release.sh), [`scripts/package-release.ps1`](scripts/package-release.ps1)):** Publishes self-contained single-file binaries (`PublishSingleFile=true`, `PublishTrimmed=false`), archives `.tar.gz` / `.zip` payloads (`SmartBacklogAssistant` + `backlog` symlink on POSIX), generates `.sha256` checksums, and smoke-tests both extracted archives and isolated standalone binaries (`--version`, `--help`, `status`, `draft`, `process`).
  * **Green-Gated Release Workflow ([`.github/workflows/release-binaries.yml`](.github/workflows/release-binaries.yml)):** Triggers on `v*` tags and `workflow_dispatch`; gates `package` (`linux-x64`, `win-x64`, `osx-arm64`) behind `verify` (hardened lockfile restore, Release `-warnaserror` build, full `dotnet test`); assembles `SHA256SUMS.txt`; attaches SLSA provenance via `actions/attest-build-provenance@v2`; and publishes GitHub Releases only when every check passes green.
  * **Documentation & Verification:** Added the end-to-end **Install & Run from Tested GitHub Binary** guide in [`README.md`](README.md), plus unit/contract tests in [`PromptTemplateServiceTests`](tests/SmartBacklogAssistant.Tests/Unit/PromptTemplateServiceTests.cs) and [`ReleaseBinaryContractTests`](tests/SmartBacklogAssistant.Tests/Unit/ReleaseBinaryContractTests.cs).
