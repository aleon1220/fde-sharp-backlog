[CmdletBinding()]
param(
    [string]$Solution = "",
    [bool]$LockedMode = $true,
    [switch]$NoLockedMode,
    [switch]$FixUserConfig,
    [switch]$ClearCache,
    [switch]$Force,
    [string]$ProxyUrl = "",
    [switch]$ProxyOnly,
    [string]$FallbackDir = "",
    [string]$FallbackPackagesDir = "",
    [switch]$DryRun,
    [switch]$Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..")).Path

if ($Help) {
    @"
Usage: .\scripts\restore-nuget.ps1 [OPTIONS]

Hardened NuGet restore runner for UAT workstations and pre-production pipelines (Issue #57).
Configures enhanced HTTP retries, enforces packages.lock.json determinism (--locked-mode),
optionally repairs dirty user NuGet.Config feeds, and supports upstream caching proxies
with ephemeral token-scoped auth (NUGET_AUTH_TOKEN / VSS_NUGET_ACCESSTOKEN / GITHUB_TOKEN).

Parameters:
  -Solution <path>             Solution or project file (default: SmartBacklogAssistant.slnx)
  -LockedMode <bool>           Enforce packages.lock.json verification (default: `$true)
  -NoLockedMode                Allow updating packages.lock.json during restore
  -FixUserConfig               Register/enable nuget.org in user NuGet.Config if missing/disabled
  -ClearCache                  Clear NuGet HTTP cache and restore with --force --no-cache
  -Force                       Pass --force to dotnet restore
  -ProxyUrl <url>              Upstream NuGet caching proxy feed URL (env: NUGET_PROXY_FEED_URL)
  -ProxyOnly                   Route exclusively through -ProxyUrl without nuget.org fallback
  -FallbackDir <path>          Local .nupkg directory fallback feed (env: NUGET_FALLBACK_FEED_DIR)
  -FallbackPackagesDir <path>  Local expanded fallback packages folder (env: NUGET_FALLBACK_PACKAGES_DIR)
  -DryRun                      Print resolved NuGet restore settings (tokens masked) without restoring
  -Help                        Show this help message
"@ | Write-Host
    exit 0
}

function Mask-Secret {
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value)) {
        return "(not set)"
    }
    if ($Value.Length -le 4) {
        return "****"
    }
    return "****" + $Value.Substring($Value.Length - 4)
}

function Resolve-DotNetExecutable {
    $cmd = Get-Command "dotnet" -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    $candidates = @()
    if ($env:USERPROFILE) {
        $candidates += (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe")
    }
    if ($env:HOME) {
        $candidates += (Join-Path $env:HOME ".dotnet/dotnet")
    }

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw ".NET SDK not found on PATH or in user-scoped .dotnet directory. Install .NET 10 SDK first."
}

$dotnetBin = Resolve-DotNetExecutable
$solutionPath = if ([string]::IsNullOrWhiteSpace($Solution)) { Join-Path $RepoRoot "SmartBacklogAssistant.slnx" } else { $Solution }
$effectiveLockedMode = if ($NoLockedMode) { $false } else { $LockedMode }

$effectiveProxyUrl = if (-not [string]::IsNullOrWhiteSpace($ProxyUrl)) { $ProxyUrl.Trim() } elseif (-not [string]::IsNullOrWhiteSpace($env:NUGET_PROXY_FEED_URL)) { $env:NUGET_PROXY_FEED_URL.Trim() } else { "" }
$effectiveProxyOnly = $ProxyOnly.IsPresent -or ($env:NUGET_PROXY_ONLY -match "^(true|1|yes)$")

if ($effectiveProxyOnly -and [string]::IsNullOrWhiteSpace($effectiveProxyUrl)) {
    throw "-ProxyOnly / NUGET_PROXY_ONLY=true requires -ProxyUrl or NUGET_PROXY_FEED_URL."
}

$allowInsecure = ($env:NUGET_ALLOW_INSECURE_CONNECTIONS -match "^(true|1|yes)$")
if (-not [string]::IsNullOrWhiteSpace($effectiveProxyUrl)) {
    if ($effectiveProxyUrl -match "^https?://[^/@]+@") {
        throw "NUGET_PROXY_FEED_URL must not contain embedded credentials. Supply tokens via NUGET_AUTH_TOKEN or VSS_NUGET_ACCESSTOKEN."
    }

    if ($effectiveProxyUrl.StartsWith("http://", [System.StringComparison]::OrdinalIgnoreCase) -and -not $allowInsecure) {
        throw "Proxy feed URL '$effectiveProxyUrl' uses insecure HTTP. Use HTTPS or set NUGET_ALLOW_INSECURE_CONNECTIONS=true for trusted local proxies."
    }

    if (-not $effectiveProxyUrl.StartsWith("https://", [System.StringComparison]::OrdinalIgnoreCase) -and -not $effectiveProxyUrl.StartsWith("http://", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Proxy feed URL '$effectiveProxyUrl' must start with https:// (or http:// with NUGET_ALLOW_INSECURE_CONNECTIONS=true)."
    }
}

$explicitFallbackDir = -not [string]::IsNullOrWhiteSpace($FallbackDir)
$effectiveFallbackDir = if ($explicitFallbackDir) { $FallbackDir.Trim() } elseif (-not [string]::IsNullOrWhiteSpace($env:NUGET_FALLBACK_FEED_DIR)) { $env:NUGET_FALLBACK_FEED_DIR.Trim() } else { "" }
if (-not [string]::IsNullOrWhiteSpace($effectiveFallbackDir) -and -not (Test-Path $effectiveFallbackDir -PathType Container)) {
    if ($explicitFallbackDir) {
        throw "Fallback feed directory '$effectiveFallbackDir' does not exist."
    }
    Write-Warning "Ignoring non-existent NUGET_FALLBACK_FEED_DIR '$effectiveFallbackDir'."
    $effectiveFallbackDir = ""
}

$explicitFallbackPackages = -not [string]::IsNullOrWhiteSpace($FallbackPackagesDir)
$effectiveFallbackPackages = if ($explicitFallbackPackages) {
    $FallbackPackagesDir.Trim()
} elseif (-not [string]::IsNullOrWhiteSpace($env:NUGET_FALLBACK_PACKAGES_DIR)) {
    $env:NUGET_FALLBACK_PACKAGES_DIR.Trim()
} elseif (Test-Path (Join-Path $RepoRoot ".nuget/fallback") -PathType Container) {
    Join-Path $RepoRoot ".nuget/fallback"
} else {
    ""
}

if (-not [string]::IsNullOrWhiteSpace($effectiveFallbackPackages) -and -not (Test-Path $effectiveFallbackPackages -PathType Container)) {
    if ($explicitFallbackPackages) {
        throw "Fallback packages directory '$effectiveFallbackPackages' does not exist."
    }
    Write-Warning "Ignoring non-existent NUGET_FALLBACK_PACKAGES_DIR '$effectiveFallbackPackages'."
    $effectiveFallbackPackages = ""
}

# Resolve token-scoped / workload-identity auth without persisting static PATs
$authToken = ""
$authSource = "none"
if (-not [string]::IsNullOrWhiteSpace($env:NUGET_AUTH_TOKEN)) {
    $authToken = $env:NUGET_AUTH_TOKEN
    $authSource = "NUGET_AUTH_TOKEN"
} elseif (-not [string]::IsNullOrWhiteSpace($env:VSS_NUGET_ACCESSTOKEN)) {
    $authToken = $env:VSS_NUGET_ACCESSTOKEN
    $authSource = "VSS_NUGET_ACCESSTOKEN"
} elseif (-not [string]::IsNullOrWhiteSpace($effectiveProxyUrl) -and
          $effectiveProxyUrl.StartsWith("https://nuget.pkg.github.com/", [System.StringComparison]::OrdinalIgnoreCase) -and
          -not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
    $authToken = $env:GITHUB_TOKEN
    $authSource = "GITHUB_TOKEN"
}

$authUser = if (-not [string]::IsNullOrWhiteSpace($env:NUGET_AUTH_USER)) {
    $env:NUGET_AUTH_USER
} elseif (-not [string]::IsNullOrWhiteSpace($effectiveProxyUrl) -and $effectiveProxyUrl -like "*nuget.pkg.github.com*") {
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_ACTOR)) { $env:GITHUB_ACTOR } else { "x-access-token" }
} else {
    "azdo"
}

# Standardize NuGet HTTP retry settings for transient network/429/503 errors (Issue #57)
if ([string]::IsNullOrWhiteSpace($env:NUGET_ENABLE_ENHANCED_HTTP_RETRY)) { $env:NUGET_ENABLE_ENHANCED_HTTP_RETRY = "true" }
if ([string]::IsNullOrWhiteSpace($env:NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT)) { $env:NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT = "6" }
if ([string]::IsNullOrWhiteSpace($env:NUGET_ENHANCED_NETWORK_RETRY_DELAY_MILLISECONDS)) { $env:NUGET_ENHANCED_NETWORK_RETRY_DELAY_MILLISECONDS = "1000" }
if ([string]::IsNullOrWhiteSpace($env:NUGET_RETRY_HTTP_429)) { $env:NUGET_RETRY_HTTP_429 = "true" }
if ([string]::IsNullOrWhiteSpace($env:NUGET_OBSERVE_RETRY_AFTER)) { $env:NUGET_OBSERVE_RETRY_AFTER = "true" }
if ([string]::IsNullOrWhiteSpace($env:DOTNET_CLI_TELEMETRY_OPTOUT)) { $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1" }

if ($FixUserConfig) {
    Write-Host "Checking global user NuGet.Config for nuget.org registration..."
    $tempInspectDir = [System.IO.Path]::GetTempPath()
    Push-Location $tempInspectDir
    $prevEap = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $userSources = (& $dotnetBin nuget list source 2>&1 | Out-String)
        $ErrorActionPreference = $prevEap
        if ($userSources -notmatch [regex]::Escape("https://api.nuget.org/v3/index.json")) {
            if ($DryRun) {
                Write-Host "[dry-run] Would run: dotnet nuget add source https://api.nuget.org/v3/index.json --name nuget.org"
            } else {
                & $dotnetBin nuget add source https://api.nuget.org/v3/index.json --name nuget.org
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                $ErrorActionPreference = "Continue"
                $userSources = (& $dotnetBin nuget list source 2>&1 | Out-String)
                $ErrorActionPreference = $prevEap
            }
        }

        if ($userSources -match "nuget\.org \[Disabled\]") {
            if ($DryRun) {
                Write-Host "[dry-run] Would run: dotnet nuget enable source nuget.org"
            } else {
                & $dotnetBin nuget enable source nuget.org
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            }
        } elseif ($userSources -match [regex]::Escape("https://api.nuget.org/v3/index.json")) {
            Write-Host "Global user NuGet.Config already has nuget.org enabled."
        }
    } finally {
        $ErrorActionPreference = $prevEap
        Pop-Location
    }
}

$tmpConfigPath = $null
$configFile = Join-Path $RepoRoot "nuget.config"
$usingManagedConfig = $false

try {
    if (-not [string]::IsNullOrWhiteSpace($effectiveProxyUrl) -or
        -not [string]::IsNullOrWhiteSpace($effectiveFallbackDir) -or
        -not [string]::IsNullOrWhiteSpace($effectiveFallbackPackages)) {
        $usingManagedConfig = $true
        $tmpConfigPath = Join-Path ([System.IO.Path]::GetTempPath()) ("sba-nuget-" + [System.Guid]::NewGuid().ToString("N") + ".config")

        $xmlLines = New-Object System.Collections.Generic.List[string]
        $xmlLines.Add('<?xml version="1.0" encoding="utf-8"?>')
        $xmlLines.Add('<configuration>')
        $xmlLines.Add('  <packageSources>')
        $xmlLines.Add('    <clear />')
        if (-not [string]::IsNullOrWhiteSpace($effectiveProxyUrl)) {
            $escapedProxy = [System.Security.SecurityElement]::Escape($effectiveProxyUrl)
            if ($allowInsecure -and $effectiveProxyUrl.StartsWith("http://", [System.StringComparison]::OrdinalIgnoreCase)) {
                $xmlLines.Add("    <add key=`"upstream_proxy`" value=`"$escapedProxy`" protocolVersion=`"3`" allowInsecureConnections=`"true`" />")
            } else {
                $xmlLines.Add("    <add key=`"upstream_proxy`" value=`"$escapedProxy`" protocolVersion=`"3`" />")
            }
        }
        if (-not $effectiveProxyOnly) {
            $xmlLines.Add('    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />')
        }
        if (-not [string]::IsNullOrWhiteSpace($effectiveFallbackDir)) {
            $escapedFallback = [System.Security.SecurityElement]::Escape($effectiveFallbackDir)
            $xmlLines.Add("    <add key=`"local_fallback`" value=`"$escapedFallback`" />")
        }
        $xmlLines.Add('  </packageSources>')
        $xmlLines.Add('  <disabledPackageSources>')
        $xmlLines.Add('    <clear />')
        $xmlLines.Add('  </disabledPackageSources>')
        if (-not [string]::IsNullOrWhiteSpace($effectiveFallbackPackages)) {
            $escapedFallbackPkgs = [System.Security.SecurityElement]::Escape($effectiveFallbackPackages)
            $xmlLines.Add('  <fallbackPackageFolders>')
            $xmlLines.Add("    <add key=`"local_fallback_cache`" value=`"$escapedFallbackPkgs`" />")
            $xmlLines.Add('  </fallbackPackageFolders>')
        }
        $xmlLines.Add('  <packageRestore>')
        $xmlLines.Add('    <add key="enabled" value="True" />')
        $xmlLines.Add('    <add key="automatic" value="True" />')
        $xmlLines.Add('  </packageRestore>')
        $xmlLines.Add('</configuration>')

        [System.IO.File]::WriteAllLines($tmpConfigPath, $xmlLines, [System.Text.UTF8Encoding]::new($false))
        $configFile = $tmpConfigPath
    }

    $restoreArgs = New-Object System.Collections.Generic.List[string]
    $restoreArgs.Add("restore")
    $restoreArgs.Add($solutionPath)
    $restoreArgs.Add("--configfile")
    $restoreArgs.Add($configFile)
    if ($effectiveLockedMode) {
        $restoreArgs.Add("--locked-mode")
    }
    if ($ClearCache) {
        $restoreArgs.Add("--force")
        $restoreArgs.Add("--no-cache")
    } elseif ($Force) {
        $restoreArgs.Add("--force")
    }
    if ($usingManagedConfig) {
        $restoreArgs.Add("-p:SBA_NUGET_CONFIG_MANAGED=true")
    }

    $dotnetVersion = (& $dotnetBin --version)
    Write-Host "NuGet Hardened Restore Summary:"
    Write-Host "  dotnet          : $dotnetBin ($dotnetVersion)"
    Write-Host "  solution        : $solutionPath"
    Write-Host "  configfile      : $configFile"
    Write-Host "  locked-mode     : $effectiveLockedMode"
    Write-Host "  http-retry      : $($env:NUGET_ENABLE_ENHANCED_HTTP_RETRY) (maxTries=$($env:NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT), delayMs=$($env:NUGET_ENHANCED_NETWORK_RETRY_DELAY_MILLISECONDS), retry429=$($env:NUGET_RETRY_HTTP_429))"
    Write-Host "  proxy-feed      : $(if ($effectiveProxyUrl) { $effectiveProxyUrl } else { '(none)' })$(if ($effectiveProxyOnly) { ' (proxy-only=true)' } else { '' })"
    Write-Host "  auth-token      : $(Mask-Secret $authToken) (source=$authSource, user=$authUser)"
    Write-Host "  fallback-feed   : $(if ($effectiveFallbackDir) { $effectiveFallbackDir } else { '(none)' })"
    Write-Host "  fallback-cache  : $(if ($effectiveFallbackPackages) { $effectiveFallbackPackages } else { '(none)' })"

    if ($DryRun) {
        Write-Host "[dry-run] Restore command: $dotnetBin $($restoreArgs -join ' ')"
        exit 0
    }

    if ($ClearCache) {
        & $dotnetBin nuget locals http-cache --clear
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    $hadCredVar = Test-Path Env:NuGetPackageSourceCredentials_upstream_proxy
    $prevCredVar = if ($hadCredVar) { $env:NuGetPackageSourceCredentials_upstream_proxy } else { $null }
    try {
        if (-not [string]::IsNullOrWhiteSpace($effectiveProxyUrl) -and -not [string]::IsNullOrWhiteSpace($authToken)) {
            $env:NuGetPackageSourceCredentials_upstream_proxy = "Username=$authUser;Password=$authToken"
        }

        & $dotnetBin @restoreArgs
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    } finally {
        if ($hadCredVar) {
            $env:NuGetPackageSourceCredentials_upstream_proxy = $prevCredVar
        } else {
            Remove-Item Env:NuGetPackageSourceCredentials_upstream_proxy -ErrorAction SilentlyContinue
        }
    }
} finally {
    if ($tmpConfigPath -and (Test-Path $tmpConfigPath)) {
        Remove-Item -Path $tmpConfigPath -Force -ErrorAction SilentlyContinue
    }
}
