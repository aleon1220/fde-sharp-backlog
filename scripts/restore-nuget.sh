#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"

SOLUTION_PATH="$REPO_ROOT/SmartBacklogAssistant.slnx"
LOCKED_MODE="true"
FIX_USER_CONFIG="false"
CLEAR_CACHE="false"
FORCE_RESTORE="false"
DRY_RUN="false"

PROXY_URL="${NUGET_PROXY_FEED_URL:-}"
PROXY_ONLY="${NUGET_PROXY_ONLY:-false}"
FALLBACK_DIR="${NUGET_FALLBACK_FEED_DIR:-}"
FALLBACK_PACKAGES_DIR="${NUGET_FALLBACK_PACKAGES_DIR:-}"
EXPLICIT_FALLBACK_DIR="false"
EXPLICIT_FALLBACK_PACKAGES="false"

usage() {
  cat <<'EOF'
Usage: ./scripts/restore-nuget.sh [OPTIONS]

Hardened NuGet restore runner for UAT workstations and pre-production pipelines (Issue #57).
Configures enhanced HTTP retries, enforces packages.lock.json determinism (--locked-mode),
optionally repairs dirty user NuGet.Config feeds, and supports upstream caching proxies
with ephemeral token-scoped auth (NUGET_AUTH_TOKEN / VSS_NUGET_ACCESSTOKEN / GITHUB_TOKEN).

Options:
  --solution <path>           Solution or project file (default: SmartBacklogAssistant.slnx)
  --locked-mode               Enforce packages.lock.json verification (default)
  --no-locked-mode            Allow updating packages.lock.json during restore
  --fix-user-config           Register/enable nuget.org in user NuGet.Config if missing/disabled
  --clear-cache               Clear NuGet HTTP cache and restore with --force --no-cache
  --force                     Pass --force to dotnet restore
  --proxy-url <url>           Upstream NuGet caching proxy feed URL (env: NUGET_PROXY_FEED_URL)
  --proxy-only                Route exclusively through --proxy-url without nuget.org fallback
  --fallback-dir <path>       Local .nupkg directory fallback feed (env: NUGET_FALLBACK_FEED_DIR)
  --fallback-packages <path>  Local expanded fallback packages folder (env: NUGET_FALLBACK_PACKAGES_DIR)
  --dry-run                   Print resolved NuGet restore settings (tokens masked) without restoring
  -h, --help                  Show this help message
EOF
}

mask_secret() {
  local value="${1:-}"
  if [[ -z "$value" ]]; then
    echo "(not set)"
    return
  fi

  local len=${#value}
  if (( len <= 4 )); then
    echo "****"
  else
    echo "****${value: -4}"
  fi
}

xml_escape() {
  local s="$1"
  s="${s//&/&amp;}"
  s="${s//</&lt;}"
  s="${s//>/&gt;}"
  s="${s//\"/&quot;}"
  s="${s//\'/&apos;}"
  printf '%s' "$s"
}

resolve_dotnet() {
  if command -v dotnet >/dev/null 2>&1; then
    command -v dotnet
    return 0
  fi

  if [[ -x "${HOME:-}/.dotnet/dotnet" ]]; then
    printf '%s\n' "${HOME}/.dotnet/dotnet"
    return 0
  fi

  echo "error: .NET SDK not found on PATH or at \$HOME/.dotnet/dotnet. Install .NET 10 SDK first." >&2
  return 1
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --solution)
      [[ $# -ge 2 ]] || { echo "error: --solution requires a path argument" >&2; exit 2; }
      SOLUTION_PATH="$2"
      shift 2
      ;;
    --locked-mode)
      LOCKED_MODE="true"
      shift
      ;;
    --no-locked-mode)
      LOCKED_MODE="false"
      shift
      ;;
    --fix-user-config)
      FIX_USER_CONFIG="true"
      shift
      ;;
    --clear-cache)
      CLEAR_CACHE="true"
      shift
      ;;
    --force)
      FORCE_RESTORE="true"
      shift
      ;;
    --proxy-url)
      [[ $# -ge 2 ]] || { echo "error: --proxy-url requires a URL argument" >&2; exit 2; }
      PROXY_URL="$2"
      shift 2
      ;;
    --proxy-only)
      PROXY_ONLY="true"
      shift
      ;;
    --fallback-dir)
      [[ $# -ge 2 ]] || { echo "error: --fallback-dir requires a directory path" >&2; exit 2; }
      FALLBACK_DIR="$2"
      EXPLICIT_FALLBACK_DIR="true"
      shift 2
      ;;
    --fallback-packages)
      [[ $# -ge 2 ]] || { echo "error: --fallback-packages requires a directory path" >&2; exit 2; }
      FALLBACK_PACKAGES_DIR="$2"
      EXPLICIT_FALLBACK_PACKAGES="true"
      shift 2
      ;;
    --dry-run)
      DRY_RUN="true"
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "error: Unknown option '$1'" >&2
      usage >&2
      exit 2
      ;;
  esac
done

DOTNET_BIN="$(resolve_dotnet)"

# Normalize boolean flags
PROXY_ONLY_LOWER="$(printf '%s' "$PROXY_ONLY" | tr '[:upper:]' '[:lower:]')"
if [[ "$PROXY_ONLY_LOWER" == "true" || "$PROXY_ONLY_LOWER" == "1" || "$PROXY_ONLY_LOWER" == "yes" ]]; then
  PROXY_ONLY="true"
else
  PROXY_ONLY="false"
fi

if [[ "$PROXY_ONLY" == "true" && -z "$PROXY_URL" ]]; then
  echo "error: --proxy-only / NUGET_PROXY_ONLY=true requires --proxy-url or NUGET_PROXY_FEED_URL." >&2
  exit 2
fi

ALLOW_INSECURE_LOWER="$(printf '%s' "${NUGET_ALLOW_INSECURE_CONNECTIONS:-false}" | tr '[:upper:]' '[:lower:]')"
ALLOW_INSECURE="false"
if [[ "$ALLOW_INSECURE_LOWER" == "true" || "$ALLOW_INSECURE_LOWER" == "1" ]]; then
  ALLOW_INSECURE="true"
fi

if [[ -n "$PROXY_URL" ]]; then
  if [[ "$PROXY_URL" =~ ^https?://[^/@]+@ ]]; then
    echo "error: NUGET_PROXY_FEED_URL must not contain embedded credentials. Supply tokens via NUGET_AUTH_TOKEN or VSS_NUGET_ACCESSTOKEN." >&2
    exit 2
  fi

  if [[ "$PROXY_URL" == http://* && "$ALLOW_INSECURE" != "true" ]]; then
    echo "error: Proxy feed URL '$PROXY_URL' uses insecure HTTP. Use HTTPS or set NUGET_ALLOW_INSECURE_CONNECTIONS=true for trusted local proxies." >&2
    exit 2
  fi

  if [[ "$PROXY_URL" != https://* && "$PROXY_URL" != http://* ]]; then
    echo "error: Proxy feed URL '$PROXY_URL' must start with https:// (or http:// with NUGET_ALLOW_INSECURE_CONNECTIONS=true)." >&2
    exit 2
  fi
fi

# Validate fallback directories
if [[ -n "$FALLBACK_DIR" && ! -d "$FALLBACK_DIR" ]]; then
  if [[ "$EXPLICIT_FALLBACK_DIR" == "true" ]]; then
    echo "error: Fallback feed directory '$FALLBACK_DIR' does not exist." >&2
    exit 2
  fi
  echo "warning: Ignoring non-existent NUGET_FALLBACK_FEED_DIR '$FALLBACK_DIR'." >&2
  FALLBACK_DIR=""
fi

if [[ -z "$FALLBACK_PACKAGES_DIR" && -d "$REPO_ROOT/.nuget/fallback" ]]; then
  FALLBACK_PACKAGES_DIR="$REPO_ROOT/.nuget/fallback"
fi

if [[ -n "$FALLBACK_PACKAGES_DIR" && ! -d "$FALLBACK_PACKAGES_DIR" ]]; then
  if [[ "$EXPLICIT_FALLBACK_PACKAGES" == "true" ]]; then
    echo "error: Fallback packages directory '$FALLBACK_PACKAGES_DIR' does not exist." >&2
    exit 2
  fi
  echo "warning: Ignoring non-existent NUGET_FALLBACK_PACKAGES_DIR '$FALLBACK_PACKAGES_DIR'." >&2
  FALLBACK_PACKAGES_DIR=""
fi

# Resolve token-scoped / workload-identity authentication without writing static PATs to disk
AUTH_TOKEN="${NUGET_AUTH_TOKEN:-${VSS_NUGET_ACCESSTOKEN:-}}"
AUTH_SOURCE="none"
if [[ -n "${NUGET_AUTH_TOKEN:-}" ]]; then
  AUTH_SOURCE="NUGET_AUTH_TOKEN"
elif [[ -n "${VSS_NUGET_ACCESSTOKEN:-}" ]]; then
  AUTH_SOURCE="VSS_NUGET_ACCESSTOKEN"
elif [[ -n "$PROXY_URL" && "$PROXY_URL" == https://nuget.pkg.github.com/* && -n "${GITHUB_TOKEN:-}" ]]; then
  AUTH_TOKEN="$GITHUB_TOKEN"
  AUTH_SOURCE="GITHUB_TOKEN"
fi

AUTH_USER="${NUGET_AUTH_USER:-}"
if [[ -z "$AUTH_USER" ]]; then
  if [[ -n "$PROXY_URL" && "$PROXY_URL" == *nuget.pkg.github.com* ]]; then
    AUTH_USER="${GITHUB_ACTOR:-x-access-token}"
  else
    AUTH_USER="azdo"
  fi
fi

# Standardize NuGet HTTP retry resilience for transient errors (Issue #57)
export NUGET_ENABLE_ENHANCED_HTTP_RETRY="${NUGET_ENABLE_ENHANCED_HTTP_RETRY:-true}"
export NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT="${NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT:-6}"
export NUGET_ENHANCED_NETWORK_RETRY_DELAY_MILLISECONDS="${NUGET_ENHANCED_NETWORK_RETRY_DELAY_MILLISECONDS:-1000}"
export NUGET_RETRY_HTTP_429="${NUGET_RETRY_HTTP_429:-true}"
export NUGET_OBSERVE_RETRY_AFTER="${NUGET_OBSERVE_RETRY_AFTER:-true}"
export DOTNET_CLI_TELEMETRY_OPTOUT="${DOTNET_CLI_TELEMETRY_OPTOUT:-1}"

if [[ "$FIX_USER_CONFIG" == "true" ]]; then
  echo "Checking global user NuGet.Config for nuget.org registration..."
  USER_SOURCES_OUTPUT="$(cd /tmp && "$DOTNET_BIN" nuget list source 2>&1 || true)"
  if ! printf '%s\n' "$USER_SOURCES_OUTPUT" | grep -Fq "https://api.nuget.org/v3/index.json"; then
    if [[ "$DRY_RUN" == "true" ]]; then
      echo "[dry-run] Would run: dotnet nuget add source https://api.nuget.org/v3/index.json --name nuget.org"
    else
      (cd /tmp && "$DOTNET_BIN" nuget add source https://api.nuget.org/v3/index.json --name nuget.org)
      USER_SOURCES_OUTPUT="$(cd /tmp && "$DOTNET_BIN" nuget list source 2>&1 || true)"
    fi
  fi

  if printf '%s\n' "$USER_SOURCES_OUTPUT" | grep -Eq "nuget\.org \[Disabled\]"; then
    if [[ "$DRY_RUN" == "true" ]]; then
      echo "[dry-run] Would run: dotnet nuget enable source nuget.org"
    else
      (cd /tmp && "$DOTNET_BIN" nuget enable source nuget.org)
    fi
  elif printf '%s\n' "$USER_SOURCES_OUTPUT" | grep -Fq "https://api.nuget.org/v3/index.json"; then
    echo "Global user NuGet.Config already has nuget.org enabled."
  fi
fi

TMP_NUGET_CONFIG=""
cleanup() {
  if [[ -n "$TMP_NUGET_CONFIG" && -f "$TMP_NUGET_CONFIG" ]]; then
    rm -f "$TMP_NUGET_CONFIG"
  fi
}
trap cleanup EXIT

CONFIG_FILE="$REPO_ROOT/nuget.config"
USING_MANAGED_CONFIG="false"

if [[ -n "$PROXY_URL" || -n "$FALLBACK_DIR" || -n "$FALLBACK_PACKAGES_DIR" ]]; then
  USING_MANAGED_CONFIG="true"
  TMP_NUGET_CONFIG="$(mktemp "${TMPDIR:-/tmp}/sba-nuget-XXXXXX.config")"
  chmod 600 "$TMP_NUGET_CONFIG"

  {
    echo '<?xml version="1.0" encoding="utf-8"?>'
    echo '<configuration>'
    echo '  <packageSources>'
    echo '    <clear />'
    if [[ -n "$PROXY_URL" ]]; then
      ESCAPED_PROXY="$(xml_escape "$PROXY_URL")"
      if [[ "$ALLOW_INSECURE" == "true" && "$PROXY_URL" == http://* ]]; then
        echo "    <add key=\"upstream_proxy\" value=\"${ESCAPED_PROXY}\" protocolVersion=\"3\" allowInsecureConnections=\"true\" />"
      else
        echo "    <add key=\"upstream_proxy\" value=\"${ESCAPED_PROXY}\" protocolVersion=\"3\" />"
      fi
    fi
    if [[ "$PROXY_ONLY" != "true" ]]; then
      echo '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />'
    fi
    if [[ -n "$FALLBACK_DIR" ]]; then
      ESCAPED_FALLBACK_DIR="$(xml_escape "$FALLBACK_DIR")"
      echo "    <add key=\"local_fallback\" value=\"${ESCAPED_FALLBACK_DIR}\" />"
    fi
    echo '  </packageSources>'
    echo '  <disabledPackageSources>'
    echo '    <clear />'
    echo '  </disabledPackageSources>'
    if [[ -n "$FALLBACK_PACKAGES_DIR" ]]; then
      ESCAPED_FALLBACK_PKGS="$(xml_escape "$FALLBACK_PACKAGES_DIR")"
      echo '  <fallbackPackageFolders>'
      echo "    <add key=\"local_fallback_cache\" value=\"${ESCAPED_FALLBACK_PKGS}\" />"
      echo '  </fallbackPackageFolders>'
    fi
    echo '  <packageRestore>'
    echo '    <add key="enabled" value="True" />'
    echo '    <add key="automatic" value="True" />'
    echo '  </packageRestore>'
    echo '</configuration>'
  } > "$TMP_NUGET_CONFIG"

  CONFIG_FILE="$TMP_NUGET_CONFIG"
fi

RESTORE_ARGS=("$SOLUTION_PATH" "--configfile" "$CONFIG_FILE")
if [[ "$LOCKED_MODE" == "true" ]]; then
  RESTORE_ARGS+=("--locked-mode")
fi
if [[ "$CLEAR_CACHE" == "true" ]]; then
  RESTORE_ARGS+=("--force" "--no-cache")
elif [[ "$FORCE_RESTORE" == "true" ]]; then
  RESTORE_ARGS+=("--force")
fi
if [[ "$USING_MANAGED_CONFIG" == "true" ]]; then
  RESTORE_ARGS+=("-p:SBA_NUGET_CONFIG_MANAGED=true")
fi

echo "NuGet Hardened Restore Summary:"
echo "  dotnet          : $DOTNET_BIN ($("$DOTNET_BIN" --version))"
echo "  solution        : $SOLUTION_PATH"
echo "  configfile      : $CONFIG_FILE"
echo "  locked-mode     : $LOCKED_MODE"
echo "  http-retry      : $NUGET_ENABLE_ENHANCED_HTTP_RETRY (maxTries=$NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT, delayMs=$NUGET_ENHANCED_NETWORK_RETRY_DELAY_MILLISECONDS, retry429=$NUGET_RETRY_HTTP_429)"
echo "  proxy-feed      : ${PROXY_URL:-"(none)"}${PROXY_ONLY:+ (proxy-only=$PROXY_ONLY)}"
echo "  auth-token      : $(mask_secret "$AUTH_TOKEN") (source=$AUTH_SOURCE, user=$AUTH_USER)"
echo "  fallback-feed   : ${FALLBACK_DIR:-"(none)"}"
echo "  fallback-cache  : ${FALLBACK_PACKAGES_DIR:-"(none)"}"

if [[ "$DRY_RUN" == "true" ]]; then
  echo "[dry-run] Restore command: $DOTNET_BIN restore ${RESTORE_ARGS[*]}"
  exit 0
fi

if [[ "$CLEAR_CACHE" == "true" ]]; then
  "$DOTNET_BIN" nuget locals http-cache --clear
fi

if [[ -n "$PROXY_URL" && -n "$AUTH_TOKEN" ]]; then
  export NuGetPackageSourceCredentials_upstream_proxy="Username=${AUTH_USER};Password=${AUTH_TOKEN}"
fi

"$DOTNET_BIN" restore "${RESTORE_ARGS[@]}"
