# Documentation Index

Reference guides, configuration references, operational runbooks, and project architecture for `SmartBacklogAssistant`.

| Section | Page | What's inside |
|---|---|---|
| Getting Started | [install-binary.md](getting-started/install-binary.md) | Download, verify checksums/attestation, install, and upgrade standalone GitHub binaries |
| Getting Started | [build-from-source.md](getting-started/build-from-source.md) | .NET 10 prerequisites, hardened NuGet restore, lockfile troubleshooting, and UAT build checks |
| Usage | [cli.md](usage/cli.md) | Headless `uv`-style CLI subcommands (`process`, `draft`, `config`, `preview`, `keys`, `status`), aliases, and logging flags |
| Usage | [interactive-repl.md](usage/interactive-repl.md) | Zero-arg REPL/TUI menu (`[0-4]`), Backlog Assisted Orchestrator, `/slash` commands, and `@filename` mentions |
| Configuration | [providers.md](configuration/providers.md) | Multi-provider AI orchestration (`gemini`, `copilot-sdk`, `openai`, `azure`, `claude`, `manual`), credentials, and verification commands |
| Configuration | [environment-variables.md](configuration/environment-variables.md) | Complete runtime, AI provider, logging, NuGet proxy/fallback, and Project V2 environment variables |
| Configuration | [logging-and-secrets.md](configuration/logging-and-secrets.md) | `stderr` diagnostic logging, secret redaction, and interactive `*`-masked API key management |
| Configuration | [workspace.md](configuration/workspace.md) | Windows and WSL2 default `./in` and `./out` directory mapping and `settings.json` persistence |
| Operations | [releases.md](operations/releases.md) | Maintainer release workflow, local packaging dry-runs, `SHA256SUMS.txt`, and SLSA provenance |
| Operations | [containers.md](operations/containers.md) | Podman/OCI container build and Kubernetes pod deployment (`Containerfile` & `k8s/smart-backlog-pod.yaml`) |
| Operations | [github-project.md](operations/github-project.md) | GitHub Project V2 schema, custom fields, saved views, bootstrap scripts, and automated PR lifecycle sync |
| Project | [engineering-principles.md](project/engineering-principles.md) | Design philosophy, AI-First SDLC contract, Type01/Type02 output parity, and build/lockfile/security discipline |
| Project | [BACKLOG.md](project/BACKLOG.md) | Runtime findings (`Finding 1`–`Finding 8`) and prioritized backlog (`SBA-101`–`SBA-112`) |
| Project | [PLAN.md](project/PLAN.md) | Problem definition, AI-First SDLC architecture, testing approach, and CLI/TUI standards |

See also: [Root README](../README.md)
