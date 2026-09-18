#!/usr/bin/env bash
set -euo pipefail

# Boots an Android TV emulator with jellyfin-androidtv installed and points it
# at the same stack the phone suite uses, so #55 (a talk that plays everywhere
# except in the TV app) can be reproduced by hand.
#
# This is a reproduction harness, not a test: it sets everything up and then
# leaves the emulator running for you to drive. The regression this found is
# covered without an emulator by the "Android TV client" test in
# e2e/tests/profiles.spec.ts. Nothing here runs from ./run-tests.sh.
#
# Drive it afterwards with adb, e.g.
#   adb -s "$TV_SERIAL" shell input keyevent KEYCODE_DPAD_DOWN
#   adb -s "$TV_SERIAL" shell input keyevent KEYCODE_DPAD_CENTER
#   adb -s "$TV_SERIAL" exec-out screencap -p >/tmp/tv.png
#   adb -s "$TV_SERIAL" logcat -s PlaybackController:* PlaybackManager:* VideoManager:*

cd "$(dirname "$0")"
source lib/env.sh
source lib/sdk.sh

TV_AVD="${TV_AVD:-chaosflix-e2e-tv}"
# The phone suite's google_apis image has no leanback launcher, and the TV
# images stop at x86 for this API level. The app ships x86 native libraries.
TV_SYSTEM_IMAGE="${TV_SYSTEM_IMAGE:-system-images;android-34;android-tv;x86}"
TV_PORT="${TV_PORT:-5570}"
TV_SERIAL="${TV_SERIAL:-emulator-$TV_PORT}"
JELLYFIN_ANDROIDTV_VERSION="${JELLYFIN_ANDROIDTV_VERSION:-v0.19.10}"
TV_APK="$APK_DIR/jellyfin-androidtv-${JELLYFIN_ANDROIDTV_VERSION}-release.apk"

export E2E_ARTIFACTS_DIR="$ARTIFACTS_DIR/stack"
export E2E_FIXTURE_SECONDS="${E2E_FIXTURE_SECONDS:-180}"

command -v node >/dev/null || die "node is required"
command -v java >/dev/null || die "a JDK (17 or newer) is required for the Android SDK tools"
[[ -r /dev/kvm && -w /dev/kvm ]] || die "/dev/kvm is missing or inaccessible — see README.md"

if [[ -z "${E2E_REUSE_STACK:-}" ]]; then
	log "Building plugin and ${E2E_FIXTURE_SECONDS}s fixture videos"
	"$E2E_DIR/scripts/build-artifacts.sh"
	log "Starting the Jellyfin stack on $JELLYFIN_URL"
	(cd "$E2E_DIR" && docker compose up -d --wait)
fi

log "Preparing the Jellyfin instance"
node setup-server.mjs >/dev/null

log "Installing the Android SDK and $TV_SYSTEM_IMAGE"
ANDROID_SYSTEM_IMAGE="$TV_SYSTEM_IMAGE" ANDROID_API_LEVEL=34 sdk_install

if [[ ! -d "$ANDROID_AVD_HOME/$TV_AVD.avd" ]]; then
	log "Creating AVD $TV_AVD"
	echo no | avdmanager create avd -n "$TV_AVD" -k "$TV_SYSTEM_IMAGE" -d tv_1080p --force \
		2>&1 | tr '\r' '\n' | grep -vE '%\s*$' || true
	cat >>"$ANDROID_AVD_HOME/$TV_AVD.avd/config.ini" <<-'EOF'
		hw.ramSize=3072
		hw.audioInput=no
		hw.audioOutput=yes
		disk.dataPartition.size=6G
	EOF
fi

if ! adb devices | grep -q "^${TV_SERIAL}[[:space:]]*device$"; then
	log "Booting $TV_AVD headless on port $TV_PORT"
	mkdir -p "$ARTIFACTS_DIR"
	nohup emulator -avd "$TV_AVD" -port "$TV_PORT" \
		-no-window -no-boot-anim -no-snapshot -no-audio \
		-gpu swiftshader_indirect -accel on -wipe-data \
		>"$ARTIFACTS_DIR/emulator-tv.log" 2>&1 &
fi

EMULATOR_SERIAL="$TV_SERIAL" emulator_wait

if [[ ! -f "$TV_APK" ]]; then
	log "Downloading jellyfin-androidtv $JELLYFIN_ANDROIDTV_VERSION"
	download \
		"https://github.com/jellyfin/jellyfin-androidtv/releases/download/${JELLYFIN_ANDROIDTV_VERSION}/jellyfin-androidtv-${JELLYFIN_ANDROIDTV_VERSION}-release.apk" \
		"$TV_APK" || die "could not download the jellyfin-androidtv APK"
fi

log "Installing org.jellyfin.androidtv"
adb -s "$TV_SERIAL" install -r -g "$TV_APK" >/dev/null || die "installing the APK failed"
# BACK on the connect screen leaves the app for the launcher, which starts
# YouTube and hides the emulator's actual state.
adb -s "$TV_SERIAL" shell pm disable-user --user 0 com.google.android.youtube.tv >/dev/null 2>&1 || true
adb -s "$TV_SERIAL" shell monkey -p org.jellyfin.androidtv -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1 || true

cat <<EOF

Android TV emulator ready.

  serial : $TV_SERIAL
  server : $JELLYFIN_URL_FROM_EMULATOR   (user e2e-admin / chaosflix-e2e)

Sign in with "Use a password"; the on-screen keyboard swallows the D-pad, so
commit each field with KEYCODE_ENTER rather than KEYCODE_DPAD_DOWN.
EOF
