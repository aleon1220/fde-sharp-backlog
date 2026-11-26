# fde-sharp-backlog (`SmartBacklogAssistant`)

.NET 10 hybrid CLI & interactive REPL/TUI assistant for transforming unstructured engineering discovery notes into standardized Jira backlog items using an **AI-First SDLC** workflow.

## Highlights

* **Headless `uv`-Style Subcommands:** Verb-driven commands (`process`, `draft`, `preview`, `status`, `config`, `keys`, `models`) with step timers, actionable `error:`/`hint:` diagnostics, and per-command `--help`.
* **Interactive REPL & Unified Orchestrator:** Zero-arg execution opens a boxed workspace header (`[0-4]`), shortcuts (`[w/g/d/p/v/s/c/?/q]`), `/slash` commands, `@filename` mentions, and fuzzy OSC 52 copy.
* **Multi-Provider AI & Key Lifecycle:** Orchestrates Google Gemini REST, OpenAI Gateway (`/v1/chat/completions` & `/v1/responses`), `Microsoft.Copilot.SDK` `2.0.1`, Azure/Claude BYOK, and `manual` (`--offline`) with `*`-masked API key management (`backlog keys`, `[K]`, `/keys`).
* **Type01 / Type02 Parity & Zero Hallucination:** Deterministic C# handles ingestion, secret redaction, formatting, and validation while LLMs synthesize requirements without inventing owners, IDs, or dates.
* **Green-Gated Releases:** Self-contained single-file binaries for `linux-x64`, `win-x64`, and `osx-arm64` with `SHA256SUMS.txt` and SLSA provenance attestations.

## Quick start

**From tested GitHub binary** — see [docs/getting-started/install-binary.md](docs/getting-started/install-binary.md) to download, verify SHA-256 checksums, and install:

```bash
backlog --version
backlog status
backlog
```

**From source (.NET 10 SDK):**

```bash
dotnet restore SmartBacklogAssistant.slnx --locked-mode
dotnet test SmartBacklogAssistant.slnx -c Release
dotnet run --project src/SmartBacklogAssistant -- status
dotnet run --project src/SmartBacklogAssistant -- draft "Implement OAuth2 PKCE Login" --type 01 --preview --offline
```

See [docs/getting-started/build-from-source.md](docs/getting-started/build-from-source.md) for hardened NuGet restore, proxy/fallback feeds, and UAT verification.

## Documentation

Browse the full [docs/README.md](docs/README.md) index or jump directly to a guide:

| Area | Guide | Summary |
| --- | --- | --- |
| Getting Started | [install-binary.md](docs/getting-started/install-binary.md) | Download, verify, install, and upgrade standalone GitHub binaries |
| Getting Started | [build-from-source.md](docs/getting-started/build-from-source.md) | .NET 10 SDK, hardened NuGet restore, lockfiles, and UAT checks |
| Usage | [cli.md](docs/usage/cli.md) | Headless subcommands, aliases, piping, and `--log-level` flags |
| Usage | [interactive-repl.md](docs/usage/interactive-repl.md) | Interactive menu (`[0-4]`), Orchestrator, `/slash` commands, and `@filename` |
| Configuration | [providers.md](docs/configuration/providers.md) | Gemini, Copilot SDK, OpenAI Gateway, Azure/Claude, and verification commands |
| Configuration | [environment-variables.md](docs/configuration/environment-variables.md) | Runtime, provider, logging, NuGet, and Project V2 environment variables |
| Configuration | [logging-and-secrets.md](docs/configuration/logging-and-secrets.md) | `stderr` telemetry, secret redaction, and `*`-masked API key management |
| Configuration | [workspace.md](docs/configuration/workspace.md) | Windows/WSL2 `./in` & `./out` paths and secret-free `settings.json` |
| Operations | [releases.md](docs/operations/releases.md) | Tag-driven release pipeline, local packaging, checksums, and attestation |
| Operations | [containers.md](docs/operations/containers.md) | Podman OCI build and Kubernetes pod deployment (`Containerfile`) |
| Operations | [github-project.md](docs/operations/github-project.md) | GitHub Project V2 schema, saved views, bootstrap, and lifecycle sync |
| Project | [engineering-principles.md](docs/project/engineering-principles.md) | Design philosophy, AI-First SDLC contract, formats, and build/secret rules |
| Project | [BACKLOG.md](docs/project/BACKLOG.md) | Runtime findings (`Finding 1`–`Finding 8`) and backlog (`SBA-101`–`SBA-112`) |
| Project | [PLAN.md](docs/project/PLAN.md) | Architecture blueprint, testing approach, and CLI/TUI standards |

## Project links

* [Repository](https://github.com/aleon1220/fde-sharp-backlog) · [Releases](https://github.com/aleon1220/fde-sharp-backlog/releases) · [Issues](https://github.com/aleon1220/fde-sharp-backlog/issues) · [Milestones](https://github.com/aleon1220/fde-sharp-backlog/milestones) · [UAT Gate (#6)](https://github.com/aleon1220/fde-sharp-backlog/issues/6)

## Findings & iteration backlog

See [BACKLOG.md](docs/project/BACKLOG.md) and [PLAN.md](docs/project/PLAN.md) for architecture notes, runtime findings (`SBA-101`–`SBA-112`), and CLI/TUI UX standards.
