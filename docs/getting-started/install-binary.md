# Install & Run from Tested GitHub Binary

Download, verify, and run pre-built self-contained single-file binaries for `SmartBacklogAssistant` without installing the `.NET SDK` or cloning the repository.

## Install & Run from Tested GitHub Binary

Pre-built, self-contained single-file binaries are published by [`.github/workflows/release-binaries.yml`](../../.github/workflows/release-binaries.yml) for `linux-x64`, `win-x64`, and `osx-arm64` **only after** all hardened NuGet restores, Release `-warnaserror` builds, unit/integration tests, and out-of-repo standalone binary smoke checks pass green. Built-in `prompts/*.md` templates are embedded inside the executable, so no `.NET SDK` or git checkout is required.

### 1. Verify Green CI Checks & Latest Release

```bash
# Confirm latest published releases and check that the release workflow passed green
gh release list --repo aleon1220/fde-sharp-backlog --limit 5
gh run list --repo aleon1220/fde-sharp-backlog --workflow release-binaries.yml --limit 5
```

Or open the repository **Releases** and **Actions** tabs on GitHub and confirm the matching tag run (`vX.Y.Z`) displays a green checkmark before downloading.

### 2. Download, Verify SHA-256 Checksum & Install

#### Linux / WSL2 (`linux-x64`) & macOS (`osx-arm64`)

```bash
# 1. Download latest green-checked release archive + checksums (set RID=osx-arm64 on Apple Silicon)
RID="linux-x64"   # or "osx-arm64"
TAG="$(gh release view --repo aleon1220/fde-sharp-backlog --json tagName --jq .tagName)"
mkdir -p /tmp/sba-install && cd /tmp/sba-install
gh release download "$TAG" --repo aleon1220/fde-sharp-backlog \
  --pattern "SmartBacklogAssistant-${TAG}-${RID}.tar.gz" \
  --pattern "SHA256SUMS.txt"

# Alternative without gh CLI (curl):
# curl -fsSLO "https://github.com/aleon1220/fde-sharp-backlog/releases/download/${TAG}/SmartBacklogAssistant-${TAG}-${RID}.tar.gz"
# curl -fsSLO "https://github.com/aleon1220/fde-sharp-backlog/releases/download/${TAG}/SHA256SUMS.txt"

# 2. Verify SHA-256 checksum (use 'shasum -a 256 -c' on macOS) and optional SLSA provenance
grep "SmartBacklogAssistant-${TAG}-${RID}.tar.gz" SHA256SUMS.txt | sha256sum -c
gh attestation verify "SmartBacklogAssistant-${TAG}-${RID}.tar.gz" --repo aleon1220/fde-sharp-backlog

# 3. Extract and install onto PATH (exposes both 'SmartBacklogAssistant' and 'backlog')
tar -xzf "SmartBacklogAssistant-${TAG}-${RID}.tar.gz"
mkdir -p "$HOME/.local/bin"
install -m 0755 "SmartBacklogAssistant-${TAG}-${RID}/SmartBacklogAssistant" "$HOME/.local/bin/SmartBacklogAssistant"
ln -sf "$HOME/.local/bin/SmartBacklogAssistant" "$HOME/.local/bin/backlog"
export PATH="$HOME/.local/bin:$PATH"   # persist in ~/.bashrc or ~/.zshrc if not already on PATH
```

#### Windows PowerShell (`win-x64`)

```powershell
# 1. Download latest green-checked Windows release archive + SHA256SUMS.txt
$Tag = gh release view --repo aleon1220/fde-sharp-backlog --json tagName --jq .tagName
$Asset = "SmartBacklogAssistant-$Tag-win-x64.zip"
$InstallDir = Join-Path $env:LOCALAPPDATA "Programs\SmartBacklogAssistant"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

# Using gh CLI (or Invoke-RestMethod / irm from github.com/.../releases/download/$Tag/$Asset):
gh release download $Tag --repo aleon1220/fde-sharp-backlog --dir $env:TEMP --pattern $Asset --pattern "SHA256SUMS.txt"

# Alternative without gh CLI (Invoke-RestMethod):
# $Latest = Invoke-RestMethod "https://api.github.com/repos/aleon1220/fde-sharp-backlog/releases/latest"
# $Tag = $Latest.tag_name; $Asset = "SmartBacklogAssistant-$Tag-win-x64.zip"
# Invoke-RestMethod "https://github.com/aleon1220/fde-sharp-backlog/releases/download/$Tag/$Asset" -OutFile (Join-Path $env:TEMP $Asset)
# Invoke-RestMethod "https://github.com/aleon1220/fde-sharp-backlog/releases/download/$Tag/SHA256SUMS.txt" -OutFile (Join-Path $env:TEMP "SHA256SUMS.txt")

# 2. Verify SHA-256 checksum
$Expected = (Select-String -Path (Join-Path $env:TEMP "SHA256SUMS.txt") -Pattern ([regex]::Escape($Asset))).Line.Split()[0].ToLowerInvariant()
$Actual = (Get-FileHash (Join-Path $env:TEMP $Asset) -Algorithm SHA256).Hash.ToLowerInvariant()
if ($Actual -ne $Expected) { throw "SHA-256 mismatch for $Asset" }

# 3. Extract, add to User PATH, and expose 'backlog' convenience copy
Expand-Archive (Join-Path $env:TEMP $Asset) -DestinationPath $InstallDir -Force
$BinDir = Join-Path $InstallDir "SmartBacklogAssistant-$Tag-win-x64"
Copy-Item (Join-Path $BinDir "SmartBacklogAssistant.exe") (Join-Path $BinDir "backlog.exe") -Force
$UserPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($UserPath -notlike "*$BinDir*") {
    [Environment]::SetEnvironmentVariable("Path", "$UserPath;$BinDir", "User")
    $env:Path = "$env:Path;$BinDir"
}
```

### 3. Run Standalone & Upgrade Cleanly

```bash
# Run from any standalone directory outside a git checkout
cd ~
backlog --version
backlog --help
backlog status
backlog draft "Add API Rate Limiting" --type 01 --preview --offline --non-interactive

# Launch interactive REPL / Wizard TUI
backlog
```

* **Upgrading:** Re-run the download + checksum + extract/overwrite steps above for a newer tag `vX.Y.Z`; workspace `settings.json`, `./in`, and `./out` remain preserved in your workspace root.
* **Publishing a Release (Maintainers):** See [releases.md](../operations/releases.md) for tag-driven publishing and local packaging dry-runs.

See also: [build-from-source.md](build-from-source.md) · [cli.md](../usage/cli.md) · [releases.md](../operations/releases.md) · [Issue #59](https://github.com/aleon1220/fde-sharp-backlog/issues/59)
