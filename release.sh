#!/usr/bin/env bash
set -euo pipefail

# Chaosflix release script
# Usage: ./release.sh [version] [changelog] [--dry-run]
#   ./release.sh                        version inferred from the commit history
#   ./release.sh 0.0.2                  version given by hand, notes generated
#   ./release.sh 0.0.2 "Added search"   version and changelog given by hand
#   ./release.sh --dry-run              print version and notes, change nothing

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

DRY_RUN=0
ARGS=()
for arg in "$@"; do
    case "${arg}" in
        --dry-run) DRY_RUN=1 ;;
        -h|--help)
            sed -n '4,9p' "$0" | sed 's|^# \?||'
            exit 0
            ;;
        *) ARGS+=("${arg}") ;;
    esac
done
if (( ${#ARGS[@]} > 2 )); then
    echo "Usage: $0 [version] [changelog] [--dry-run]"
    exit 1
fi
VERSION="${ARGS[0]:-}"
MANUAL_CHANGELOG="${ARGS[1]:-}"

if [[ -n "${VERSION}" && ! "${VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
    echo "❌ Version must be X.Y.Z or X.Y.Z.W (digits only), got: ${VERSION}"
    exit 1
fi

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

# ── 0. Level with the remote ────────────────────────────

# A release built from a stale checkout tags code the remote does not have:
# the tag pushes, the branch is rejected, and the workflow publishes the old
# build with a manifest entry nobody can see (#64).
BRANCH=$(git rev-parse --abbrev-ref HEAD)
DEFAULT_BRANCH=$(git symbolic-ref --quiet --short refs/remotes/origin/HEAD 2>/dev/null | sed 's|^origin/||')
DEFAULT_BRANCH="${DEFAULT_BRANCH:-main}"
if [[ "${BRANCH}" != "${DEFAULT_BRANCH}" ]]; then
    # A release cut from a feature branch tags a commit the default branch does
    # not have: the asset publishes, but the manifest entry the workflow needs
    # lives only on that branch, so the checksum step fails (#100).
    echo "❌ Releasing from '${BRANCH}', but releases are cut from '${DEFAULT_BRANCH}'."
    echo "   git checkout ${DEFAULT_BRANCH} && git merge --ff-only origin/${DEFAULT_BRANCH}"
    exit 1
fi

UPSTREAM=$(git rev-parse --abbrev-ref --symbolic-full-name '@{upstream}' 2>/dev/null || true)
if [[ -n "${UPSTREAM}" ]]; then
    git fetch --quiet "${UPSTREAM%%/*}" || echo "   ⚠️  could not reach ${UPSTREAM%%/*}, checking against the last fetch"
    BEHIND=$(git rev-list --count "HEAD..${UPSTREAM}")
    AHEAD=$(git rev-list --count "${UPSTREAM}..HEAD")
    if (( BEHIND > 0 )); then
        echo "❌ $(git rev-parse --abbrev-ref HEAD) is ${BEHIND} commit(s) behind ${UPSTREAM}."
        echo "   Releasing now would tag code the remote does not have. Missing:"
        git log --oneline "HEAD..${UPSTREAM}" | sed 's/^/     /'
        echo ""
        echo "   git fetch origin && git reset --hard ${UPSTREAM}"
        exit 1
    fi
    if (( AHEAD > 0 )); then
        echo "⚠️  $(git rev-parse --abbrev-ref HEAD) is ${AHEAD} commit(s) ahead of ${UPSTREAM}; they are released as part of this version."
    fi
else
    echo "⚠️  no upstream for $(git rev-parse --abbrev-ref HEAD); cannot check whether this is what the remote has."
fi

# ── 0. The commits since the previous tag ───────────────

PREV_TAG=$(git describe --tags --abbrev=0 2>/dev/null || true)
RANGE="HEAD"
[[ -n "${PREV_TAG}" ]] && RANGE="${PREV_TAG}..HEAD"
# \x1f separates the fields of a commit, \x1e the commits: neither can
# occur in a subject or body, unlike any printable delimiter.
RAW_LOG=$(git log --no-merges --format="%H%x1f%s%x1f%b%x1e" "${RANGE}" 2>/dev/null || true)

# The tagged meta.json is the only record of the targetAbi the previous release
# shipped with. A raise since then changes which servers may install the plugin:
# it is a MINOR on its own, and it is a breaking change in the notes. Both need
# it, so it is read here rather than inside the version inference — given a
# version nothing is inferred, and the raise would go unreported (#112).
PREV_ABI=""
if [[ -n "${PREV_TAG}" ]]; then
    PREV_ABI=$(git show "${PREV_TAG}:${META}" 2>/dev/null \
        | python3 -c "
import json, sys
try:
    print(json.load(sys.stdin)['targetAbi'])
except Exception:
    pass
" || true)
fi

# ── 0a. Next version ────────────────────────────────────

# Without a version argument the number is derived from those commits (#19).
# The reasoning is printed before anything is written, so the bump can be
# checked before it is released.
if [[ -z "${VERSION}" ]]; then
    CURRENT_VERSION=$(META="${META}" python3 -c "
import json, os
with open(os.environ['META']) as f:
    print(json.load(f)['version'])
")
    # Values reach Python through the environment, never interpolated.
    VERSION=$(
        RAW_LOG="${RAW_LOG}" \
        PREV_TAG="${PREV_TAG}" \
        CURRENT_VERSION="${CURRENT_VERSION}" \
        PREV_ABI="${PREV_ABI}" \
        TARGET_ABI="${TARGET_ABI}" \
        python3 - <<'PY'
import os
import re
import sys

HEADER = re.compile(
    r'^(?P<type>[A-Za-z]+)(?:\((?P<scope>[^)]*)\))?(?P<bang>!)?:\s+(?P<desc>.+)$')
TRAILER = re.compile(r'^BREAKING[ -]CHANGE:\s*')
NO_RELEASE = ('chore', 'docs', 'test', 'build', 'ci', 'refactor', 'style', 'perf')


def parts(version):
    numbers = [int(n) for n in version.split('.')]
    return tuple(numbers + [0] * (4 - len(numbers)))[:4]


raw = os.environ.get('RAW_LOG', '')
prev_tag = os.environ.get('PREV_TAG', '')
current = os.environ['CURRENT_VERSION']
prev_abi = os.environ.get('PREV_ABI', '')
target_abi = os.environ['TARGET_ABI']

# The tag can be ahead of the files (or the other way round); the next version
# has to be above both, or the server sees a release it already has.
base = parts(current)
if prev_tag:
    tagged = re.match(r'^v?([0-9]+(?:\.[0-9]+)*)$', prev_tag)
    if tagged:
        base = max(base, parts(tagged.group(1)))

counts = {}
breaking = []
for record in raw.split('\x1e'):
    if not record.strip():
        continue
    fields = record.strip('\n').split('\x1f')
    subject = fields[1] if len(fields) > 1 else ''
    body = fields[2] if len(fields) > 2 else ''
    match = HEADER.match(subject)
    ctype = match.group('type').lower() if match else ''
    if ctype == 'release':
        continue
    counts[ctype or 'other'] = counts.get(ctype or 'other', 0) + 1
    if (match and match.group('bang')) or any(
            TRAILER.match(line.strip()) for line in body.splitlines()):
        breaking.append(subject)

total = sum(counts.values())
abi_raised = bool(prev_abi) and parts(target_abi) > parts(prev_abi)

major, minor, patch = base[0], base[1], base[2]
if not total:
    rule = None
elif breaking and major == 0:
    # A 0.x MAJOR bump would declare 1.0; while below 1.0.0 a breaking change
    # is degraded to MINOR, as semver itself suggests.
    rule = 'a breaking change, but the project is below 1.0.0 → MINOR'
    minor, patch = minor + 1, 0
elif breaking:
    rule = 'a breaking change → MAJOR'
    major, minor, patch = major + 1, 0, 0
elif counts.get('feat'):
    rule = 'a feat → MINOR'
    minor, patch = minor + 1, 0
elif abi_raised:
    rule = 'targetAbi raised (%s → %s) → MINOR' % (prev_abi, target_abi)
    minor, patch = minor + 1, 0
elif counts.get('fix'):
    rule = 'a fix → PATCH'
    patch += 1
elif all(ctype in NO_RELEASE for ctype in counts):
    rule = ('only %s and nothing user-facing → PATCH'
            % ', '.join(sorted(counts)))
    patch += 1
else:
    rule = 'changes without a release type → PATCH'
    patch += 1

summary = ', '.join('%s: %d' % (t, n) for t, n in sorted(counts.items())) or 'none'
log = sys.stderr
print('🔢 Working out the next version (no version given)', file=log)
print('   Previous version: %d.%d.%d (%s)'
      % (base[0], base[1], base[2], prev_tag or 'no tag yet'), file=log)
print('   Commits since:    %d (%s)' % (total, summary), file=log)
if breaking:
    print('   Breaking:         %d (%s)'
          % (len(breaking), '; '.join(breaking)), file=log)
print('   targetAbi:        %s (%s)'
      % (target_abi,
         'raised from %s' % prev_abi if abi_raised
         else 'unchanged' if prev_abi else 'no previous value'), file=log)

if rule is None:
    print('   Rule:             no commits since %s — nothing to release'
          % (prev_tag or 'the first commit'), file=log)
    print('❌ No commits since %s; nothing was changed.'
          % (prev_tag or 'the first commit'), file=log)
    sys.exit(2)

print('   Rule:             %s' % rule, file=log)
print('   Next version:     %d.%d.%d' % (major, minor, patch), file=log)
print('%d.%d.%d' % (major, minor, patch))
PY
    ) || exit 1
    INFERRED=1
else
    INFERRED=0
fi

# .NET treats a missing component as -1, so 0.1.0 < 0.1.0.0: every file that
# carries the version has to use the same four-part form. Tag and ZIP keep the
# three-part form the published releases already use.
VERSION_THREE=$(cut -d. -f1-3 <<< "${VERSION}")
VERSION_FOUR="${VERSION_THREE}.$(cut -d. -f4 <<< "${VERSION}.0")"
TAG="v${VERSION_THREE}"
ZIP_NAME="chaosflix-jellyfin-${TAG}.zip"
SOURCE_URL="https://github.com/${REPO_OWNER}/${REPO_NAME}/releases/download/${TAG}/${ZIP_NAME}"

# ── 0b. Release notes ───────────────────────────────────

# The notes are derived from the commits since the previous tag (see #19); a
# changelog passed as the second argument still overrides the generated one.
NOTES_FILE="release-notes-${TAG}.md"
if [[ -n "${MANUAL_CHANGELOG}" ]]; then
    RAW_LOG=""
fi

# Same reason as below: everything reaches Python through the environment.
CHANGELOG=$(
    VERSION_THREE="${VERSION_THREE}" \
    RAW_LOG="${RAW_LOG}" \
    MANUAL_CHANGELOG="${MANUAL_CHANGELOG}" \
    PREV_ABI="${PREV_ABI}" \
    TARGET_ABI="${TARGET_ABI}" \
    RELEASE_DATE="$(date -u +%Y-%m-%d)" \
    NOTES_FILE="${NOTES_FILE}" \
    CHANGELOG_FILE="${CHANGELOG_FILE}" \
    DRY_RUN="${DRY_RUN}" \
    python3 - <<'PY'
import os
import re
import sys

version = os.environ['VERSION_THREE']
release_date = os.environ['RELEASE_DATE']
manual = os.environ.get('MANUAL_CHANGELOG', '')
raw = os.environ.get('RAW_LOG', '')
prev_abi = os.environ.get('PREV_ABI', '')
target_abi = os.environ.get('TARGET_ABI', '')
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


def raised_minimum(previous, current):
    """The server versions this release drops, said plainly.

    targetAbi is the minimum server version that may install the plugin, and it
    has no upper bound. Raising it is the only way to keep a build away from
    servers it no longer supports, and those servers stay on the release before
    it without being told. That belongs at the top of the notes, not under
    whatever type the commit that changed it happened to carry (#112).
    """
    def parts(value):
        try:
            return tuple(int(piece) for piece in value.split('.'))
        except ValueError:
            return ()

    before, after = parts(previous), parts(current)
    if not before or not after or after <= before:
        return ''

    def server(value):
        return '.'.join(value.split('.')[:3])

    return 'requires Jellyfin %s or newer (raised from %s)' % (
        server(current), server(previous))


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
        # The raised-minimum entry stands for no commit, so it carries no sha.
        out += ['- %s (%s)' % item if item[1] else '- %s' % item[0]
                for item in sections[title]]
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
    raised = raised_minimum(prev_abi, target_abi)
    if raised:
        sections[BREAKING].insert(0, (raised, ''))
    body = markdown(sections)
    short = compact(sections) or 'Release v%s' % version

if os.environ.get('DRY_RUN') == '1':
    # Nothing is written; the notes go to stderr so stdout stays the short form.
    print('📰 Release notes that would be written to %s:\n' % notes_path,
          file=sys.stderr)
    print('\n'.join('   ' + line for line in body.split('\n')),
          file=sys.stderr)
    print(short, end='')
    raise SystemExit(0)

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

if (( DRY_RUN )); then
    echo "🔍 Dry run — Chaosflix ${TAG}"
else
    echo "📦 Releasing Chaosflix ${TAG}"
fi
echo "   Version:   ${VERSION_FOUR} ($( (( INFERRED )) && echo "inferred from the commit history" || echo "given on the command line"))"
echo "   targetAbi: ${TARGET_ABI}"
echo "   ZIP:       ${ZIP_NAME}"
if [[ -n "${MANUAL_CHANGELOG}" ]]; then
    echo "   Changelog: ${CHANGELOG} (given on the command line)"
else
    echo "   Changelog: generated from the commits since ${PREV_TAG:-the first commit}"
fi
if (( DRY_RUN )); then
    echo ""
    echo "   Nothing was written: no version strings, no ${CHANGELOG_FILE}, no commit, no tag."
    exit 0
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

# The artifact is deliberately not built here: the release workflow builds the
# ZIP, uploads it and writes the md5 of the bytes it uploaded into
# manifest.json. A ZIP built on this machine would never be byte-identical to
# the published one, so its checksum described bytes nobody could download
# (#49). That also keeps releasing free of a local zip or .NET SDK.

# ── 2. Git commit + tag ─────────────────────────────────

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
# Annotated with the notes: release-notes-*.md is git-ignored, so the tag
# message is what a release built from the tag (CI) has to work with.
git tag -a "${TAG}" -F "${NOTES_FILE}"

echo ""
echo "📰 Release notes (${NOTES_FILE}):"
echo ""
sed 's/^/   /' "${NOTES_FILE}"
echo ""
echo "🎉 Done! Next steps:"
echo ""
echo "   git push origin main --tags"
echo ""
echo "   The release workflow builds ${ZIP_NAME} from the tag, publishes it"
echo "   with these notes and commits its checksum into ${MANIFEST}."
echo ""
