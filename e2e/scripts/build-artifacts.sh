#!/usr/bin/env bash
set -euo pipefail

# Builds everything the e2e stack mounts: the plugin DLL plus meta.json, and
# short fixture videos. Videos are only rebuilt when missing or when the
# requested length changed.
#
# E2E_ARTIFACTS_DIR   where to put them (default e2e/.artifacts)
# E2E_FIXTURE_SECONDS length of the fixture videos (default 5); must match
#                     FIXTURE_SECONDS handed to the fake CCC API.

cd "$(dirname "$0")/.."
ROOT="$(cd .. && pwd)"
ARTIFACTS="${E2E_ARTIFACTS_DIR:-$(pwd)/.artifacts}"
PLUGIN_DIR="$ARTIFACTS/plugin"
MEDIA_DIR="$ARTIFACTS/media"
SECONDS_PER_FIXTURE="${E2E_FIXTURE_SECONDS:-5}"
TFM=$(grep -oP '<TargetFramework>net\K[0-9.]+' "$ROOT/Jellyfin.Plugin.Chaosflix/Jellyfin.Plugin.Chaosflix.csproj")

mkdir -p "$PLUGIN_DIR" "$MEDIA_DIR"

echo "🔨 Publishing plugin into $PLUGIN_DIR"
if command -v dotnet &>/dev/null; then
    dotnet publish "$ROOT/Jellyfin.Plugin.Chaosflix/Jellyfin.Plugin.Chaosflix.csproj" -c Release -o "$PLUGIN_DIR"
else
    mkdir -p "${HOME}/.nuget/packages"
    docker run --rm \
        --user "$(id -u):$(id -g)" \
        -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -e DOTNET_NOLOGO=1 -e NUGET_PACKAGES=/nuget \
        -v "${HOME}/.nuget/packages:/nuget" \
        -v "$ROOT:/src" -v "$PLUGIN_DIR:/out" -w /src \
        "mcr.microsoft.com/dotnet/sdk:${TFM}" \
        dotnet publish Jellyfin.Plugin.Chaosflix/Jellyfin.Plugin.Chaosflix.csproj -c Release -o /out
fi
cp "$ROOT/Jellyfin.Plugin.Chaosflix/meta.json" "$PLUGIN_DIR/"

ffmpeg_run() {
    if command -v ffmpeg &>/dev/null; then
        ffmpeg "$@"
    else
        # The image ships jellyfin-ffmpeg, and nothing named ffmpeg is on PATH.
        docker run --rm --user "$(id -u):$(id -g)" -v "$MEDIA_DIR:/media" \
            --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg \
            jellyfin/jellyfin:"${JELLYFIN_TAG:-10.11.7}" "$@"
    fi
}

# A talk with an extra video stream for the visually impaired is what broke
# audio in v0.0.28; both layouts are therefore part of the fixtures.
STAMP="$MEDIA_DIR/.length"
if [[ ! -f "$MEDIA_DIR/three-stream.mp4" || "$(cat "$STAMP" 2>/dev/null)" != "$SECONDS_PER_FIXTURE" ]]; then
    echo "🎬 Generating fixture videos"
    ffmpeg_run -y -loglevel error \
        -f lavfi -i "testsrc=size=640x360:rate=25:duration=$SECONDS_PER_FIXTURE" \
        -f lavfi -i "sine=frequency=440:duration=$SECONDS_PER_FIXTURE" \
        -c:v libx264 -pix_fmt yuv420p -c:a aac -movflags +faststart \
        "$MEDIA_DIR/two-stream.mp4"

    ffmpeg_run -y -loglevel error \
        -f lavfi -i "testsrc=size=640x360:rate=25:duration=$SECONDS_PER_FIXTURE" \
        -f lavfi -i "testsrc2=size=320x180:rate=25:duration=$SECONDS_PER_FIXTURE" \
        -f lavfi -i "sine=frequency=440:duration=$SECONDS_PER_FIXTURE" \
        -map 0:v -map 1:v -map 2:a \
        -c:v libx264 -pix_fmt yuv420p -c:a aac -movflags +faststart \
        "$MEDIA_DIR/three-stream.mp4"

    for name in two-stream three-stream; do
        ffmpeg_run -y -loglevel error -i "$MEDIA_DIR/${name}.mp4" \
            -c:v libvpx-vp9 -b:v 300k -c:a libopus "$MEDIA_DIR/${name}.webm"
    done

    ffmpeg_run -y -loglevel error -f lavfi -i "color=c=blue:size=320x180:duration=1" -frames:v 1 "$MEDIA_DIR/poster.png"
    cp "$MEDIA_DIR/poster.png" "$MEDIA_DIR/thumb.png"
    cp "$MEDIA_DIR/poster.png" "$MEDIA_DIR/logo.png"
    echo "$SECONDS_PER_FIXTURE" > "$STAMP"
fi

echo "✅ Artifacts ready"
