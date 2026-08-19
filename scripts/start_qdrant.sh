#!/usr/bin/env bash
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOG_DIR="$REPO/.tools/logs"
mkdir -p "$LOG_DIR"

TOOL_DIR="$REPO/.tools"
QDRANT_DATA="$TOOL_DIR/qdrant-data"

# Prefer a Linux-native qdrant (repo-local .tools first, then PATH);
# otherwise fall back to the existing Windows D:\qdrant\qdrant.exe via WSL interop.
if [ -x "$TOOL_DIR/qdrant/qdrant" ]; then
  QDRANT_BIN="$TOOL_DIR/qdrant/qdrant"
  QDRANT_DIR="$TOOL_DIR/qdrant"
  QDRANT_LINUX=1
elif command -v qdrant >/dev/null 2>&1; then
  QDRANT_BIN="$(command -v qdrant)"
  QDRANT_DIR="$(dirname "$QDRANT_BIN")"
  QDRANT_LINUX=1
else
  QDRANT_BIN="/mnt/d/qdrant/qdrant.exe"
  QDRANT_DIR="/mnt/d/qdrant"
  QDRANT_LINUX=0
fi

WIN_HOST="$(ip route show default 2>/dev/null | awk '/default/ {print $3; exit}')"

# If it is already reachable, just print the right host and exit.
for host in localhost "$WIN_HOST"; do
  if [ -n "$host" ] && curl -fsS --max-time 2 "http://$host:6333/collections" >/dev/null 2>&1; then
    echo "Qdrant already running at http://$host:6333"
    echo "QDRANT_HOST=$host"
    exit 0
  fi
done

if [ ! -f "$QDRANT_BIN" ]; then
  echo "Qdrant binary not found: $QDRANT_BIN" >&2
  exit 1
fi

echo "Starting Qdrant from $QDRANT_BIN ..."
mkdir -p "$QDRANT_DATA"
cd "$QDRANT_DIR"
if [ "$QDRANT_LINUX" = "1" ]; then
  QDRANT__STORAGE__STORAGE_PATH="$QDRANT_DATA" setsid nohup "$QDRANT_BIN" --disable-telemetry > "$LOG_DIR/qdrant.log" 2>&1 < /dev/null &
else
  setsid nohup "$QDRANT_BIN" > "$LOG_DIR/qdrant.log" 2>&1 < /dev/null &
fi
disown || true

# Wait for it. A Windows Qdrant is reachable from WSL via the Windows host IP
# (WSL default gateway), while a Linux Qdrant is reachable at localhost.
for _ in $(seq 1 30); do
  for host in localhost "$WIN_HOST"; do
    if [ -n "$host" ] && curl -fsS --max-time 2 "http://$host:6333/collections" >/dev/null 2>&1; then
      echo "Qdrant started at http://$host:6333"
      echo "QDRANT_HOST=$host"
      exit 0
    fi
  done
  sleep 1
done

echo "Qdrant did not become ready; see $LOG_DIR/qdrant.log" >&2
exit 1
