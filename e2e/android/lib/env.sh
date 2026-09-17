#!/usr/bin/env bash
# Shared configuration for the Android e2e suite. Sourced, never executed.

ANDROID_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
E2E_DIR="$(cd "$ANDROID_DIR/.." && pwd)"
REPO_DIR="$(cd "$E2E_DIR/.." && pwd)"

# Everything downloadable lives outside the repo: the SDK alone is ~9 GB.
ANDROID_E2E_HOME="${ANDROID_E2E_HOME:-$HOME/.cache/chaosflix-android-e2e}"
export ANDROID_SDK_ROOT="${ANDROID_SDK_ROOT:-$ANDROID_E2E_HOME/sdk}"
export ANDROID_HOME="$ANDROID_SDK_ROOT"
export ANDROID_AVD_HOME="${ANDROID_AVD_HOME:-$ANDROID_E2E_HOME/avd}"
export MAESTRO_HOME="${MAESTRO_HOME:-$ANDROID_E2E_HOME/maestro}"
export PATH="$ANDROID_SDK_ROOT/platform-tools:$ANDROID_SDK_ROOT/cmdline-tools/latest/bin:$ANDROID_SDK_ROOT/emulator:$MAESTRO_HOME/bin:$PATH"

# Pinned so a new upstream release cannot turn a green suite red overnight.
JELLYFIN_ANDROID_VERSION="${JELLYFIN_ANDROID_VERSION:-v2.7.3}"
JELLYFIN_ANDROID_VARIANT="${JELLYFIN_ANDROID_VARIANT:-libre-release}"
JELLYFIN_ANDROID_PACKAGE="org.jellyfin.mobile"
MAESTRO_VERSION="${MAESTRO_VERSION:-2.10.0}"
export MAESTRO_CLI_NO_ANALYTICS=1
export MAESTRO_CLI_ANALYSIS_NOTIFICATION_DISABLED=true
ANDROID_API_LEVEL="${ANDROID_API_LEVEL:-33}"
ANDROID_SYSTEM_IMAGE="${ANDROID_SYSTEM_IMAGE:-system-images;android-${ANDROID_API_LEVEL};google_apis;x86_64}"
AVD_NAME="${AVD_NAME:-chaosflix-e2e}"
EMULATOR_PORT="${EMULATOR_PORT:-5560}"
EMULATOR_SERIAL="${EMULATOR_SERIAL:-}"
if [[ -z "$EMULATOR_SERIAL" ]]; then
	# In CI the emulator is started by android-emulator-runner on whatever port
	# it likes, so the attached device wins over our own naming.
	if [[ -n "${ANDROID_E2E_USE_RUNNING_EMULATOR:-}" ]] && command -v adb >/dev/null 2>&1; then
		EMULATOR_SERIAL="$(adb devices | awk '$2 == "device" { print $1; exit }')"
	fi
	EMULATOR_SERIAL="${EMULATOR_SERIAL:-emulator-${EMULATOR_PORT}}"
fi

# 8098 by default so the suite can run next to the Playwright stack on 8097.
export JELLYFIN_PORT="${JELLYFIN_PORT:-8098}"
# Own compose project, otherwise this stack and the Playwright one fight over
# the same containers and volumes.
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-chaosflix-e2e-android}"
JELLYFIN_URL="http://127.0.0.1:${JELLYFIN_PORT}"
# From inside the emulator the host is always 10.0.2.2.
JELLYFIN_URL_FROM_EMULATOR="http://10.0.2.2:${JELLYFIN_PORT}"

ARTIFACTS_DIR="$ANDROID_DIR/.artifacts"
APK_DIR="$ANDROID_E2E_HOME/apk"
APK_FILE="$APK_DIR/jellyfin-android-${JELLYFIN_ANDROID_VERSION}-${JELLYFIN_ANDROID_VARIANT}.apk"

log() { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*" >&2; }
die() { printf '\033[1;31m[fail]\033[0m %s\n' "$*" >&2; exit 1; }

# curl is not available everywhere this runs (and the SDK ships none), so
# downloads go through node, which the suite needs anyway.
download() {
	local url="$1" target="$2"
	mkdir -p "$(dirname "$target")"
	node -e '
		const fs = require("node:fs");
		const [url, target] = process.argv.slice(1);
		fetch(url, { redirect: "follow" }).then(async (response) => {
			if (!response.ok) throw new Error(`${response.status} ${url}`);
			fs.writeFileSync(target, Buffer.from(await response.arrayBuffer()));
		}).catch((error) => { console.error(String(error)); process.exit(1); });
	' "$url" "$target"
}
