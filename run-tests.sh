#!/usr/bin/env bash
set -euo pipefail

# Runs every test suite in the repo; exits non-zero on the first failure.
#   1. Shell script tests in tests/ (fast, offline, no SDK)
#   2. Release build of the plugin (compile check)
#   3. All *.Tests.csproj projects via dotnet test
#   4. Playwright e2e tests in e2e/ (if present)
# Uses a local dotnet SDK when available, otherwise the SDK Docker image.

cd "$(dirname "$0")"

PLUGIN="Jellyfin.Plugin.Chaosflix/Jellyfin.Plugin.Chaosflix.csproj"
TFM=$(grep -oP '<TargetFramework>net\K[0-9.]+' "$PLUGIN")

dotnet_run() {
    if command -v dotnet &>/dev/null; then
        dotnet "$@"
    else
        mkdir -p "${HOME}/.nuget/packages"
        docker run --rm \
            --user "$(id -u):$(id -g)" \
            -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -e DOTNET_NOLOGO=1 \
            -e NUGET_PACKAGES=/nuget \
            -v "${HOME}/.nuget/packages:/nuget" \
            -v "$(pwd):/src" -w /src \
            "mcr.microsoft.com/dotnet/sdk:${TFM}" \
            dotnet "$@"
    fi
}

echo "🐚 Shell script tests"
tests/test-release.sh

echo "🔨 Building plugin (net${TFM})..."
dotnet_run build "$PLUGIN" -c Release

# .claude/worktrees holds full checkouts of other branches; testing those here
# would run someone else's code and report it as this branch's result.
mapfile -t TEST_PROJECTS < <(find . -name '*.Tests.csproj' -not -path './e2e/*' -not -path './.claude/*' | sort)
if (( ${#TEST_PROJECTS[@]} == 0 )); then
    echo "⚠️  No .NET test projects found (see #11)"
fi
for project in "${TEST_PROJECTS[@]}"; do
    echo "🧪 dotnet test ${project}"
    dotnet_run test "$project" -c Release
done

if [[ -f e2e/package.json ]]; then
    echo "🎭 Playwright e2e tests"
    (cd e2e && npm ci && npx playwright test)
else
    echo "⚠️  No e2e/ Playwright suite found (see #11)"
fi

# The Android suite boots an emulator, which takes minutes. This script gates
# every push, so it only runs when explicitly asked for (see e2e/android/README.md).
if [[ -n "${ANDROID_E2E:-}" && -x e2e/android/run.sh ]]; then
    echo "🤖 Android e2e tests"
    e2e/android/run.sh
else
    echo "⏭️  Android e2e tests skipped (set ANDROID_E2E=1 to include them)"
fi

echo "✅ All tests passed"
