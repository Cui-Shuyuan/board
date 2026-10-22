#!/usr/bin/env bash
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
RUNTIME_ROOT="${BOARD_RUNTIME_ROOT:-/mnt/d/board}"
TOOL_DIR="$REPO/.tools"
DOTNET="$TOOL_DIR/dotnet/dotnet"

# Load runtime secrets first; keep repo-root .env as a migration fallback.
if [ -f "$RUNTIME_ROOT/config/.env" ]; then
  set -a
  # shellcheck disable=SC1091
  . "$RUNTIME_ROOT/config/.env"
  set +a
elif [ -f "$REPO/.env" ]; then
  set -a
  # shellcheck disable=SC1091
  . "$REPO/.env"
  set +a
fi

if [ ! -x "$DOTNET" ]; then
  echo "Local .NET SDK not found at $DOTNET" >&2
  echo "Please run the setup/install step or install .NET SDK 9/10 into $TOOL_DIR/dotnet." >&2
  exit 1
fi

# Make sure Qdrant is up, and learn which host WSL should use to reach it.
QDRANT_OUTPUT="$(bash "$REPO/tools/ops/start_qdrant.sh")"
echo "$QDRANT_OUTPUT"
QDRANT_HOST="$(printf '%s\n' "$QDRANT_OUTPUT" | sed -n 's/^QDRANT_HOST=//p' | tail -1)"
if [ -z "$QDRANT_HOST" ]; then
  echo "Could not determine Qdrant host" >&2
  exit 1
fi

export DOTNET_ROOT="$TOOL_DIR/dotnet"
export DOTNET_CLI_HOME="$TOOL_DIR/home"
export HOME="$TOOL_DIR/home"
export PATH="$TOOL_DIR/dotnet:$PATH"
CUDA_LIB=""
[ -d /usr/local/cuda/lib64 ] && CUDA_LIB="/usr/local/cuda/lib64"
export LD_LIBRARY_PATH="$TOOL_DIR/lib${CUDA_LIB:+:$CUDA_LIB}${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export NUGET_PACKAGES="$TOOL_DIR/nuget"
export DOTNET_ROLL_FORWARD=Major

# Migration-friendly config for the Linux/WSL layout.
export BOARD_RUNTIME_ROOT="$RUNTIME_ROOT"
export Rules__BasePath="$REPO"
export Embedding__ModelDir="$RUNTIME_ROOT/models/bge-base-zh-v1.5-fp32"
export Rules__DiskCache__Directory="$RUNTIME_ROOT/cache/rules-derived-cache"
export Content__ManifestDirectory="$RUNTIME_ROOT/content/manifests"
export Content__ReleaseDirectory="$RUNTIME_ROOT/content/releases"
export Qdrant__Host="$QDRANT_HOST"

mkdir -p "$DOTNET_CLI_HOME"

cd "$REPO/backend/BoardAI.Api"

# First build (no restore; packages are cached in .tools/nuget).
dotnet build --no-restore -p:UseAppHost=false

echo "Starting BoardAI.Api at http://localhost:5000 ..."
exec dotnet run --no-build --no-restore -p:UseAppHost=false --urls "http://localhost:5000"
