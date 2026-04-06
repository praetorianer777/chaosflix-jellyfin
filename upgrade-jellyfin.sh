#!/usr/bin/env bash
set -euo pipefail

# ──────────────────────────────────────────────────────────
# Chaosflix Jellyfin Version Upgrade Script
# ──────────────────────────────────────────────────────────
#
# Usage:
#   ./upgrade-jellyfin.sh              # auto-detect latest Jellyfin version
#   ./upgrade-jellyfin.sh 10.12.0      # upgrade to specific version
#
# What it does:
#   1. Detects the latest Jellyfin NuGet package version (or uses provided)
#   2. Updates .csproj NuGet references
#   3. Updates targetAbi in manifest.json
#   4. Updates .NET SDK version if needed (net9.0 → net10.0 etc.)
#   5. Attempts a Docker build to verify compatibility
#   6. Reports any breaking changes / build errors
#
# After running:
#   - Fix any build errors manually
#   - Test the plugin on the new Jellyfin version
#   - Run ./release.sh <version> "Upgrade to Jellyfin X.Y.Z"
#
# ──────────────────────────────────────────────────────────

CSPROJ="Jellyfin.Plugin.Chaosflix/Jellyfin.Plugin.Chaosflix.csproj"
MANIFEST="manifest.json"

# ── Detect current version ───────────────────────────────

CURRENT_VERSION=$(grep -oP 'Include="Jellyfin.Controller" Version="\K[^"]+' "$CSPROJ")
echo "📋 Current Jellyfin version: ${CURRENT_VERSION}"

# ── Determine target version ─────────────────────────────

if [[ $# -ge 1 ]]; then
    TARGET_VERSION="$1"
    echo "🎯 Target version (manual):  ${TARGET_VERSION}"
else
    echo "🔍 Fetching latest Jellyfin.Controller version from NuGet..."
    TARGET_VERSION=$(curl -s "https://api.nuget.org/v3-flatcontainer/jellyfin.controller/index.json" \
        | python3 -c "import json,sys; versions=json.load(sys.stdin)['versions']; print(versions[-1])")

    if [[ -z "$TARGET_VERSION" ]]; then
        echo "❌ Failed to fetch latest version from NuGet"
        echo "   Try: ./upgrade-jellyfin.sh <version>"
        exit 1
    fi
    echo "🎯 Latest NuGet version:     ${TARGET_VERSION}"
fi

if [[ "$CURRENT_VERSION" == "$TARGET_VERSION" ]]; then
    echo ""
    echo "✅ Already on version ${TARGET_VERSION}, nothing to do."
    exit 0
fi

# ── Detect .NET version change ───────────────────────────

CURRENT_MAJOR=$(echo "$CURRENT_VERSION" | cut -d. -f1-2)
TARGET_MAJOR=$(echo "$TARGET_VERSION" | cut -d. -f1-2)

# Map Jellyfin major version to .NET target framework
# 10.11.x → net9.0, 10.12.x → check release notes
CURRENT_TFM=$(grep -oP '<TargetFramework>\K[^<]+' "$CSPROJ")
echo "📦 Current target framework:  ${CURRENT_TFM}"

# ── Update .csproj ───────────────────────────────────────

echo ""
echo "✏️  Updating NuGet packages..."

sed -i "s|Include=\"Jellyfin.Controller\" Version=\"[^\"]*\"|Include=\"Jellyfin.Controller\" Version=\"${TARGET_VERSION}\"|" "$CSPROJ"
sed -i "s|Include=\"Jellyfin.Model\" Version=\"[^\"]*\"|Include=\"Jellyfin.Model\" Version=\"${TARGET_VERSION}\"|" "$CSPROJ"
echo "   ✅ ${CSPROJ}"

# ── Update targetAbi in manifest.json ────────────────────

TARGET_ABI="${TARGET_MAJOR}.0.0"
python3 -c "
import json
with open('${MANIFEST}', 'r') as f:
    data = json.load(f)
data[0]['versions'][0]['targetAbi'] = '${TARGET_ABI}'
with open('${MANIFEST}', 'w') as f:
    json.dump(data, f, indent=2)
    f.write('\n')
"
echo "   ✅ ${MANIFEST} (targetAbi: ${TARGET_ABI})"

# ── Check if .NET SDK image needs updating ───────────────

# Determine required SDK from target framework
SDK_TAG=$(echo "$CURRENT_TFM" | sed 's/net//')
echo ""
echo "🐳 Using Docker SDK image: mcr.microsoft.com/dotnet/sdk:${SDK_TAG}"

# ── Build test ───────────────────────────────────────────

echo ""
echo "🔨 Test build (${CURRENT_VERSION} → ${TARGET_VERSION})..."
echo ""

CERT_MOUNT=""
if [[ -d "/usr/local/share/ca-certificates" ]]; then
    CERT_MOUNT="-v /usr/local/share/ca-certificates:/usr/local/share/ca-certificates:ro"
fi

BUILD_OUTPUT=$(docker run --rm \
    -v "$(pwd):/src" \
    ${CERT_MOUNT} \
    -w /src \
    "mcr.microsoft.com/dotnet/sdk:${SDK_TAG}" \
    bash -c "update-ca-certificates 2>/dev/null; dotnet build ${CSPROJ} -c Release 2>&1" || true)

# Check result
if echo "$BUILD_OUTPUT" | grep -q "Build succeeded"; then
    echo "$BUILD_OUTPUT" | tail -5
    echo ""
    echo "══════════════════════════════════════════════════"
    echo "✅ BUILD SUCCEEDED — Upgrade ${CURRENT_VERSION} → ${TARGET_VERSION}"
    echo "══════════════════════════════════════════════════"
    echo ""
    echo "Next steps:"
    echo "  1. Test the plugin on Jellyfin ${TARGET_VERSION}"
    echo "  2. git add -A && git commit -m 'chore: upgrade to Jellyfin ${TARGET_VERSION}'"
    echo "  3. ./release.sh <plugin-version> 'Upgrade to Jellyfin ${TARGET_VERSION}'"
    echo ""
else
    echo "$BUILD_OUTPUT" | grep -E "error |Error |warning " | head -20
    echo ""
    echo "══════════════════════════════════════════════════"
    echo "❌ BUILD FAILED — Breaking changes detected!"
    echo "══════════════════════════════════════════════════"
    echo ""
    echo "Common fixes for Jellyfin major upgrades:"
    echo ""
    echo "  1. Target Framework change (z.B. net9.0 → net10.0):"
    echo "     sed -i 's/<TargetFramework>net9.0/<TargetFramework>net10.0/' ${CSPROJ}"
    echo "     Update Docker SDK: mcr.microsoft.com/dotnet/sdk:10.0"
    echo ""
    echo "  2. Namespace changes:"
    echo "     Check: https://github.com/jellyfin/jellyfin/releases"
    echo "     grep -rn 'using MediaBrowser' Jellyfin.Plugin.Chaosflix/"
    echo ""
    echo "  3. API signature changes (new required parameters):"
    echo "     Check the error messages above for missing methods/properties"
    echo ""
    echo "  4. DI registration changes:"
    echo "     Check ChaosflixServiceRegistrator.cs"
    echo ""
    echo "To revert:"
    echo "  git checkout -- ${CSPROJ} ${MANIFEST}"
    echo ""
fi
