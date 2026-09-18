#!/usr/bin/env bash
set -euo pipefail

# Clones and builds the real jellyfin-chromecast receiver so the browser
# harness can run its actual bundle. Needs the network on first use; the clone
# and the build are cached under e2e/.artifacts, so later runs are offline.
#
# RECEIVER_REF   git ref to build (default: the pinned commit below)
# RECEIVER_DIR   where to keep the checkout (default e2e/.artifacts/receiver)

cd "$(dirname "$0")/.."

# Pinned so a receiver-side change cannot silently alter what this measures.
REF="${RECEIVER_REF:-2032cbb9a05e6c888a9b957214ec5750092f0690}"
REPO="https://github.com/jellyfin/jellyfin-chromecast.git"
DIR="${RECEIVER_DIR:-$(pwd)/.artifacts/receiver}"

if [[ ! -d "$DIR/.git" ]]; then
    echo "📥 Cloning jellyfin-chromecast into $DIR"
    mkdir -p "$(dirname "$DIR")"
    git clone --quiet "$REPO" "$DIR"
fi

if [[ "$(git -C "$DIR" rev-parse HEAD)" != "$REF" ]]; then
    git -C "$DIR" fetch --quiet origin "$REF" || git -C "$DIR" fetch --quiet origin
    git -C "$DIR" checkout --quiet "$REF"
    rm -rf "$DIR/dist"
fi

if [[ ! -f "$DIR/dist/index.html" ]]; then
    echo "🔨 Building the receiver at $REF"
    (cd "$DIR" && npm ci --silent && npm run build --silent)
fi

echo "✅ Receiver bundle ready in $DIR/dist"
