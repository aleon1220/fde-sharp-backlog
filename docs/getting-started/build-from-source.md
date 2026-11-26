# Build from Source & Hardened NuGet Restore

Set up a local `.NET 10` development and UAT environment using isolated NuGet sources, lockfile enforcement, and proxy/fallback feeds.

## Prerequisites & Hardened NuGet Restore (UAT & Pre-Production Runners)

* **.NET 10 SDK** (`dotnet --version` >= `10.0`)
* **Isolated Sources & Lockfile Determinism ([Issue #57](https://github.com/aleon1220/fde-sharp-backlog/issues/57)):**
  * Root [`nuget.config`](../../nuget.config) uses `<clear />` inside `<packageSources>` and `<disabledPackageSources>` while pinning `https://api.nuget.org/v3/index.json` (`protocolVersion="3"`), isolating restores from stale or disabled global/machine `NuGet.Config` entries.
  * [`Directory.Build.props`](../../Directory.Build.props) enables `RestorePackagesWithLockFile` (committed [`src/SmartBacklogAssistant/packages.lock.json`](../../src/SmartBacklogAssistant/packages.lock.json) and [`tests/SmartBacklogAssistant.Tests/packages.lock.json`](../../tests/SmartBacklogAssistant.Tests/packages.lock.json)) and automatically enforces `RestoreLockedMode=true` when `ContinuousIntegrationBuild=true`.
* **Recommended One-Command Hardened Restore (Linux / macOS / Windows):**

```bash
# Linux / macOS / WSL2 (enables HTTP retries, enforces --locked-mode, optionally repairs user NuGet.Config)
./scripts/restore-nuget.sh --locked-mode --fix-user-config

# Direct dotnet CLI equivalent (or register nuget.org globally for other repos)
dotnet nuget add source https://api.nuget.org/v3/index.json --name nuget.org
NUGET_ENABLE_ENHANCED_HTTP_RETRY=true dotnet restore SmartBacklogAssistant.slnx --locked-mode
```

```powershell
# Windows / PowerShell (hardened restore with HTTP retries + --locked-mode)
.\scripts\restore-nuget.ps1 -LockedMode $true -FixUserConfig
```

* **Upstream Caching Proxies, Local Fallback Feeds & Ephemeral Token Auth:**
  * **Caching Proxy & Fallback Feeds:** Set `NUGET_PROXY_FEED_URL` (e.g., GitHub Packages `https://nuget.pkg.github.com/<owner>/index.json` or Azure Artifacts upstream to `nuget.org`; add `NUGET_PROXY_ONLY=true` for proxy-only air-gapped runners), `NUGET_FALLBACK_FEED_DIR` (local `.nupkg` directory), and/or `NUGET_FALLBACK_PACKAGES_DIR` (or repo-local `.nuget/fallback` expanded cache).
  * **Token-Scoped / Workload Identity CI Auth (Zero Static PATs):** Supply short-lived credentials via `NUGET_AUTH_TOKEN`, `VSS_NUGET_ACCESSTOKEN`, or `GITHUB_TOKEN` (for GitHub Packages) — [`scripts/restore-nuget.sh`](../../scripts/restore-nuget.sh) and [`scripts/restore-nuget.ps1`](../../scripts/restore-nuget.ps1) pass credentials in memory via `NuGetPackageSourceCredentials_upstream_proxy` without persisting secrets to disk, and [`SecretRedactor`](../../src/SmartBacklogAssistant/Services/SecretRedactor.cs) redacts `NUGET_AUTH_TOKEN` / `VSS_NUGET_ACCESSTOKEN` from CLI `stderr` diagnostics.
  * **Pre-Production CI Gate:** [`.github/workflows/preprod-nuget-uat.yml`](../../.github/workflows/preprod-nuget-uat.yml) verifies lockfile-cached restores on `ubuntu-latest` and `windows-latest`, simulates poisoned user `NuGet.Config` drift, enables `id-token: write` for OIDC workload identity, and runs UAT smoke checks.

> [!NOTE]
> **Troubleshooting Non-Clean Setups & Lockfiles During UAT ([Issue #57](https://github.com/aleon1220/fde-sharp-backlog/issues/57)):**
> * If `dotnet nuget add source` reports that `nuget.org` already exists or is disabled in your user profile, inspect and re-enable it (or run `--fix-user-config` / `-FixUserConfig`):
>   ```bash
>   dotnet nuget list source
>   dotnet nuget enable source nuget.org
>   ```
> * If a stale HTTP cache or corrupted package folder blocks restore, run `./scripts/restore-nuget.sh --clear-cache` (or `.\scripts\restore-nuget.ps1 -ClearCache`), or manually clear and force-restore:
>   ```bash
>   dotnet nuget locals http-cache --clear
>   NUGET_ENABLE_ENHANCED_HTTP_RETRY=true dotnet restore --configfile nuget.config --locked-mode --force --no-cache
>   ```
> * When intentionally adding or upgrading a NuGet package, regenerate `packages.lock.json` cleanly without `--locked-mode` (`dotnet restore --force-evaluate`) and commit the updated lockfiles.

> [!TIP]
> Builds skip downloading the native Copilot runtime bundle by default (`CopilotSkipCliDownload=true`). Pass `-p:CopilotSkipCliDownload=false` when building/publishing if you want MSBuild to bundle the platform FFI native library (`runtimes/<rid>/native/`) for zero-external-CLI in-process execution.

## Build & Verify Locally for UAT

Runs all unit & integration tests with zero warnings, then checks live workspace readiness:

```bash
dotnet build SmartBacklogAssistant.slnx -c Release -p:TreatWarningsAsErrors=true --no-restore
dotnet test SmartBacklogAssistant.slnx -c Release --no-build
dotnet test --no-restore
dotnet run --project src/SmartBacklogAssistant -- status
```

## AI Provider Credentials

Configure an AI provider key or run offline with `--offline`; see [providers.md#credentials](../configuration/providers.md#credentials).

See also: [install-binary.md](install-binary.md) · [providers.md](../configuration/providers.md) · [environment-variables.md](../configuration/environment-variables.md) · [engineering-principles.md](../project/engineering-principles.md) · [Issue #57](https://github.com/aleon1220/fde-sharp-backlog/issues/57)
