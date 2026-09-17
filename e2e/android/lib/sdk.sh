#!/usr/bin/env bash
# Provisions the Android SDK, the AVD and the emulator process.

CMDLINE_TOOLS_URL="https://dl.google.com/android/repository/commandlinetools-linux-11076708_latest.zip"

sdk_install() {
	if [[ -x "$ANDROID_SDK_ROOT/cmdline-tools/latest/bin/sdkmanager" ]]; then
		log "Android SDK already present in $ANDROID_SDK_ROOT"
	else
		log "Installing Android command line tools into $ANDROID_SDK_ROOT"
		local zip="$ANDROID_E2E_HOME/cmdline-tools.zip" staging="$ANDROID_SDK_ROOT/cmdline-tools/.staging"
		[[ -f "$zip" ]] || download "$CMDLINE_TOOLS_URL" "$zip"
		mkdir -p "$staging"
		unzip -q -o "$zip" -d "$staging"
		mv "$staging/cmdline-tools" "$ANDROID_SDK_ROOT/cmdline-tools/latest"
		rmdir "$staging"
	fi

	yes | sdkmanager --sdk_root="$ANDROID_SDK_ROOT" --licenses >/dev/null 2>&1 || true
	log "Installing platform-tools, emulator and $ANDROID_SYSTEM_IMAGE (a few GB on first run)"
	sdkmanager --sdk_root="$ANDROID_SDK_ROOT" \
		"platform-tools" "emulator" "platforms;android-${ANDROID_API_LEVEL}" "$ANDROID_SYSTEM_IMAGE" \
		2>&1 | tr '\r' '\n' | grep -vE '^\s*\[|%\s*$' || true
}

avd_create() {
	mkdir -p "$ANDROID_AVD_HOME"
	if [[ -d "$ANDROID_AVD_HOME/$AVD_NAME.avd" ]]; then
		log "AVD $AVD_NAME already exists"
		return
	fi

	log "Creating AVD $AVD_NAME"
	echo no | avdmanager create avd -n "$AVD_NAME" -k "$ANDROID_SYSTEM_IMAGE" -d pixel_5 --force \
		2>&1 | tr '\r' '\n' | grep -vE '%\s*$' || true
	# ExoPlayer needs an audio sink to report a decoded audio track at all.
	cat >>"$ANDROID_AVD_HOME/$AVD_NAME.avd/config.ini" <<-'EOF'
		hw.lcd.density=440
		hw.ramSize=3072
		hw.audioInput=no
		hw.audioOutput=yes
		disk.dataPartition.size=6G
	EOF
}

emulator_running() {
	adb devices | grep -q "^${EMULATOR_SERIAL}\s*device$"
}

emulator_start() {
	if emulator_running; then
		log "Reusing the emulator already attached as $EMULATOR_SERIAL"
		return
	fi

	log "Booting $AVD_NAME headless on port $EMULATOR_PORT"
	mkdir -p "$ARTIFACTS_DIR"
	# shellcheck disable=SC2086
	nohup emulator -avd "$AVD_NAME" -port "$EMULATOR_PORT" \
		-no-window -no-boot-anim -no-snapshot -no-audio \
		-gpu swiftshader_indirect -accel on -wipe-data \
		${EMULATOR_EXTRA_ARGS:-} >"$ARTIFACTS_DIR/emulator.log" 2>&1 &
	echo $! >"$ARTIFACTS_DIR/emulator.pid"
}

emulator_wait() {
	local deadline=$((SECONDS + ${EMULATOR_BOOT_TIMEOUT:-600}))
	log "Waiting for $EMULATOR_SERIAL to finish booting"
	while ((SECONDS < deadline)); do
		if [[ "$(adb -s "$EMULATOR_SERIAL" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" == "1" ]]; then
			adb -s "$EMULATOR_SERIAL" shell input keyevent 82 >/dev/null 2>&1 || true
			# Animations make Maestro's waits unreliable and cost time.
			for scale in window_animation_scale transition_animation_scale animator_duration_scale; do
				adb -s "$EMULATOR_SERIAL" shell settings put global "$scale" 0 >/dev/null 2>&1 || true
			done
			log "Emulator is up"
			return
		fi
		sleep 5
	done

	tail -n 40 "$ARTIFACTS_DIR/emulator.log" >&2 || true
	die "the emulator did not boot within ${EMULATOR_BOOT_TIMEOUT:-600}s"
}

emulator_stop() {
	emulator_running || return 0
	log "Shutting the emulator down"
	adb -s "$EMULATOR_SERIAL" emu kill >/dev/null 2>&1 || true
}

apk_install() {
	if [[ ! -f "$APK_FILE" ]]; then
		log "Downloading jellyfin-android $JELLYFIN_ANDROID_VERSION ($JELLYFIN_ANDROID_VARIANT)"
		local name="jellyfin-android-${JELLYFIN_ANDROID_VERSION}-${JELLYFIN_ANDROID_VARIANT}.apk"
		download "https://github.com/jellyfin/jellyfin-android/releases/download/${JELLYFIN_ANDROID_VERSION}/${name}" "$APK_FILE" \
			|| die "could not download $name — check the version pin in lib/env.sh"
	fi

	log "Installing $JELLYFIN_ANDROID_PACKAGE"
	adb -s "$EMULATOR_SERIAL" install -r -g "$APK_FILE" >/dev/null \
		|| die "installing the APK failed"
	# Otherwise the app greets every launch with a snackbar over the UI.
	adb -s "$EMULATOR_SERIAL" shell dumpsys deviceidle whitelist "+$JELLYFIN_ANDROID_PACKAGE" >/dev/null 2>&1 || true
}

maestro_install() {
	if command -v maestro >/dev/null 2>&1; then
		log "Using maestro from PATH ($(command -v maestro))"
		return
	fi
	if [[ -x "$MAESTRO_HOME/bin/maestro" ]]; then
		return
	fi

	log "Installing Maestro $MAESTRO_VERSION"
	local zip="$ANDROID_E2E_HOME/maestro-${MAESTRO_VERSION}.zip"
	[[ -f "$zip" ]] || download \
		"https://github.com/mobile-dev-inc/Maestro/releases/download/cli-${MAESTRO_VERSION}/maestro.zip" "$zip" \
		|| die "could not download Maestro $MAESTRO_VERSION"
	mkdir -p "$ANDROID_E2E_HOME/.maestro-staging"
	unzip -q -o "$zip" -d "$ANDROID_E2E_HOME/.maestro-staging"
	mv "$ANDROID_E2E_HOME/.maestro-staging/maestro" "$MAESTRO_HOME"
	rmdir "$ANDROID_E2E_HOME/.maestro-staging"
	chmod +x "$MAESTRO_HOME/bin/maestro"
}
