#!/usr/bin/env bash
set -euo pipefail

# Runs every test suite in the repo; exits non-zero on the first failure.
#   1. Release build of the plugin (compile check)
#   2. All *.Tests.csproj projects via dotnet test
#   3. Playwright e2e tests in e2e/ (if present)
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

echo "🔨 Building plugin (net${TFM})..."
dotnet_run build "$PLUGIN" -c Release

mapfile -t TEST_PROJECTS < <(find . -name '*.Tests.csproj' -not -path './e2e/*' | sort)
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

echo "✅ All tests passed"
