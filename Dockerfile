FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Support corporate SSL proxies: place .crt files in certs/ directory
COPY certs/ /tmp/extra-certs/
RUN if ls /tmp/extra-certs/*.crt 1>/dev/null 2>&1; then \
      cp /tmp/extra-certs/*.crt /usr/local/share/ca-certificates/ && \
      update-ca-certificates; \
    fi

COPY Directory.Build.props .
COPY Jellyfin.Plugin.Chaosflix/ Jellyfin.Plugin.Chaosflix/
RUN dotnet restore Jellyfin.Plugin.Chaosflix/Jellyfin.Plugin.Chaosflix.csproj
RUN dotnet build Jellyfin.Plugin.Chaosflix/Jellyfin.Plugin.Chaosflix.csproj -c Release --no-restore
RUN dotnet publish Jellyfin.Plugin.Chaosflix/Jellyfin.Plugin.Chaosflix.csproj -c Release --no-build -o /out \
    && cp Jellyfin.Plugin.Chaosflix/meta.json /out/

FROM scratch AS artifact
COPY --from=build /out/ /
