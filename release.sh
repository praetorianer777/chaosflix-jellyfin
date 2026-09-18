#!/usr/bin/env bash
set -euo pipefail

# Chaosflix release script
# Usage: ./release.sh 0.0.2 "Added search feature, bugfixes"

REPO_OWNER="praetorianer777"
REPO_NAME="chaosflix-jellyfin"

PROPS="Directory.Build.props"
META="Jellyfin.Plugin.Chaosflix/meta.json"
MANIFEST="manifest.json"
CHANGELOG_FILE="CHANGELOG.md"

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
MANUAL_CHANGELOG="${2:-}"
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

# ── 0. Release notes ────────────────────────────────────

# The notes are derived from the commits since the previous tag (see #19); a
# changelog passed as the second argument still overrides the generated one.
NOTES_FILE="release-notes-${TAG}.md"
RAW_LOG=""
if [[ -z "${MANUAL_CHANGELOG}" ]]; then
    PREV_TAG=$(git describe --tags --abbrev=0 2>/dev/null || true)
    RANGE="HEAD"
    [[ -n "${PREV_TAG}" ]] && RANGE="${PREV_TAG}..HEAD"
    # \x1f separates the fields of a commit, \x1e the commits: neither can
    # occur in a subject or body, unlike any printable delimiter.
    RAW_LOG=$(git log --no-merges --format="%H%x1f%s%x1f%b%x1e" "${RANGE}" 2>/dev/null || true)
fi

# Same reason as below: everything reaches Python through the environment.
CHANGELOG=$(
    VERSION_THREE="${VERSION_THREE}" \
    RAW_LOG="${RAW_LOG}" \
    MANUAL_CHANGELOG="${MANUAL_CHANGELOG}" \
    RELEASE_DATE="$(date -u +%Y-%m-%d)" \
    NOTES_FILE="${NOTES_FILE}" \
    CHANGELOG_FILE="${CHANGELOG_FILE}" \
    python3 - <<'PY'
import os
import re

version = os.environ['VERSION_THREE']
release_date = os.environ['RELEASE_DATE']
manual = os.environ.get('MANUAL_CHANGELOG', '')
raw = os.environ.get('RAW_LOG', '')
notes_path = os.environ['NOTES_FILE']
changelog_path = os.environ['CHANGELOG_FILE']

BREAKING, FEATURES, FIXES, OTHER = (
    'Breaking changes', 'Features', 'Bug fixes', 'Other')
HEADER = re.compile(
    r'^(?P<type>[A-Za-z]+)(?:\((?P<scope>[^)]*)\))?(?P<bang>!)?:\s+(?P<desc>.+)$')
TRAILER = re.compile(r'^BREAKING[ -]CHANGE:\s*(?P<desc>.*)$')

KEEP_A_CHANGELOG_HEADER = """# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
"""


def collect(log):
    sections = {BREAKING: [], FEATURES: [], FIXES: [], OTHER: []}
    for record in log.split('\x1e'):
        if not record.strip():
            continue
        fields = record.strip('\n').split('\x1f')
        sha, subject = fields[0], fields[1] if len(fields) > 1 else ''
        body = fields[2] if len(fields) > 2 else ''
        match = HEADER.match(subject)
        ctype = match.group('type').lower() if match else ''
        if ctype == 'release':
            continue
        if match:
            text = match.group('desc')
            if match.group('scope'):
                text = '%s: %s' % (match.group('scope'), text)
        else:
            # Not a Conventional Commit — kept verbatim rather than dropped.
            text = subject
        trailer = None
        for line in body.splitlines():
            found = TRAILER.match(line.strip())
            if found:
                trailer = found.group('desc').strip() or text
                break
        if (match and match.group('bang')) or trailer:
            sections[BREAKING].append((trailer or text, sha[:7]))
        elif ctype == 'feat':
            sections[FEATURES].append((text, sha[:7]))
        elif ctype == 'fix':
            sections[FIXES].append((text, sha[:7]))
        else:
            sections[OTHER].append((text, sha[:7]))
    return sections


def markdown(sections):
    out = []
    for title in (BREAKING, FEATURES, FIXES, OTHER):
        if not sections[title]:
            continue
        out.append('### %s' % title)
        out.append('')
        out += ['- %s (%s)' % item for item in sections[title]]
        out.append('')
    if not out:
        return '- No changes recorded since the previous release.'
    return '\n'.join(out).strip()


def compact(sections, limit=4):
    """Short form for manifest.json — rendered inside Jellyfin's catalogue."""
    parts = []
    notable = any(sections[t] for t in (BREAKING, FEATURES, FIXES))
    for title in (BREAKING, FEATURES, FIXES, OTHER):
        items = sections[title]
        if not items:
            continue
        if title == OTHER and notable:
            parts.append('Plus %d other change%s.'
                         % (len(items), '' if len(items) == 1 else 's'))
            continue
        lines = ['%s:' % title]
        lines += ['- %s' % text for text, _ in items[:limit]]
        if len(items) > limit:
            lines.append('- and %d more' % (len(items) - limit))
        parts.append('\n'.join(lines))
    return '\n'.join(parts)


if manual:
    body = manual.strip()
    short = manual
else:
    sections = collect(raw)
    body = markdown(sections)
    short = compact(sections) or 'Release v%s' % version

with open(notes_path, 'w') as f:
    f.write(body.rstrip('\n') + '\n')

section = '## [%s] - %s\n\n%s' % (version, release_date, body.rstrip('\n'))
if os.path.exists(changelog_path):
    with open(changelog_path) as f:
        lines = f.read().split('\n')
    # Newest on top: the new section goes directly above the previous one,
    # so the Keep a Changelog preamble stays where it is.
    first = next((i for i, line in enumerate(lines) if line.startswith('## ')),
                 len(lines))
    head = '\n'.join(lines[:first]).strip('\n')
    tail = '\n'.join(lines[first:]).strip('\n')
    document = '\n\n'.join(part for part in (head, section, tail) if part)
else:
    document = KEEP_A_CHANGELOG_HEADER + '\n' + section

with open(changelog_path, 'w') as f:
    f.write(document.rstrip('\n') + '\n')

print(short, end='')
PY
)

echo "📦 Releasing Chaosflix ${TAG}"
echo "   Version:   ${VERSION_FOUR}"
echo "   targetAbi: ${TARGET_ABI}"
echo "   ZIP:       ${ZIP_NAME}"
if [[ -n "${MANUAL_CHANGELOG}" ]]; then
    echo "   Changelog: ${CHANGELOG} (given on the command line)"
else
    echo "   Changelog: generated from the commits since ${PREV_TAG:-the first commit}"
fi
echo "   ✅ ${CHANGELOG_FILE}"
echo "   ✅ ${NOTES_FILE}"
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
git add "${PROPS}" "${META}" "${MANIFEST}" "${CHANGELOG_FILE}"
if [[ -n "${MANUAL_CHANGELOG}" ]]; then
    git commit -m "release: ${TAG} — ${MANUAL_CHANGELOG}"
else
    # Generated notes are multi-line, so they belong in the body — the subject
    # has to stay one short line.
    git commit -m "release: ${TAG}" -m "${CHANGELOG}"
fi
git tag "${TAG}"

echo ""
echo "📰 Release notes (${NOTES_FILE}):"
echo ""
sed 's/^/   /' "${NOTES_FILE}"
echo ""
echo "🎉 Done! Next steps:"
echo ""
echo "   git push origin main --tags"
echo "   gh release create ${TAG} ${ZIP_NAME} --title ${TAG} --notes-file ${NOTES_FILE}"
echo ""
