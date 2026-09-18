#!/usr/bin/env bash
set -euo pipefail

# Installs the *published* plugin the way a user does: a fresh Jellyfin adds the
# repository manifest, downloads the release asset, verifies its checksum itself
# and loads the plugin. Nothing here is built locally — a side-loaded DLL would
# skip exactly the parts that broke before (#19 a sourceUrl with no asset behind
# it, #49 a checksum describing bytes that were never published).
#
# MANIFEST_URL   repository manifest to install from
# JELLYFIN_TAG   server image, defaults to the newest release
# EXPECT_VERSION version that has to end up installed; defaults to the newest
#                entry in the manifest
# INSTALL_PORT   host port for the throwaway server

MANIFEST_URL="${MANIFEST_URL:-https://raw.githubusercontent.com/praetorianer777/chaosflix-jellyfin/main/manifest.json}"
JELLYFIN_TAG="${JELLYFIN_TAG:-latest}"
INSTALL_PORT="${INSTALL_PORT:-8099}"
CONTAINER="chaosflix-install-test-$$"
PLUGIN_NAME="Chaosflix"
PLUGIN_ID="c4a05f11-4ccc-4b00-bdea-dbeef1337000"
ADMIN_USER="install-e2e"
ADMIN_PASS="chaosflix-install-e2e"
BASE="http://127.0.0.1:${INSTALL_PORT}"
AUTH='MediaBrowser Client="chaosflix-install", Device="script", DeviceId="chaosflix-install", Version="1.0.0"'
TOKEN=""
FAILED=0

pass() { echo "   ✅ $1"; }
fail() { echo "   ❌ $1"; FAILED=1; }

cleanup() { docker rm -f "$CONTAINER" >/dev/null 2>&1 || true; }
trap cleanup EXIT

api() {
    local method="$1" path="$2" body="${3:-}" auth="$AUTH"
    [[ -n "$TOKEN" ]] && auth="${AUTH}, Token=\"${TOKEN}\""
    # A server that just answered /System/Info/Public still resets connections
    # for a few seconds while the rest of it comes up.
    local retry=(--retry 5 --retry-connrefused --retry-all-errors --retry-delay 2)
    if [[ -n "$body" ]]; then
        curl -sS "${retry[@]}" -X "$method" "${BASE}${path}" \
            -H "Authorization: $auth" -H "Content-Type: application/json" -d "$body"
    else
        curl -sS "${retry[@]}" -X "$method" "${BASE}${path}" -H "Authorization: $auth"
    fi
}

status_of() {
    curl -sS --retry 5 --retry-connrefused --retry-all-errors --retry-delay 2 \
        -o /dev/null -w '%{http_code}' \
        -H "Authorization: ${AUTH}, Token=\"${TOKEN}\"" "${BASE}$1"
}

# 12.1 answers some endpoints in camelCase where 10.11 used PascalCase, so
# every field is looked up case-insensitively (see #1).
pluck() { python3 -c "
import json, sys

def lower(value):
    if isinstance(value, dict):
        return {k.lower(): lower(v) for k, v in value.items()}
    if isinstance(value, list):
        return [lower(v) for v in value]
    return value

d = lower(json.load(sys.stdin))
print($1)
"; }

plugin_field() {
    api GET /Plugins | pluck "next((p.get('$1', '') for p in d
        if p.get('id', '').replace('-', '') == '${PLUGIN_ID//-/}'), '')"
}

wait_for_server() {
    local deadline=$((SECONDS + 300))
    while (( SECONDS < deadline )); do
        curl -sf "${BASE}/System/Info/Public" >/dev/null 2>&1 && return 0
        sleep 3
    done
    echo "   ❌ the server never answered on ${BASE}"
    docker logs "$CONTAINER" 2>&1 | tail -20
    exit 1
}

echo "🧪 Installing ${PLUGIN_NAME} from ${MANIFEST_URL} into jellyfin:${JELLYFIN_TAG}"

EXPECTED="${EXPECT_VERSION:-$(curl -sSf "$MANIFEST_URL" | pluck "d[0]['versions'][0]['version']")}"
echo "   expecting version ${EXPECTED}"

docker run -d --name "$CONTAINER" -p "${INSTALL_PORT}:8096" \
    "jellyfin/jellyfin:${JELLYFIN_TAG}" >/dev/null
wait_for_server
SERVER_VERSION=$(curl -sS "${BASE}/System/Info/Public" | pluck "d['version']")
echo "   server ${SERVER_VERSION} is up"

api POST /Startup/Configuration \
    '{"UICulture":"en-US","MetadataCountryCode":"DE","PreferredMetadataLanguage":"en"}' >/dev/null
api GET /Startup/User >/dev/null
api POST /Startup/User "{\"Name\":\"${ADMIN_USER}\",\"Password\":\"${ADMIN_PASS}\"}" >/dev/null
api POST /Startup/RemoteAccess '{"EnableRemoteAccess":true,"EnableAutomaticPortMapping":false}' >/dev/null
api POST /Startup/Complete >/dev/null

TOKEN=$(api POST /Users/AuthenticateByName \
    "{\"Username\":\"${ADMIN_USER}\",\"Pw\":\"${ADMIN_PASS}\"}" | pluck "d['accesstoken']")
[[ -n "$TOKEN" ]] || { echo "   ❌ could not authenticate against the fresh server"; exit 1; }

api POST /Repositories \
    "[{\"Name\":\"${PLUGIN_NAME}\",\"Url\":\"${MANIFEST_URL}\",\"Enabled\":true}]" >/dev/null

# The manifest has to be reachable, parseable and compatible with this server:
# an entry whose targetAbi is above the server version is silently not offered.
OFFERED=""
deadline=$((SECONDS + 60))
while (( SECONDS < deadline )); do
    OFFERED=$(api GET /Packages | pluck "next((v['version'] for p in d
        if p.get('name') == '${PLUGIN_NAME}' for v in p.get('versions', [])
        if v.get('version') == '${EXPECTED}'), '')")
    [[ -n "$OFFERED" ]] && break
    sleep 3
done

if [[ -n "$OFFERED" ]]; then
    pass "the server offers ${PLUGIN_NAME} ${EXPECTED} from the manifest"
else
    fail "${PLUGIN_NAME} ${EXPECTED} is not offered to Jellyfin ${SERVER_VERSION} — wrong targetAbi, or the manifest is unreachable"
    exit 1
fi

# Jellyfin downloads sourceUrl and refuses the package unless its md5 matches
# the checksum in the manifest, so a failure here condemns the release itself.
api POST "/Packages/Installed/${PLUGIN_NAME}?version=${EXPECTED}" '' >/dev/null

deadline=$((SECONDS + 180))
while (( SECONDS < deadline )); do
    [[ -n "$(plugin_field version)" ]] && break
    sleep 3
done

if [[ -n "$(plugin_field version)" ]]; then
    pass "the package was downloaded, verified and unpacked"
else
    fail "the plugin never appeared in /Plugins — download or checksum verification failed"
    docker logs "$CONTAINER" 2>&1 | grep -iE "chaosflix|package|checksum" | tail -20
    exit 1
fi

api POST /System/Restart '' >/dev/null || true
sleep 5
wait_for_server

# The restart is only over once the plugin reports Active: the old process
# still answers for a moment, and until then it reports the pre-restart state.
deadline=$((SECONDS + 180))
while (( SECONDS < deadline )); do
    [[ "$(plugin_field status)" == "Active" ]] && break
    sleep 3
done

STATUS="$(plugin_field status)"
VERSION="$(plugin_field version)"

[[ "$STATUS" == "Active" ]] \
    && pass "the plugin is Active after the restart" \
    || fail "the plugin is '${STATUS}' after the restart, not Active"

[[ "$VERSION" == "$EXPECTED" ]] \
    && pass "the installed version is ${EXPECTED}" \
    || fail "the installed version is '${VERSION}', expected ${EXPECTED}"

# Active only means the assembly loaded; these two prove it registered what the
# plugin exists for.
CONFIG_STATUS="$(status_of "/Plugins/${PLUGIN_ID}/Configuration")"
[[ "$CONFIG_STATUS" == "200" ]] \
    && pass "the plugin configuration endpoint answers" \
    || fail "the plugin configuration endpoint answered ${CONFIG_STATUS}"

CHANNEL=$(api GET /Channels | pluck "next((c.get('name') for c in d['items']
    if c.get('name') == '${PLUGIN_NAME}'), '')")
[[ "$CHANNEL" == "$PLUGIN_NAME" ]] \
    && pass "the channel is registered" \
    || fail "the channel is missing from /Channels"

if (( FAILED )); then
    echo ""
    echo "❌ installing the published plugin failed"
    docker logs "$CONTAINER" 2>&1 | grep -iE "chaosflix|plugin|package" | tail -30
    exit 1
fi

echo "✅ ${PLUGIN_NAME} ${EXPECTED} installs and loads on Jellyfin ${SERVER_VERSION}"
