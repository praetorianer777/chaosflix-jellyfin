#!/usr/bin/env bash
set -euo pipefail

# Android end-to-end suite: drives the real Jellyfin Android app (ExoPlayer) on
# an emulator against the fake media.ccc.de stack from e2e/.
#
# Opt-in only. It is never part of ./run-tests.sh unless ANDROID_E2E=1 is set,
# because booting an emulator takes minutes and run-tests.sh gates every push.
#
# Environment (see lib/env.sh for the full list):
#   JELLYFIN_PORT              host port of the test Jellyfin (default 8098)
#   E2E_REUSE_STACK=1          do not start/stop docker compose
#   ANDROID_E2E_KEEP=1         leave the stack and the emulator running
#   ANDROID_E2E_USE_RUNNING_EMULATOR=1
#                              a device is already attached (CI: android-emulator-runner)

cd "$(dirname "$0")"
source lib/env.sh
source lib/sdk.sh

export E2E_ARTIFACTS_DIR="$ARTIFACTS_DIR/stack"
# Five seconds is not enough material to start a player, seek and observe a
# session through the UI, so the Android stack builds longer fixtures.
export E2E_FIXTURE_SECONDS="${E2E_FIXTURE_SECONDS:-180}"
# PublishedServerUrl deliberately stays at the container-internal address: the
# plugin builds its proxy URLs from it, and Jellyfin's own ffmpeg has to be able
# to reach them. The app only ever talks to 10.0.2.2 because that is what the
# user typed on the connect screen.

preflight() {
	command -v node >/dev/null || die "node is required"
	command -v java >/dev/null || die "a JDK (17 or newer) is required for the Android SDK tools"
	command -v unzip >/dev/null || die "unzip is required"
	if [[ -z "${ANDROID_E2E_USE_RUNNING_EMULATOR:-}" ]]; then
		[[ -e /dev/kvm ]] || die "/dev/kvm is missing — without KVM the emulator is unusably slow. See README.md."
		[[ -r /dev/kvm && -w /dev/kvm ]] || die "/dev/kvm exists but is not accessible for $(id -un); add the user to the 'kvm' group."
	fi
}

stack_up() {
	[[ -n "${E2E_REUSE_STACK:-}" ]] && return 0
	log "Building plugin and ${E2E_FIXTURE_SECONDS}s fixture videos"
	"$E2E_DIR/scripts/build-artifacts.sh"
	log "Starting the Jellyfin stack on $JELLYFIN_URL (compose project $COMPOSE_PROJECT_NAME)"
	(cd "$E2E_DIR" && docker compose up -d --wait)
}

stack_down() {
	[[ -n "${E2E_REUSE_STACK:-}" || -n "${ANDROID_E2E_KEEP:-}" ]] && return 0
	(cd "$E2E_DIR" && docker compose down -v) || true
}

cleanup() {
	local status=$?
	if ((status != 0)); then
		mkdir -p "$ARTIFACTS_DIR"
		adb -s "$EMULATOR_SERIAL" exec-out screencap -p >"$ARTIFACTS_DIR/failure.png" 2>/dev/null || true
		adb -s "$EMULATOR_SERIAL" logcat -d -t 400 >"$ARTIFACTS_DIR/logcat.txt" 2>/dev/null || true
		warn "Failure artifacts in $ARTIFACTS_DIR"
	fi
	if [[ -z "${ANDROID_E2E_KEEP:-}" ]]; then
		emulator_stop
	fi
	stack_down
	exit $status
}

preflight
trap cleanup EXIT

stack_up
log "Preparing the Jellyfin instance"
TOKEN="$(node setup-server.mjs | tail -n 1)"

if [[ -z "${ANDROID_E2E_USE_RUNNING_EMULATOR:-}" ]]; then
	sdk_install
	avd_create
	emulator_start
fi
emulator_wait
maestro_install
apk_install

# The verifier polls /Sessions while the flow drives the app; the app only
# reports a live session for as long as it is actually playing.
mkdir -p "$ARTIFACTS_DIR"
JELLYFIN_TOKEN="$TOKEN" node verify-session.mjs >"$ARTIFACTS_DIR/session.log" 2>&1 &
VERIFIER=$!

log "Running Maestro flows"
set +e
maestro --device "$EMULATOR_SERIAL" test \
	--env SERVER_URL="$JELLYFIN_URL_FROM_EMULATOR" \
	--env USERNAME="e2e-admin" \
	--env PASSWORD="chaosflix-e2e" \
	--env CONFERENCE="E2E Congress 2025" \
	--env TALK="Three stream talk" \
	--format junit --output "$ARTIFACTS_DIR/maestro-report.xml" \
	--include-tags chaosflix \
	flows
FLOW_STATUS=$?
set -e

# Without a playing app the verifier would poll until its own timeout.
((FLOW_STATUS == 0)) || kill "$VERIFIER" 2>/dev/null || true
wait "$VERIFIER" && VERIFY_STATUS=0 || VERIFY_STATUS=$?
cat "$ARTIFACTS_DIR/session.log"

((FLOW_STATUS == 0)) || die "Maestro flows failed (report: $ARTIFACTS_DIR/maestro-report.xml)"
((VERIFY_STATUS == 0)) || die "the playback session did not satisfy the assertions above"

log "✅ Android e2e suite passed"
