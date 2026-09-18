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

cat > "${SANDBOX}/bin/git" <<'EOF'
#!/usr/bin/env bash
{ printf '%s\t' "$@"; echo; } >> "${VCS_LOG}"
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
    "add Directory.Build.props Jellyfin.Plugin.Chaosflix/meta.json manifest.json"

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
