#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
PROJECT_PATH="$REPO_ROOT/src/SmartBacklogAssistant/SmartBacklogAssistant.csproj"

RID=""
VERSION=""
OUTPUT_DIR="$REPO_ROOT/artifacts/release"
SKIP_SMOKE="false"

usage() {
  cat <<'EOF'
Usage: ./scripts/package-release.sh [OPTIONS]

Publishes a self-contained single-file SmartBacklogAssistant release archive,
writes its SHA-256 checksum, and runs out-of-repo smoke verification (Issue #59).

Options:
  --rid <rid>           Target RuntimeIdentifier (default: auto-detected linux-x64/osx-arm64/...)
  --version <semver>    Release version (e.g., 1.1.0 or v1.1.0; default: from .csproj)
  --output-dir <dir>    Destination directory for archive & checksum (default: artifacts/release)
  --skip-smoke          Skip out-of-repo binary parity smoke checks
  -h, --help            Show this help message
EOF
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
  echo "error: .NET 10 SDK not found on PATH or at \$HOME/.dotnet/dotnet." >&2
  return 1
}

detect_rid() {
  local os_name arch_name
  os_name="$(uname -s)"
  arch_name="$(uname -m)"
  case "$os_name:$arch_name" in
    Linux:x86_64|Linux:amd64) echo "linux-x64" ;;
    Linux:aarch64|Linux:arm64) echo "linux-arm64" ;;
    Darwin:arm64) echo "osx-arm64" ;;
    Darwin:x86_64) echo "osx-x64" ;;
    *)
      echo "error: Unsupported OS/arch '$os_name:$arch_name'. Pass --rid explicitly." >&2
      return 1
      ;;
  esac
}

compute_sha256() {
  local file_path="$1"
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$file_path" | awk '{print $1}'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$file_path" | awk '{print $1}'
  else
    echo "error: Neither sha256sum nor shasum is installed." >&2
    return 1
  fi
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid)
      [[ $# -ge 2 ]] || { echo "error: --rid requires a value" >&2; exit 2; }
      RID="$2"
      shift 2
      ;;
    --version)
      [[ $# -ge 2 ]] || { echo "error: --version requires a value" >&2; exit 2; }
      VERSION="$2"
      shift 2
      ;;
    --output-dir)
      [[ $# -ge 2 ]] || { echo "error: --output-dir requires a value" >&2; exit 2; }
      OUTPUT_DIR="$2"
      shift 2
      ;;
    --skip-smoke)
      SKIP_SMOKE="true"
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
if [[ -z "$RID" ]]; then
  RID="$(detect_rid)"
fi

if [[ -z "$VERSION" ]]; then
  VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT_PATH" | head -n 1 | tr -d '[:space:]')"
fi

CLEAN_VERSION="${VERSION#v}"
if [[ ! "$CLEAN_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$ ]]; then
  echo "error: Invalid semantic version '$VERSION'. Expected formats like 1.1.0, v1.1.0, or v1.2.0-rc.1." >&2
  exit 2
fi

export NUGET_ENABLE_ENHANCED_HTTP_RETRY="${NUGET_ENABLE_ENHANCED_HTTP_RETRY:-true}"
export NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT="${NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT:-6}"
export NUGET_ENHANCED_NETWORK_RETRY_DELAY_MILLISECONDS="${NUGET_ENHANCED_NETWORK_RETRY_DELAY_MILLISECONDS:-1000}"
export NUGET_RETRY_HTTP_429="${NUGET_RETRY_HTTP_429:-true}"
export NUGET_OBSERVE_RETRY_AFTER="${NUGET_OBSERVE_RETRY_AFTER:-true}"
export DOTNET_CLI_TELEMETRY_OPTOUT="${DOTNET_CLI_TELEMETRY_OPTOUT:-1}"

PACKAGE_NAME="SmartBacklogAssistant-v${CLEAN_VERSION}-${RID}"
ARCHIVE_NAME="${PACKAGE_NAME}.tar.gz"
mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd -- "$OUTPUT_DIR" && pwd)"

WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/sba-release-XXXXXX")"
cleanup() {
  rm -rf "$WORK_DIR"
}
trap cleanup EXIT

PUBLISH_DIR="$WORK_DIR/publish"
STAGE_PARENT="$WORK_DIR/stage"
STAGE_DIR="$STAGE_PARENT/$PACKAGE_NAME"
mkdir -p "$PUBLISH_DIR" "$STAGE_DIR"

echo "Packaging SmartBacklogAssistant release artifact:"
echo "  dotnet     : $DOTNET_BIN ($("$DOTNET_BIN" --version))"
echo "  version    : v$CLEAN_VERSION"
echo "  rid        : $RID"
echo "  output-dir : $OUTPUT_DIR"

"$DOTNET_BIN" restore "$PROJECT_PATH" \
  -r "$RID" \
  --configfile "$REPO_ROOT/nuget.config" \
  -p:NuGetLockFilePath="$WORK_DIR/packages.$RID.lock.json" \
  -p:RestoreLockedMode=false

"$DOTNET_BIN" publish "$PROJECT_PATH" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  --no-restore \
  -p:NuGetLockFilePath="$WORK_DIR/packages.$RID.lock.json" \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishTrimmed=false \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -p:Version="$CLEAN_VERSION" \
  -p:InformationalVersion="$CLEAN_VERSION" \
  -p:TreatWarningsAsErrors=true \
  -o "$PUBLISH_DIR"

if [[ ! -f "$PUBLISH_DIR/SmartBacklogAssistant" ]]; then
  echo "error: Expected published binary '$PUBLISH_DIR/SmartBacklogAssistant' was not produced." >&2
  exit 1
fi

cp "$PUBLISH_DIR/SmartBacklogAssistant" "$STAGE_DIR/SmartBacklogAssistant"
chmod 755 "$STAGE_DIR/SmartBacklogAssistant"
ln -s SmartBacklogAssistant "$STAGE_DIR/backlog"

cp -R "$REPO_ROOT/prompts" "$STAGE_DIR/prompts"
mkdir -p "$STAGE_DIR/samples"
cp "$REPO_ROOT/samples/raw-notes.txt" "$STAGE_DIR/samples/raw-notes.txt"
cp "$REPO_ROOT/README.md" "$STAGE_DIR/README.md"
cp "$REPO_ROOT/LICENSE" "$STAGE_DIR/LICENSE"

ARCHIVE_PATH="$OUTPUT_DIR/$ARCHIVE_NAME"
tar -C "$STAGE_PARENT" -czf "$ARCHIVE_PATH" "$PACKAGE_NAME"

HASH_HEX="$(compute_sha256 "$ARCHIVE_PATH")"
printf '%s  %s\n' "$HASH_HEX" "$ARCHIVE_NAME" > "${ARCHIVE_PATH}.sha256"

echo "Created archive : $ARCHIVE_PATH"
echo "SHA-256         : $HASH_HEX"

if [[ "$SKIP_SMOKE" == "true" ]]; then
  echo "Skipping smoke verification (--skip-smoke)."
  exit 0
fi

echo "Running out-of-repo packaged & standalone binary smoke verification..."

# 1. Verify extracted archive outside git checkout
SMOKE_ARCHIVE_DIR="$WORK_DIR/smoke-archive"
SMOKE_ARCHIVE_WS="$WORK_DIR/smoke-archive-ws"
mkdir -p "$SMOKE_ARCHIVE_DIR" "$SMOKE_ARCHIVE_WS"
ACTUAL_HASH="$(compute_sha256 "$ARCHIVE_PATH")"
if [[ "$ACTUAL_HASH" != "$HASH_HEX" ]]; then
  echo "error: Archive checksum mismatch ($ACTUAL_HASH != $HASH_HEX)." >&2
  exit 1
fi
tar -C "$SMOKE_ARCHIVE_DIR" -xzf "$ARCHIVE_PATH"
EXTRACTED_DIR="$SMOKE_ARCHIVE_DIR/$PACKAGE_NAME"

VERSION_OUT="$(cd "$SMOKE_ARCHIVE_DIR" && "$EXTRACTED_DIR/SmartBacklogAssistant" --version)"
echo "  archive --version : $VERSION_OUT"
printf '%s\n' "$VERSION_OUT" | grep -Fq "v${CLEAN_VERSION}"

(cd "$SMOKE_ARCHIVE_DIR" && "$EXTRACTED_DIR/backlog" --help | grep -Fq "SUBCOMMANDS")
(cd "$SMOKE_ARCHIVE_DIR" && "$EXTRACTED_DIR/backlog" -w "$SMOKE_ARCHIVE_WS" status | grep -Fq "Workspace & AI Status")
(cd "$SMOKE_ARCHIVE_DIR" && "$EXTRACTED_DIR/backlog" -w "$SMOKE_ARCHIVE_WS" draft "Verify Extracted Release Binary" --type 01 --preview --offline --non-interactive | grep -Fq "complete (Type01)")

# 2. Verify standalone single-file binary copied into an isolated directory without external prompts/
SMOKE_BARE_DIR="$WORK_DIR/smoke-bare"
SMOKE_BARE_WS="$WORK_DIR/smoke-bare-ws"
mkdir -p "$SMOKE_BARE_DIR" "$SMOKE_BARE_WS"
cp "$EXTRACTED_DIR/SmartBacklogAssistant" "$SMOKE_BARE_DIR/backlog"
chmod 755 "$SMOKE_BARE_DIR/backlog"

printf 'Discovery Note - Standalone Smoke\nRequirement: As an operator, I want standalone binary execution so that no SDK build is needed.\nPrecondition: Binary is on PATH.\nAction: Run process.\nExpected Outcome: Outcome renders cleanly.\n' > "$WORK_DIR/standalone-notes.txt"

STATUS_BARE="$(cd "$WORK_DIR" && "$SMOKE_BARE_DIR/backlog" -w "$SMOKE_BARE_WS" status)"
printf '%s\n' "$STATUS_BARE" | grep -Fq "(bundled in binary)"

(cd "$WORK_DIR" && "$SMOKE_BARE_DIR/backlog" -w "$SMOKE_BARE_WS" draft "Verify Standalone Bundled Prompts" --type 01 --preview --offline --non-interactive | grep -Fq "complete (Type01)")
(cd "$WORK_DIR" && "$SMOKE_BARE_DIR/backlog" -w "$SMOKE_BARE_WS" draft "Verify Standalone Type02 Schedule" --type 02 --preview --offline --non-interactive | grep -Fq "schedule (Type02)")
(cd "$WORK_DIR" && "$SMOKE_BARE_DIR/backlog" -w "$SMOKE_BARE_WS" process -i "$WORK_DIR/standalone-notes.txt" --dry-run --offline --non-interactive | grep -Fq "Standalone Smoke")

echo "Smoke verification passed for extracted archive and standalone single-file binary!"
