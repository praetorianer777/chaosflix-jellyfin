#!/usr/bin/env bash
set -euo pipefail

# Chaosflix release script
# Usage: ./release.sh 0.0.2 "Added search feature, bugfixes"

REPO_OWNER="praetorianer777"
REPO_NAME="chaosflix-jellyfin"

if [[ $# -lt 1 ]]; then
    echo "Usage: $0 <version> [changelog]"
    echo "Example: $0 0.0.2 \"Added search, fixed caching\""
    exit 1
fi

VERSION="$1"
VERSION_FOUR="${VERSION}.0"  # Jellyfin uses 4-part versions
CHANGELOG="${2:-Release v${VERSION}}"
TAG="v${VERSION}"
ZIP_NAME="chaosflix-jellyfin-${TAG}.zip"
SOURCE_URL="https://github.com/${REPO_OWNER}/${REPO_NAME}/releases/download/${TAG}/${ZIP_NAME}"

echo "📦 Releasing Chaosflix ${TAG}"
echo "   Version:   ${VERSION_FOUR}"
echo "   Changelog: ${CHANGELOG}"
echo "   ZIP:       ${ZIP_NAME}"
echo ""

# ── 1. Update versions in all files ─────────────────────

echo "✏️  Updating version strings..."

# Directory.Build.props
sed -i "s|<Version>.*</Version>|<Version>${VERSION_FOUR}</Version>|" Directory.Build.props
sed -i "s|<AssemblyVersion>.*</AssemblyVersion>|<AssemblyVersion>${VERSION_FOUR}</AssemblyVersion>|" Directory.Build.props
sed -i "s|<FileVersion>.*</FileVersion>|<FileVersion>${VERSION_FOUR}</FileVersion>|" Directory.Build.props

# meta.json
sed -i "s|\"version\": \".*\"|\"version\": \"${VERSION_FOUR}\"|" Jellyfin.Plugin.Chaosflix/meta.json

# manifest.json — update version, sourceUrl, changelog, timestamp
TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
python3 -c "
import json, sys
with open('manifest.json', 'r') as f:
    data = json.load(f)
new_version = {
    'version': '${VERSION_FOUR}',
    'changelog': '${CHANGELOG}',
    'targetAbi': data[0]['versions'][0]['targetAbi'],
    'sourceUrl': '${SOURCE_URL}',
    'checksum': '',
    'timestamp': '${TIMESTAMP}'
}
# Keep only the new version (clean slate per release)
data[0]['versions'] = [new_version]
with open('manifest.json', 'w') as f:
    json.dump(data, f, indent=2)
    f.write('\n')
"

echo "   ✅ Directory.Build.props"
echo "   ✅ meta.json"
echo "   ✅ manifest.json"

# ── 2. Build ─────────────────────────────────────────────

echo ""
echo "🔨 Building..."

if command -v docker &>/dev/null; then
    docker run --rm \
        -v "$(pwd):/src" \
        -w /src \
        mcr.microsoft.com/dotnet/sdk:9.0 \
        dotnet publish Jellyfin.Plugin.Chaosflix/Jellyfin.Plugin.Chaosflix.csproj -c Release -o /src/artifacts 2>&1 | tail -3
else
    dotnet publish Jellyfin.Plugin.Chaosflix/Jellyfin.Plugin.Chaosflix.csproj -c Release -o ./artifacts 2>&1 | tail -3
fi

cp Jellyfin.Plugin.Chaosflix/meta.json artifacts/
echo "   ✅ Build succeeded"

# ── 3. Create ZIP ────────────────────────────────────────

echo ""
echo "📦 Creating ${ZIP_NAME}..."
cd artifacts
zip -j "../${ZIP_NAME}" Jellyfin.Plugin.Chaosflix.dll meta.json
cd ..
echo "   ✅ $(du -h "${ZIP_NAME}" | cut -f1) — ${ZIP_NAME}"

# ── 4. Update checksum ──────────────────────────────────

MD5=$(md5sum "${ZIP_NAME}" | cut -d' ' -f1)
python3 -c "
import json
with open('manifest.json', 'r') as f:
    data = json.load(f)
data[0]['versions'][0]['checksum'] = '${MD5}'
with open('manifest.json', 'w') as f:
    json.dump(data, f, indent=2)
    f.write('\n')
"
echo "   ✅ Checksum: ${MD5}"

# ── 5. Git commit + tag ─────────────────────────────────

echo ""
echo "📝 Committing..."
git add -A
git commit -m "release: ${TAG} — ${CHANGELOG}

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git tag "${TAG}"

echo ""
echo "🎉 Done! Next steps:"
echo ""
echo "   git push origin main --tags"
echo "   # Then on GitHub: Releases → Create release from tag ${TAG}"
echo "   # Upload: ${ZIP_NAME}"
echo ""
