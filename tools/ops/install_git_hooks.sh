#!/bin/sh
# Install the repository's local git hooks. Run from any directory:
#   sh tools/ops/install_git_hooks.sh
set -eu

ROOT=$(git rev-parse --show-toplevel)
HOOK_DIR="$ROOT/.git/hooks"
mkdir -p "$HOOK_DIR"

cat > "$HOOK_DIR/pre-commit" <<'HOOK'
#!/bin/sh
ROOT=$(git rev-parse --show-toplevel 2>/dev/null) || exit 0
[ -n "$ROOT" ] || exit 0
exec "$ROOT/tools/ops/pre-commit"
HOOK
chmod +x "$HOOK_DIR/pre-commit"

echo "Installed $HOOK_DIR/pre-commit -> tools/ops/pre-commit"
