#!/usr/bin/env bash
set -euo pipefail

# Chaosflix release script
# Usage: ./release.sh 0.0.2 "Added search feature, bugfixes"

REPO_OWNER="praetorianer777"
REPO_NAME="chaosflix-jellyfin"

PROPS="Directory.Build.props"
META="Jellyfin.Plugin.Chaosflix/meta.json"
MANIFEST="manifest.json"

# manifest.json retention: the newest entry of every distinct targetAbi is kept
# so servers on an older Jellyfin are still offered a version they can load; of
# the current targetAbi the newest KEEP_PER_ABI entries are kept.
KEEP_PER_ABI="${KEEP_PER_ABI:-5}"

if [[ $# -lt 1 ]]; then
    echo "Usage: $0 <version> [changelog]"
    echo "Example: $0 0.0.2 \"Added search, fixed caching\""
    exit 1
fi

VERSION="$1"
if [[ ! "${VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
    echo "❌ Version must be X.Y.Z or X.Y.Z.W (digits only), got: ${VERSION}"
    exit 1
fi
# .NET treats a missing component as -1, so 0.1.0 < 0.1.0.0: every file that
# carries the version has to use the same four-part form. Tag and ZIP keep the
# three-part form the published releases already use.
VERSION_THREE=$(cut -d. -f1-3 <<< "${VERSION}")
VERSION_FOUR="${VERSION_THREE}.$(cut -d. -f4 <<< "${VERSION}.0")"
CHANGELOG="${2:-Release v${VERSION_THREE}}"
TAG="v${VERSION_THREE}"
ZIP_NAME="chaosflix-jellyfin-${TAG}.zip"
SOURCE_URL="https://github.com/${REPO_OWNER}/${REPO_NAME}/releases/download/${TAG}/${ZIP_NAME}"

# meta.json holds the single source of truth for targetAbi — the *minimum*
# server version that may install and load this build. It is copied into every
# manifest entry written here; upgrade-jellyfin.sh is what changes it.
TARGET_ABI=$(META="${META}" python3 -c "
import json, os
with open(os.environ['META']) as f:
    print(json.load(f)['targetAbi'])
")
if [[ ! "${TARGET_ABI}" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "❌ targetAbi in ${META} must be four-part, got: ${TARGET_ABI}"
    exit 1
fi

echo "📦 Releasing Chaosflix ${TAG}"
echo "   Version:   ${VERSION_FOUR}"
echo "   targetAbi: ${TARGET_ABI}"
echo "   Changelog: ${CHANGELOG}"
echo "   ZIP:       ${ZIP_NAME}"
echo ""

# ── 1. Update versions in all files ─────────────────────

echo "✏️  Updating version strings..."

# Directory.Build.props
sed -i "s|<Version>.*</Version>|<Version>${VERSION_FOUR}</Version>|" "${PROPS}"
sed -i "s|<AssemblyVersion>.*</AssemblyVersion>|<AssemblyVersion>${VERSION_FOUR}</AssemblyVersion>|" "${PROPS}"
sed -i "s|<FileVersion>.*</FileVersion>|<FileVersion>${VERSION_FOUR}</FileVersion>|" "${PROPS}"

# meta.json
sed -i "s|\"version\": \".*\"|\"version\": \"${VERSION_FOUR}\"|" "${META}"

# manifest.json — prepend the new version, keep the older entries
TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
# Values reach Python through the environment. Interpolated into the source
# they broke on any changelog containing a quote — and let an argument inject
# Python code — after the other two files had already been rewritten.
MANIFEST="${MANIFEST}" \
VERSION_FOUR="${VERSION_FOUR}" \
CHANGELOG="${CHANGELOG}" \
TARGET_ABI="${TARGET_ABI}" \
SOURCE_URL="${SOURCE_URL}" \
TIMESTAMP="${TIMESTAMP}" \
KEEP_PER_ABI="${KEEP_PER_ABI}" \
python3 -c "
import json, os

path = os.environ['MANIFEST']
with open(path) as f:
    data = json.load(f)

new_version = {
    'version': os.environ['VERSION_FOUR'],
    'changelog': os.environ['CHANGELOG'],
    'targetAbi': os.environ['TARGET_ABI'],
    'sourceUrl': os.environ['SOURCE_URL'],
    'checksum': '',
    'timestamp': os.environ['TIMESTAMP'],
}

versions = [v for v in data[0]['versions'] if v.get('version') != new_version['version']]
versions.insert(0, new_version)

keep_current = int(os.environ['KEEP_PER_ABI'])
kept, seen, current = [], set(), 0
for entry in versions:
    abi = entry.get('targetAbi')
    if abi == new_version['targetAbi'] and current < keep_current:
        current += 1
    elif abi in seen:
        continue
    kept.append(entry)
    seen.add(abi)
data[0]['versions'] = kept

with open(path, 'w') as f:
    json.dump(data, f, indent=2)
    f.write('\n')
"

echo "   ✅ ${PROPS}"
echo "   ✅ ${META}"
echo "   ✅ ${MANIFEST}"

# ── 2. Build ─────────────────────────────────────────────

echo ""
echo "🔨 Building..."

if command -v docker &>/dev/null; then
    CERT_MOUNT=""
    if [[ -d "/usr/local/share/ca-certificates" ]]; then
        CERT_MOUNT="-v /usr/local/share/ca-certificates:/usr/local/share/ca-certificates:ro"
    fi
    # shellcheck disable=SC2086
    docker run --rm \
        -v "$(pwd):/src" \
        ${CERT_MOUNT} \
        -w /src \
        mcr.microsoft.com/dotnet/sdk:9.0 \
        bash -c "update-ca-certificates 2>/dev/null; dotnet publish Jellyfin.Plugin.Chaosflix/Jellyfin.Plugin.Chaosflix.csproj -c Release -o /src/artifacts" 2>&1 | tail -3
else
    dotnet publish Jellyfin.Plugin.Chaosflix/Jellyfin.Plugin.Chaosflix.csproj -c Release -o ./artifacts 2>&1 | tail -3
fi

cp "${META}" artifacts/
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
MANIFEST="${MANIFEST}" MD5="${MD5}" python3 -c "
import json, os

path = os.environ['MANIFEST']
with open(path) as f:
    data = json.load(f)
data[0]['versions'][0]['checksum'] = os.environ['MD5']
with open(path, 'w') as f:
    json.dump(data, f, indent=2)
    f.write('\n')
"
echo "   ✅ Checksum: ${MD5}"

# ── 5. Git commit + tag ─────────────────────────────────

echo ""
echo "📝 Committing..."
# Only the files this release rewrote — "git add -A" swept whatever else was in
# the working tree into the release commit.
git add "${PROPS}" "${META}" "${MANIFEST}"
git commit -m "release: ${TAG} — ${CHANGELOG}"
git tag "${TAG}"

echo ""
echo "🎉 Done! Next steps:"
echo ""
echo "   git push origin main --tags"
echo "   # Then on GitHub: Releases → Create release from tag ${TAG}"
echo "   # Upload: ${ZIP_NAME}"
echo ""
