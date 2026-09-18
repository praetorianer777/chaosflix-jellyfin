# Android end-to-end tests

Drives the real **Jellyfin Android app** on an emulator against the fake
`media.ccc.de` stack from [`../`](../), so that the parts of the plugin that only
ever break on Android get covered: `CccApiClient.ResolveRedirectAsync` exists
because ExoPlayer struggles with cross-domain CDN redirects, and the
DirectStream and audio-mapping work in v0.0.26–v0.0.29 mostly showed up on
Android and Chromecast. The Playwright suite never touches any of it.

**This suite is opt-in.** `./run-tests.sh` gates every `git push`, and booting an
emulator takes minutes, so it only picks the suite up when `ANDROID_E2E=1` is
set. Run it directly instead:

```sh
e2e/android/run.sh            # everything: stack, emulator, app, flows, assertions
ANDROID_E2E=1 ./run-tests.sh  # or as the last step of the full suite
```

## What it covers

| Scenario | Where |
| --- | --- |
| Add the server (`10.0.2.2:8098`) and sign in | `flows/connect-and-login.yaml` |
| Find the Chaosflix channel, browse year → conference → talk | `flows/browse-to-talk.yaml` |
| Play in the native ExoPlayer and seek | `flows/player-play-and-seek.yaml` |
| No re-encoding, audio stream present, seek reaches the server | `verify-session.mjs` |

`verify-session.mjs` polls `/Sessions` while the flows run, so it judges a
session that is genuinely live.

Chromecast stays manual, as the issue expected: there is nothing to cast to on
an emulator.

## Maestro, not Appium

Maestro was chosen because the flows are plain YAML, there is no Appium server,
no driver matrix and no client library to keep in sync, and it talks to the
device over `adb` directly. It also matches the WebView's DOM ids
(`txtManualName`, `txtManualPassword`) as resource ids, which matters because
the Jellyfin Android app is jellyfin-web in a WebView plus a native ExoPlayer
fragment. Appium would buy scripting power this suite does not need.

Two Maestro details are load-bearing and explain the layout of `flows/`:

- Maestro force-stops the app between flows, and a freshly launched WebView does
  not expose its accessibility tree until something interacts with it. Splitting
  the scenarios into separate top-level flows therefore breaks on the second
  one. Everything runs as **one** flow, `chaosflix-android.yaml`, composed of
  subflows via `runFlow`.
- The subflows are untagged and the runner passes `--include-tags chaosflix`, so
  only the orchestrating flow is executed as a test.

## Requirements

- **KVM**: `/dev/kvm` must exist and be readable/writable for your user
  (`ls -l /dev/kvm`; add yourself to the `kvm` group otherwise). Without it the
  emulator is unusably slow and `run.sh` refuses to start.
- **Disk**: roughly 10 GB for the Android SDK plus the system image, in
  `~/.cache/chaosflix-android-e2e` (override with `ANDROID_E2E_HOME`). Nothing
  large lands in the repository.
- **Runtime**: Node 18+, a JDK 17+, `unzip`, Docker with Compose, and the
  .NET SDK (or Docker, which `build-artifacts.sh` falls back to).
- **Network**: first run downloads the Android command line tools, the system
  image, Maestro and the pinned Jellyfin APK.
- **Time**: about 10 minutes end to end on a warm cache, considerably more on
  the first run.

Everything except KVM and the host tools is installed by `run.sh` itself.

## How it runs locally

```sh
e2e/android/run.sh
```

1. Checks KVM and the host tools.
2. Builds the plugin and 180 s fixture videos into
   `e2e/android/.artifacts/stack`, then starts `../docker-compose.yml` as its
   own compose project (`chaosflix-e2e-android`) on port **8098**, so it does not
   collide with the Playwright stack on 8097.
3. Completes the Jellyfin wizard and points the plugin at the fake CCC API
   (`setup-server.mjs`).
4. Installs the Android SDK and creates the AVD if needed, boots it headless,
   installs Maestro and the pinned APK.
5. Starts `verify-session.mjs` in the background and runs the Maestro flow.
6. Fails if either the flow or the session assertions fail.

Useful switches:

| Variable | Effect |
| --- | --- |
| `E2E_REUSE_STACK=1` | do not start or stop docker compose |
| `ANDROID_E2E_KEEP=1` | leave the stack and the emulator running afterwards |
| `ANDROID_E2E_USE_RUNNING_EMULATOR=1` | use the device already attached to adb |
| `JELLYFIN_PORT` | host port of the test server (default 8098) |
| `JELLYFIN_ANDROID_VERSION` | APK release tag (default `v2.7.3`) |
| `E2E_FIXTURE_SECONDS` | fixture length (default 180) |
| `ANDROID_E2E_HOME` | where SDK, AVD, Maestro and APKs are cached |

On failure a screenshot and the last 400 logcat lines land in
`e2e/android/.artifacts/`, next to Maestro's JUnit report and the session log.

## How it runs in CI

The workflow lives in
[`.github/workflows/android-e2e.yml`](../../.github/workflows/android-e2e.yml);
it used to sit next to this suite in `e2e/android/ci/` and was moved into place
with `git mv` once a token with the `workflow` scope was available.

It runs nightly (03:17 UTC) and on demand via `workflow_dispatch`, never on
push — the normal CI workflow (`.github/workflows/ci.yml`) runs `run-tests.sh`
without `ANDROID_E2E`, so it never boots an emulator. It starts the stack on the runner, then hands
`reactivecircus/android-emulator-runner` the emulator part and lets it call
`e2e/android/run.sh` with `ANDROID_E2E_USE_RUNNING_EMULATOR=1` and
`E2E_REUSE_STACK=1`. The `Enable KVM` step is what makes the runner's `/dev/kvm`
usable; a runner without nested virtualisation cannot run this job at a useful
speed.

## Known limits

- **PlayMethod is `Transcode`, and that is expected.** jellyfin-android asks for
  DirectPlay first; the server answers `SupportsDirectPlay=false` for every
  channel item, so the app falls back to HLS. ffmpeg then only remuxes —
  `IsVideoDirect` and `IsAudioDirect` are both true and `TranscodeReasons` is
  `["DirectPlayError"]`. The assertion is therefore "nothing is re-encoded"
  rather than a literal `PlayMethod == "DirectStream"`: re-encoding is the
  regression worth catching, and a strict DirectStream check would only ever
  fail.
- **`PublishedServerUrl` must stay container-internal.** The plugin builds its
  proxy URLs from it and Jellyfin's own ffmpeg has to be able to fetch them.
  Pointing it at `10.0.2.2` makes every playback fall back to a real, failing
  transcode. The app reaches the server at `10.0.2.2` simply because that is
  what the connect screen was given.
- The fixture videos are 180 s here instead of the 5 s the Playwright suite
  uses; five seconds is not enough material to start a player, seek and observe
  a session through the UI. Both lengths come out of the same
  `build-artifacts.sh` via `E2E_FIXTURE_SECONDS`, and the fake CCC API reports
  the matching duration through `FIXTURE_SECONDS`.
- The suite drives one device, one API level (33) and one pinned app version. It
  is a regression net, not a compatibility matrix.
- Audio is asserted through the server's view of the session (an audio stream
  exists and `AudioStreamIndex` points at it). A headless emulator produces no
  sound to measure, so "audio is audible" is out of reach.
- Chromecast is not automated.
