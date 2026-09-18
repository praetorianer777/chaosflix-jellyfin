#!/usr/bin/env bash
set -euo pipefail

# Regression tests for release.sh (see #6). Runs the real script against a
# throwaway copy of the version-carrying files, with the build, the packaging
# and the VCS calls stubbed, so nothing is built, committed or tagged.
# Offline, no dependencies beyond bash and python3.

cd "$(dirname "$0")/.."
REPO="$(pwd)"

WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT

FAILED=0
pass() { echo "   ✅ $1"; }
fail() { echo "   ❌ $1"; FAILED=1; }
check() { if [[ "$2" == "$3" ]]; then pass "$1"; else fail "$1: expected [$3], got [$2]"; fi; }

# ── Fixture ──────────────────────────────────────────────

SANDBOX="${WORK}/repo"
mkdir -p "${SANDBOX}/Jellyfin.Plugin.Chaosflix" "${SANDBOX}/bin"
cp "${REPO}/release.sh" "${SANDBOX}/release.sh"

cat > "${SANDBOX}/Directory.Build.props" <<'EOF'
<Project>
    <PropertyGroup>
        <Version>0.0.29.0</Version>
        <AssemblyVersion>0.0.29.0</AssemblyVersion>
        <FileVersion>0.0.29.0</FileVersion>
    </PropertyGroup>
</Project>
EOF

cat > "${SANDBOX}/Jellyfin.Plugin.Chaosflix/meta.json" <<'EOF'
{
  "guid": "c4a05f11-4ccc-4b00-bdea-dbeef1337000",
  "name": "Chaosflix",
  "owner": "praetorianer777",
  "version": "0.0.29.0",
  "targetAbi": "10.11.0.0",
  "status": "Active"
}
EOF

python3 - "${SANDBOX}/manifest.json" <<'EOF'
import json, sys


def entry(version, abi):
    return {
        "version": version,
        "changelog": "Release v" + version,
        "targetAbi": abi,
        "sourceUrl": "https://example.invalid/" + version + ".zip",
        "checksum": "0" * 32,
        "timestamp": "2026-01-01T00:00:00Z",
    }


versions = [entry(v, "10.11.0.0") for v in
            ("0.0.29.0", "0.0.28.0", "0.0.27.0", "0.0.26.0", "0.0.25.0")]
versions += [entry(v, "10.10.0.0") for v in ("0.0.20.0", "0.0.19.0")]

with open(sys.argv[1], "w") as f:
    json.dump([{"guid": "c4a05f11-4ccc-4b00-bdea-dbeef1337000",
                "name": "Chaosflix",
                "owner": "praetorianer777",
                "versions": versions}], f, indent=2)
    f.write("\n")
EOF

PRISTINE="${WORK}/pristine"
mkdir -p "${PRISTINE}/Jellyfin.Plugin.Chaosflix"
cp "${SANDBOX}/Directory.Build.props" "${SANDBOX}/manifest.json" "${PRISTINE}/"
cp "${SANDBOX}/Jellyfin.Plugin.Chaosflix/meta.json" "${PRISTINE}/Jellyfin.Plugin.Chaosflix/"

# The version tests all start from the same 0.0.29 fixture, so each one can
# state the number it expects instead of tracking what its predecessors wrote.
reset_fixture() {
    cp "${PRISTINE}/Directory.Build.props" "${PRISTINE}/manifest.json" "${SANDBOX}/"
    cp "${PRISTINE}/Jellyfin.Plugin.Chaosflix/meta.json" \
        "${SANDBOX}/Jellyfin.Plugin.Chaosflix/"
    rm -f "${SANDBOX}/CHANGELOG.md" "${SANDBOX}"/release-notes-*.md \
        "${SANDBOX}"/chaosflix-jellyfin-*.zip
}

# ── Stubs: no container build, no archiver, no VCS ───────

cat > "${SANDBOX}/bin/docker" <<'EOF'
#!/usr/bin/env bash
mkdir -p artifacts
echo "stub assembly" > artifacts/Jellyfin.Plugin.Chaosflix.dll
echo "Build succeeded (stub)"
EOF

cat > "${SANDBOX}/bin/zip" <<'EOF'
#!/usr/bin/env bash
target=""
for arg in "$@"; do
    [[ "$arg" == -* ]] && continue
    if [[ -z "$target" ]]; then
        target="$arg"
        : > "$target"
    else
        cat "$arg" >> "$target"
    fi
done
EOF

# The history the notes are generated from is handed to the stub instead of a
# real repository: FAKE_PREV_TAG is what "git describe" answers, FAKE_LOG the
# file "git log" prints.
cat > "${SANDBOX}/bin/git" <<'EOF'
#!/usr/bin/env bash
{ printf '%s\t' "$@"; echo; } >> "${VCS_LOG}"
case "$1" in
    describe)
        [[ -n "${FAKE_PREV_TAG:-}" ]] && echo "${FAKE_PREV_TAG}"
        ;;
    show)
        # The tagged meta.json, reduced to the field release.sh reads from it.
        [[ -n "${FAKE_PREV_ABI:-}" ]] \
            && printf '{"version": "0.0.0.0", "targetAbi": "%s"}\n' "${FAKE_PREV_ABI}"
        ;;
    log)
        [[ -n "${FAKE_LOG:-}" && -s "${FAKE_LOG}" ]] && cat "${FAKE_LOG}"
        ;;
esac
exit 0
EOF

chmod +x "${SANDBOX}/bin/"*

# ── Run ──────────────────────────────────────────────────

CHANGELOG="Don't \"crash\"; '); import os; os.system('touch ${WORK}/pwned'); ('"
VCS_LOG="${WORK}/vcs.log"
: > "${VCS_LOG}"

echo "🧪 release.sh — quoted changelog, manifest retention, version format"
if ! (cd "${SANDBOX}" && PATH="${SANDBOX}/bin:${PATH}" VCS_LOG="${VCS_LOG}" \
        ./release.sh 0.0.30 "${CHANGELOG}") > "${WORK}/out.log" 2>&1; then
    echo "   ❌ release.sh exited non-zero"
    cat "${WORK}/out.log"
    exit 1
fi

# ── Assertions ───────────────────────────────────────────

if [[ -e "${WORK}/pwned" ]]; then
    fail "changelog was executed as Python code"
else
    pass "changelog is not executed as code"
fi

ACTUAL_CHANGELOG=$(python3 -c "
import json, sys
with open(sys.argv[1]) as f:
    print(json.load(f)[0]['versions'][0]['changelog'], end='')
" "${SANDBOX}/manifest.json")
check "changelog survives quotes intact" "${ACTUAL_CHANGELOG}" "${CHANGELOG}"

read -r KEPT NEWEST HAS_29 HAS_20 HAS_25 MANIFEST_ABI MANIFEST_VERSION CHECKSUM < <(python3 -c "
import json, sys
with open(sys.argv[1]) as f:
    versions = json.load(f)[0]['versions']
have = {v['version'] for v in versions}
print(len(versions), versions[0]['version'],
      '0.0.29.0' in have, '0.0.20.0' in have, '0.0.25.0' in have,
      versions[0]['targetAbi'], versions[0]['version'], versions[0]['checksum'])
" "${SANDBOX}/manifest.json")

check "newest entry is the released version" "${NEWEST}" "0.0.30.0"
check "previous entry of the same targetAbi is kept" "${HAS_29}" "True"
check "newest entry of an older targetAbi is kept" "${HAS_20}" "True"
check "entries beyond the retention window are dropped" "${HAS_25}" "False"
check "retention keeps 5 current plus 1 per older targetAbi" "${KEPT}" "6"

META_ABI=$(python3 -c "
import json, sys
with open(sys.argv[1]) as f:
    print(json.load(f)['targetAbi'])
" "${SANDBOX}/Jellyfin.Plugin.Chaosflix/meta.json")
check "targetAbi matches between meta.json and manifest.json" "${MANIFEST_ABI}" "${META_ABI}"

META_VERSION=$(python3 -c "
import json, sys
with open(sys.argv[1]) as f:
    print(json.load(f)['version'])
" "${SANDBOX}/Jellyfin.Plugin.Chaosflix/meta.json")
check "meta.json version is four-part" "${META_VERSION}" "0.0.30.0"
check "manifest.json version is four-part" "${MANIFEST_VERSION}" "0.0.30.0"

PROPS_VERSIONS=$(grep -oP '<(Version|AssemblyVersion|FileVersion)>\K[^<]+' \
    "${SANDBOX}/Directory.Build.props" | sort -u | tr '\n' ' ')
check "Directory.Build.props versions are four-part" "${PROPS_VERSIONS}" "0.0.30.0 "

check "checksum is the MD5 of the ZIP" "${CHECKSUM}" \
    "$(md5sum "${SANDBOX}/chaosflix-jellyfin-v0.0.30.zip" | cut -d' ' -f1)"

STAGED=$(grep -P '^add\t' "${VCS_LOG}" | head -1 | tr '\t' ' ' | sed 's/ *$//')
check "only the release files are staged" "${STAGED}" \
    "add Directory.Build.props Jellyfin.Plugin.Chaosflix/meta.json manifest.json CHANGELOG.md"

if grep -qi 'co-authored-by' "${VCS_LOG}"; then
    fail "release commit still carries a Co-authored-by trailer"
else
    pass "release commit carries no invented co-author"
fi

# ── Rejected input leaves the tree untouched ─────────────

echo "🧪 release.sh — malformed version is rejected before anything is written"
BEFORE=$(md5sum "${SANDBOX}/manifest.json" "${SANDBOX}/Directory.Build.props" \
    "${SANDBOX}/Jellyfin.Plugin.Chaosflix/meta.json")
if (cd "${SANDBOX}" && PATH="${SANDBOX}/bin:${PATH}" VCS_LOG="${VCS_LOG}" \
        ./release.sh "0.0.31-rc1" "nope") > /dev/null 2>&1; then
    fail "malformed version was accepted"
else
    pass "malformed version is rejected"
fi
AFTER=$(md5sum "${SANDBOX}/manifest.json" "${SANDBOX}/Directory.Build.props" \
    "${SANDBOX}/Jellyfin.Plugin.Chaosflix/meta.json")
check "no file was modified by the rejected run" "${AFTER}" "${BEFORE}"

# ── Generated release notes (#19) ────────────────────────

FAKE_LOG="${WORK}/fake-log"
log_reset() { : > "${FAKE_LOG}"; }
log_entry() { printf '%s\x1f%s\x1f%s\x1e' "$1" "$2" "${3:-}" >> "${FAKE_LOG}"; }

run_release() {
    local prev_tag="$1"
    shift
    : > "${VCS_LOG}"
    (cd "${SANDBOX}" && PATH="${SANDBOX}/bin:${PATH}" VCS_LOG="${VCS_LOG}" \
        FAKE_LOG="${FAKE_LOG}" FAKE_PREV_TAG="${prev_tag}" \
        FAKE_PREV_ABI="${FAKE_PREV_ABI:-}" \
        ./release.sh "$@") > "${WORK}/out.log" 2>&1
}

echo "🧪 release.sh — notes generated from the commits since the previous tag"
log_reset
log_entry 1111111111111111111111111111111111111111 "feat: add a search box"
log_entry 2222222222222222222222222222222222222222 "fix: stop the proxy hanging (#42)"
log_entry 3333333333333333333333333333333333333333 "feat!: drop the old config keys"
log_entry 4444444444444444444444444444444444444444 "chore: bump a dependency"
log_entry 5555555555555555555555555555555555555555 "Update README by hand"
log_entry 6666666666666666666666666666666666666666 "refactor: split the client" \
    "BREAKING CHANGE: watch state is reset"
log_entry 7777777777777777777777777777777777777777 "release: v0.0.30 — Release v0.0.30"

if ! run_release "" 0.0.31; then
    echo "   ❌ release.sh exited non-zero"
    cat "${WORK}/out.log"
    exit 1
fi

NOTES="${SANDBOX}/release-notes-v0.0.31.md"
if [[ -f "${NOTES}" ]]; then
    pass "release notes file is written"
else
    fail "release notes file is missing"
fi

section_of() {  # item -> heading it landed under
    python3 - "${NOTES}" "$1" <<'EOF'
import sys

heading = ""
with open(sys.argv[1]) as f:
    for line in f:
        line = line.rstrip("\n")
        if line.startswith("### "):
            heading = line[4:]
        elif sys.argv[2] in line:
            print(heading, end="")
            break
    else:
        print("<missing>", end="")
EOF
}

check "a feature lands under Features" "$(section_of 'add a search box')" "Features"
check "a fix lands under Bug fixes" "$(section_of 'stop the proxy hanging')" "Bug fixes"
check "a chore lands under Other" "$(section_of 'bump a dependency')" "Other"
check "a non-conforming subject is not lost" \
    "$(section_of 'Update README by hand')" "Other"
check "a release commit is skipped" "$(section_of 'Release v0.0.30')" "<missing>"
check "an exclamation mark makes a breaking change" \
    "$(section_of 'drop the old config keys')" "Breaking changes"
check "a BREAKING CHANGE trailer makes a breaking change" \
    "$(section_of 'watch state is reset')" "Breaking changes"
check "breaking changes come first" \
    "$(grep -m1 '^### ' "${NOTES}")" "### Breaking changes"
check "issue references are kept" \
    "$(grep -c 'stop the proxy hanging (#42)' "${NOTES}" || true)" "1"
check "every item carries its short SHA" \
    "$(grep -c '(1111111)' "${NOTES}" || true)" "1"

if grep -q 'generated from the commits since the first commit' "${WORK}/out.log"; then
    pass "a missing previous tag falls back to the whole history"
else
    fail "a missing previous tag falls back to the whole history"
fi

check "changelog file is created with a Keep a Changelog header" \
    "$(head -1 "${SANDBOX}/CHANGELOG.md")" "# Changelog"
check "the section of the previous release is kept" \
    "$(grep -c '^## \[0.0.30\]' "${SANDBOX}/CHANGELOG.md" || true)" "1"
if grep -q '^## \[0.0.31\] - [0-9]\{4\}-[0-9]\{2\}-[0-9]\{2\}$' "${SANDBOX}/CHANGELOG.md"; then
    pass "changelog file carries the released version and date"
else
    fail "changelog file carries the released version and date"
fi

MANIFEST_NOTES=$(python3 -c "
import json, sys
with open(sys.argv[1]) as f:
    print(json.load(f)[0]['versions'][0]['changelog'], end='')
" "${SANDBOX}/manifest.json")
if [[ "${MANIFEST_NOTES}" == *"Features:"* && "${MANIFEST_NOTES}" == *"### "* ]]; then
    fail "manifest changelog carries the full markdown"
elif [[ "${MANIFEST_NOTES}" == *"Features:"* && "${MANIFEST_NOTES}" == *"add a search box"* ]]; then
    pass "manifest changelog is the compact list"
else
    fail "manifest changelog is the compact list: got [${MANIFEST_NOTES}]"
fi

COMMIT_SUBJECT=$(grep -P '^commit\t' "${VCS_LOG}" | head -1 | cut -f3)
check "the release commit subject stays one short line" \
    "${COMMIT_SUBJECT}" "release: v0.0.31"

if grep -q 'gh release create v0.0.31 chaosflix-jellyfin-v0.0.31.zip' "${WORK}/out.log"; then
    pass "a ready-to-run gh release command is printed"
else
    fail "a ready-to-run gh release command is printed"
fi
if grep -q 'add a search box' "${WORK}/out.log"; then
    pass "the notes are printed for pasting"
else
    fail "the notes are printed for pasting"
fi

if grep -qP '^push\t' "${VCS_LOG}"; then
    fail "release.sh pushed by itself"
else
    pass "release.sh does not push by itself"
fi

# ── The changelog file is prepended to, never overwritten ─

echo "🧪 release.sh — a second release prepends to CHANGELOG.md"
log_reset
log_entry 8888888888888888888888888888888888888888 "fix: repair the second thing"
if ! run_release "v0.0.31" 0.0.32; then
    echo "   ❌ release.sh exited non-zero"
    cat "${WORK}/out.log"
    exit 1
fi

check "the earlier section is still there" \
    "$(grep -c '^## \[0.0.31\]' "${SANDBOX}/CHANGELOG.md" || true)" "1"
check "the header is not repeated" \
    "$(grep -c '^# Changelog$' "${SANDBOX}/CHANGELOG.md" || true)" "1"
check "the newest section is on top" \
    "$(grep -m1 '^## \[' "${SANDBOX}/CHANGELOG.md")" "## [0.0.32] - $(date -u +%Y-%m-%d)"
check "the previous tag bounds the history" \
    "$(grep -c 'add a search box' "${SANDBOX}/release-notes-v0.0.32.md" || true)" "0"

# ── A changelog given on the command line still wins ─────

echo "🧪 release.sh — a changelog argument overrides the generated notes"
log_reset
log_entry 9999999999999999999999999999999999999999 "feat: add something generated"
if ! run_release "v0.0.32" 0.0.33 "Handwritten note"; then
    echo "   ❌ release.sh exited non-zero"
    cat "${WORK}/out.log"
    exit 1
fi

OVERRIDE=$(python3 -c "
import json, sys
with open(sys.argv[1]) as f:
    print(json.load(f)[0]['versions'][0]['changelog'], end='')
" "${SANDBOX}/manifest.json")
check "the manual changelog wins in manifest.json" "${OVERRIDE}" "Handwritten note"
check "the generated notes are not used" \
    "$(grep -c 'add something generated' "${SANDBOX}/CHANGELOG.md" || true)" "0"
check "the manual changelog reaches CHANGELOG.md" \
    "$(grep -c 'Handwritten note' "${SANDBOX}/CHANGELOG.md" || true)" "1"
MANUAL_SUBJECT=$(grep -P '^commit\t' "${VCS_LOG}" | head -1 | cut -f3)
check "the manual changelog reaches the commit subject" \
    "${MANUAL_SUBJECT}" "release: v0.0.33 — Handwritten note"

# ── The version is worked out from the history (#19) ─────

released_version() {
    python3 -c "
import json, sys
with open(sys.argv[1]) as f:
    print(json.load(f)['version'], end='')
" "${SANDBOX}/Jellyfin.Plugin.Chaosflix/meta.json"
}

fixture_state() {
    md5sum "${SANDBOX}/manifest.json" "${SANDBOX}/Directory.Build.props" \
        "${SANDBOX}/Jellyfin.Plugin.Chaosflix/meta.json"
}

FAKE_PREV_ABI=""

echo "🧪 release.sh — the next version is inferred from the commits"

reset_fixture
log_reset
log_entry aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa "fix: repair one thing"
log_entry bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb "fix: repair another thing"
if ! run_release "v0.0.29"; then
    echo "   ❌ release.sh exited non-zero"
    cat "${WORK}/out.log"
    exit 1
fi
check "a fix-only history bumps the patch" "$(released_version)" "0.0.30.0"
check "the deciding rule is printed" \
    "$(grep -c 'a fix → PATCH' "${WORK}/out.log" || true)" "1"
check "the previous version is printed" \
    "$(grep -c 'Previous version: 0.0.29' "${WORK}/out.log" || true)" "1"
check "the counts per type are printed" \
    "$(grep -c 'Commits since:    2 (fix: 2)' "${WORK}/out.log" || true)" "1"

reset_fixture
log_reset
log_entry cccccccccccccccccccccccccccccccccccccccc "fix: repair one thing"
log_entry dddddddddddddddddddddddddddddddddddddddd "feat: add a browsing filter"
if ! run_release "v0.0.29"; then
    echo "   ❌ release.sh exited non-zero"
    cat "${WORK}/out.log"
    exit 1
fi
check "a feat bumps the minor" "$(released_version)" "0.1.0.0"
check "the feat rule is printed" \
    "$(grep -c 'a feat → MINOR' "${WORK}/out.log" || true)" "1"

reset_fixture
log_reset
log_entry eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee "feat!: drop the old config keys"
if ! run_release "v0.0.29"; then
    echo "   ❌ release.sh exited non-zero"
    cat "${WORK}/out.log"
    exit 1
fi
check "a breaking change stays below 1.0.0" "$(released_version)" "0.1.0.0"
check "the degraded breaking rule is printed" \
    "$(grep -c 'below 1.0.0 → MINOR' "${WORK}/out.log" || true)" "1"

reset_fixture
log_reset
log_entry ffffffffffffffffffffffffffffffffffffffff "refactor: split the client" \
    "BREAKING CHANGE: watch state is reset"
if ! run_release "v0.0.29"; then
    echo "   ❌ release.sh exited non-zero"
    cat "${WORK}/out.log"
    exit 1
fi
check "a BREAKING CHANGE trailer counts as breaking" "$(released_version)" "0.1.0.0"

reset_fixture
log_reset
log_entry 1010101010101010101010101010101010101010 "chore: tidy the workflow"
log_entry 2020202020202020202020202020202020202020 "docs: extend the README"
if ! run_release "v0.0.29"; then
    echo "   ❌ release.sh exited non-zero"
    cat "${WORK}/out.log"
    exit 1
fi
check "housekeeping alone bumps the patch" "$(released_version)" "0.0.30.0"
check "housekeeping alone is called out" \
    "$(grep -c 'nothing user-facing → PATCH' "${WORK}/out.log" || true)" "1"

echo "🧪 release.sh — a raised targetAbi is a minor bump"
reset_fixture
sed -i 's|"targetAbi": "10.11.0.0"|"targetAbi": "10.12.0.0"|' \
    "${SANDBOX}/Jellyfin.Plugin.Chaosflix/meta.json"
log_reset
log_entry 3030303030303030303030303030303030303030 "chore: raise the Jellyfin baseline"
FAKE_PREV_ABI="10.11.0.0"
if ! run_release "v0.0.29"; then
    echo "   ❌ release.sh exited non-zero"
    cat "${WORK}/out.log"
    exit 1
fi
check "a raised targetAbi bumps the minor" "$(released_version)" "0.1.0.0"
check "the targetAbi rule is printed" \
    "$(grep -c 'targetAbi raised (10.11.0.0 → 10.12.0.0) → MINOR' "${WORK}/out.log" || true)" "1"
FAKE_PREV_ABI=""

echo "🧪 release.sh — an empty history aborts without touching anything"
reset_fixture
log_reset
BEFORE=$(fixture_state)
if run_release "v0.0.29"; then
    fail "an empty history was released"
else
    pass "an empty history is rejected"
fi
check "no file was modified by the empty history" "$(fixture_state)" "${BEFORE}"
if grep -q 'No commits since v0.0.29' "${WORK}/out.log"; then
    pass "the abort names the tag it looked at"
else
    fail "the abort names the tag it looked at"
fi
if grep -qP '^(add|commit|tag)\t' "${VCS_LOG}"; then
    fail "the aborted run touched the VCS"
else
    pass "the aborted run touched no VCS state"
fi

echo "🧪 release.sh — an explicit version still wins over the inferred one"
reset_fixture
log_reset
log_entry 4040404040404040404040404040404040404040 "feat: would infer a minor bump"
if ! run_release "v0.0.29" 0.0.40; then
    echo "   ❌ release.sh exited non-zero"
    cat "${WORK}/out.log"
    exit 1
fi
check "the given version is used verbatim" "$(released_version)" "0.0.40.0"
check "nothing is inferred when a version is given" \
    "$(grep -c 'Working out the next version' "${WORK}/out.log" || true)" "0"

echo "🧪 release.sh — --dry-run changes nothing"
reset_fixture
log_reset
log_entry 5050505050505050505050505050505050505050 "feat: add a browsing filter"
BEFORE=$(fixture_state)
if ! run_release "v0.0.29" --dry-run; then
    echo "   ❌ release.sh --dry-run exited non-zero"
    cat "${WORK}/out.log"
    exit 1
fi
check "the dry run leaves every file untouched" "$(fixture_state)" "${BEFORE}"
if [[ -e "${SANDBOX}/CHANGELOG.md" || -e "${SANDBOX}/release-notes-v0.1.0.md" ]]; then
    fail "the dry run wrote the changelog or the notes"
else
    pass "the dry run wrote neither changelog nor notes"
fi
if grep -qP '^(add|commit|tag)\t' "${VCS_LOG}"; then
    fail "the dry run touched the VCS"
else
    pass "the dry run touched no VCS state"
fi
check "the dry run prints the version it would release" \
    "$(grep -c 'Dry run — Chaosflix v0.1.0' "${WORK}/out.log" || true)" "1"
if grep -q 'add a browsing filter' "${WORK}/out.log"; then
    pass "the dry run prints the notes it would write"
else
    fail "the dry run prints the notes it would write"
fi

reset_fixture

# ── The files that are actually published ────────────────

echo "🧪 repository files agree on targetAbi, owner and version format"
REPO_OK=$(python3 -c "
import json, re

with open('Jellyfin.Plugin.Chaosflix/meta.json') as f:
    meta = json.load(f)
with open('manifest.json') as f:
    manifest = json.load(f)[0]
props = open('Directory.Build.props').read()

four = re.compile(r'^\d+\.\d+\.\d+\.\d+$')
problems = []
if not four.match(meta['targetAbi']):
    problems.append('meta targetAbi not four-part')
if meta['targetAbi'] != manifest['versions'][0]['targetAbi']:
    problems.append('targetAbi differs between meta.json and manifest.json')
if meta['owner'] != manifest['owner']:
    problems.append('owner differs between meta.json and manifest.json')
versions = [meta['version'], manifest['versions'][0]['version']]
versions += re.findall(r'<(?:Version|AssemblyVersion|FileVersion)>([^<]+)<', props)
for v in versions:
    if not four.match(v):
        problems.append('version not four-part: ' + v)
print('; '.join(problems) or 'ok')
")
check "repository files are consistent" "${REPO_OK}" "ok"

if (( FAILED )); then
    echo "❌ release.sh tests failed"
    exit 1
fi
echo "✅ release.sh tests passed"
