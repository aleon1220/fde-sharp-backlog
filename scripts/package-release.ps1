[CmdletBinding()]
param(
    [string]$Rid = "win-x64",
    [string]$Version = "",
    [string]$OutputDir = "",
    [switch]$SkipSmoke,
    [switch]$Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..")).Path
$ProjectPath = Join-Path $RepoRoot "src\SmartBacklogAssistant\SmartBacklogAssistant.csproj"

if ($Help) {
    @"
Usage: .\scripts\package-release.ps1 [OPTIONS]

Publishes a self-contained single-file SmartBacklogAssistant Windows release archive,
writes its SHA-256 checksum, and runs out-of-repo smoke verification (Issue #59).

Parameters:
  -Rid <rid>            Target RuntimeIdentifier (default: win-x64)
  -Version <semver>     Release version (e.g., 1.1.0 or v1.1.0; default: from .csproj)
  -OutputDir <dir>      Destination directory for archive & checksum (default: artifacts\release)
  -SkipSmoke            Skip out-of-repo binary parity smoke checks
  -Help                 Show this help message
"@ | Write-Host
    exit 0
}

function Resolve-DotNetExecutable {
    $cmd = Get-Command "dotnet" -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    if ($env:USERPROFILE) {
        $candidate = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
        if (Test-Path $candidate) { return $candidate }
    }
    throw ".NET 10 SDK not found on PATH or under $env:USERPROFILE\.dotnet."
}

$dotnetBin = Resolve-DotNetExecutable

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$csproj = Get-Content -Path $ProjectPath -Raw
    $versionNode = $csproj.SelectSingleNode("//Version")
    if (-not $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        throw "Unable to resolve <Version> from '$ProjectPath'."
    }
    $Version = $versionNode.InnerText.Trim()
}

$cleanVersion = $Version.TrimStart('v')
if ($cleanVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$') {
    throw "Invalid semantic version '$Version'. Expected formats like 1.1.0, v1.1.0, or v1.2.0-rc.1."
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot "artifacts\release"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$resolvedOutputDir = (Resolve-Path $OutputDir).Path

if ([string]::IsNullOrWhiteSpace($env:NUGET_ENABLE_ENHANCED_HTTP_RETRY)) { $env:NUGET_ENABLE_ENHANCED_HTTP_RETRY = "true" }
if ([string]::IsNullOrWhiteSpace($env:NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT)) { $env:NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT = "6" }
if ([string]::IsNullOrWhiteSpace($env:NUGET_ENHANCED_NETWORK_RETRY_DELAY_MILLISECONDS)) { $env:NUGET_ENHANCED_NETWORK_RETRY_DELAY_MILLISECONDS = "1000" }
if ([string]::IsNullOrWhiteSpace($env:NUGET_RETRY_HTTP_429)) { $env:NUGET_RETRY_HTTP_429 = "true" }
if ([string]::IsNullOrWhiteSpace($env:NUGET_OBSERVE_RETRY_AFTER)) { $env:NUGET_OBSERVE_RETRY_AFTER = "true" }
if ([string]::IsNullOrWhiteSpace($env:DOTNET_CLI_TELEMETRY_OPTOUT)) { $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1" }

$packageName = "SmartBacklogAssistant-v$cleanVersion-$Rid"
$archiveName = "$packageName.zip"
$archivePath = Join-Path $resolvedOutputDir $archiveName

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("sba-release-" + [System.Guid]::NewGuid().ToString("N"))
$publishDir = Join-Path $workDir "publish"
$stageParent = Join-Path $workDir "stage"
$stageDir = Join-Path $stageParent $packageName
New-Item -ItemType Directory -Force -Path $publishDir, $stageDir | Out-Null

try {
    $dotnetVersion = (& $dotnetBin --version)
    Write-Host "Packaging SmartBacklogAssistant release artifact:"
    Write-Host "  dotnet     : $dotnetBin ($dotnetVersion)"
    Write-Host "  version    : v$cleanVersion"
    Write-Host "  rid        : $Rid"
    Write-Host "  output-dir : $resolvedOutputDir"

    $ridLockFile = Join-Path $workDir "packages.$Rid.lock.json"

    & $dotnetBin restore $ProjectPath -r $Rid --configfile (Join-Path $RepoRoot "nuget.config") "-p:NuGetLockFilePath=$ridLockFile" "-p:RestoreLockedMode=false"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $dotnetBin publish $ProjectPath `
        -c Release `
        -r $Rid `
        --self-contained true `
        --no-restore `
        "-p:NuGetLockFilePath=$ridLockFile" `
        "-p:PublishSingleFile=true" `
        "-p:IncludeNativeLibrariesForSelfExtract=true" `
        "-p:PublishTrimmed=false" `
        "-p:DebugType=None" `
        "-p:DebugSymbols=false" `
        "-p:Version=$cleanVersion" `
        "-p:InformationalVersion=$cleanVersion" `
        "-p:TreatWarningsAsErrors=true" `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $exeName = if ($Rid -like "win-*") { "SmartBacklogAssistant.exe" } else { "SmartBacklogAssistant" }
    $publishedExe = Join-Path $publishDir $exeName
    if (-not (Test-Path $publishedExe)) {
        throw "Expected published binary '$publishedExe' was not produced."
    }

    Copy-Item -Path $publishedExe -Destination (Join-Path $stageDir $exeName)
    Copy-Item -Path (Join-Path $RepoRoot "prompts") -Destination (Join-Path $stageDir "prompts") -Recurse
    New-Item -ItemType Directory -Force -Path (Join-Path $stageDir "samples") | Out-Null
    Copy-Item -Path (Join-Path $RepoRoot "samples\raw-notes.txt") -Destination (Join-Path $stageDir "samples\raw-notes.txt")
    Copy-Item -Path (Join-Path $RepoRoot "README.md") -Destination (Join-Path $stageDir "README.md")
    Copy-Item -Path (Join-Path $RepoRoot "docs") -Destination (Join-Path $stageDir "docs") -Recurse
    Copy-Item -Path (Join-Path $RepoRoot "LICENSE") -Destination (Join-Path $stageDir "LICENSE")

    if (Test-Path $archivePath) { Remove-Item $archivePath -Force }
    Compress-Archive -Path $stageDir -DestinationPath $archivePath -CompressionLevel Optimal

    $hashHex = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$archivePath.sha256", "$hashHex  $archiveName`n", [System.Text.UTF8Encoding]::new($false))

    Write-Host "Created archive : $archivePath"
    Write-Host "SHA-256         : $hashHex"

    if ($SkipSmoke) {
        Write-Host "Skipping smoke verification (-SkipSmoke)."
        exit 0
    }

    Write-Host "Running out-of-repo packaged & standalone binary smoke verification..."

    $smokeArchiveDir = Join-Path $workDir "smoke-archive"
    $smokeArchiveWs = Join-Path $workDir "smoke-archive-ws"
    New-Item -ItemType Directory -Force -Path $smokeArchiveDir, $smokeArchiveWs | Out-Null

    $actualHash = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $hashHex) {
        throw "Archive checksum mismatch ($actualHash != $hashHex)."
    }

    Expand-Archive -Path $archivePath -DestinationPath $smokeArchiveDir -Force
    $extractedDir = Join-Path $smokeArchiveDir $packageName
    $extractedExe = Join-Path $extractedDir $exeName

    Push-Location $smokeArchiveDir
    try {
        $verOut = (& $extractedExe --version | Out-String)
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        Write-Host "  archive --version : $($verOut.Trim())"
        if ($verOut -notlike "*v$cleanVersion*") { throw "Expected v$cleanVersion in --version output: $verOut" }

        $helpOut = (& $extractedExe --help | Out-String)
        if ($LASTEXITCODE -ne 0 -or $helpOut -notlike "*SUBCOMMANDS*") { throw "--help smoke check failed." }

        $statusOut = (& $extractedExe -w $smokeArchiveWs status | Out-String)
        if ($LASTEXITCODE -ne 0 -or $statusOut -notlike "*Workspace & AI Status*") { throw "status smoke check failed." }

        $draftOut = (& $extractedExe -w $smokeArchiveWs draft "Verify Extracted Release Binary" --type 01 --preview --offline --non-interactive | Out-String)
        if ($LASTEXITCODE -ne 0 -or $draftOut -notlike "*complete (Type01)*") { throw "draft smoke check failed." }
    } finally {
        Pop-Location
    }

    # Verify standalone single-file binary copied into a bare directory without external prompts/
    $smokeBareDir = Join-Path $workDir "smoke-bare"
    $smokeBareWs = Join-Path $workDir "smoke-bare-ws"
    New-Item -ItemType Directory -Force -Path $smokeBareDir, $smokeBareWs | Out-Null
    $bareBinName = if ($Rid -like "win-*") { "backlog.exe" } else { "backlog" }
    $bareBin = Join-Path $smokeBareDir $bareBinName
    Copy-Item -Path $extractedExe -Destination $bareBin

    $notePath = Join-Path $workDir "standalone-notes.txt"
    @(
        "Discovery Note - Standalone Smoke"
        "Requirement: As an operator, I want standalone binary execution so that no SDK build is needed."
        "Precondition: Binary is on PATH."
        "Action: Run process."
        "Expected Outcome: Outcome renders cleanly."
    ) | Set-Content -Path $notePath -Encoding UTF8

    Push-Location $workDir
    try {
        $bareStatus = (& $bareBin -w $smokeBareWs status | Out-String)
        if ($LASTEXITCODE -ne 0 -or $bareStatus -notlike "*(bundled in binary)*") {
            throw "Expected '(bundled in binary)' in standalone status output. Output:`n$bareStatus"
        }

        $bareDraft1 = (& $bareBin -w $smokeBareWs draft "Verify Standalone Bundled Prompts" --type 01 --preview --offline --non-interactive | Out-String)
        if ($LASTEXITCODE -ne 0 -or $bareDraft1 -notlike "*complete (Type01)*") { throw "Standalone Type01 draft failed." }

        $bareDraft2 = (& $bareBin -w $smokeBareWs draft "Verify Standalone Type02 Schedule" --type 02 --preview --offline --non-interactive | Out-String)
        if ($LASTEXITCODE -ne 0 -or $bareDraft2 -notlike "*schedule (Type02)*") { throw "Standalone Type02 draft failed." }

        $bareProcess = (& $bareBin -w $smokeBareWs process -i $notePath --dry-run --offline --non-interactive | Out-String)
        if ($LASTEXITCODE -ne 0 -or $bareProcess -notlike "*Standalone Smoke*") { throw "Standalone process dry-run failed." }
    } finally {
        Pop-Location
    }

    Write-Host "Smoke verification passed for extracted archive and standalone single-file binary!"
} finally {
    Remove-Item -Path $workDir -Recurse -Force -ErrorAction SilentlyContinue
}
