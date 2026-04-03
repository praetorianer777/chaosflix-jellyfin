# 📺 Chaosflix — CCC Media Plugin for Jellyfin

Browse and stream **Chaos Computer Club** conference talks directly in Jellyfin.

All content is sourced from [media.ccc.de](https://media.ccc.de) via their public API — no downloads, no local storage needed.

## Features

- 🎬 **Stream talks directly** from the CCC CDN — no server-side downloads
- 📂 **Browse by conference** — 38C3, Camp 2019, FOSSGIS, and hundreds more
- 🏷️ **Tags as genres** — filter by topic (security, ethics, hardware…)
- 👤 **Speaker metadata** — see all talks by a specific person
- 🕐 **Watch history & resume** — powered by Jellyfin (per-user, cross-device)
- 👥 **SyncPlay** — watch together with multiple users
- 🆕 **Latest talks** — see newly released recordings on your home screen
- ⚙️ **Quality & format preferences** — HD/SD, MP4/WebM, language selection
- 🌐 **Multi-language** — filter by original language or translation

## Installation

1. Build the plugin (see below)
2. Copy `Jellyfin.Plugin.Chaosflix.dll` to your Jellyfin plugin directory:
   - Linux: `~/.local/share/jellyfin/plugins/Chaosflix/`
   - Docker: `/config/plugins/Chaosflix/`
3. Restart Jellyfin
4. The **Chaosflix** channel appears under *My Media → Channels*

## Configuration

Go to **Dashboard → Plugins → Chaosflix** to set:

| Setting | Options | Default |
|---------|---------|---------|
| Preferred Quality | HD (1080p) / SD (576p) | HD |
| Preferred Format | MP4 (H.264) / WebM (VP9) | MP4 |
| Preferred Language | Original / Deutsch / English | Original |

## Building

```bash
# Build in Docker (no local .NET SDK needed)
docker run --rm -v "$PWD:/src" -w /src \
  mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet build Jellyfin.Plugin.Chaosflix/Jellyfin.Plugin.Chaosflix.csproj -c Release

# Output: Jellyfin.Plugin.Chaosflix/bin/Release/net9.0/Jellyfin.Plugin.Chaosflix.dll
```

## How It Works

```
Jellyfin UI  →  Chaosflix Channel  →  media.ccc.de API  →  CDN streaming
                     │
                     ├── Conferences as folders
                     ├── Talks as playable items
                     ├── Speaker/tag metadata
                     └── Multiple quality/language sources per talk
```

The plugin implements Jellyfin's `IChannel` interface. All heavy lifting (watch history, playback position, user management, transcoding, SyncPlay) is handled by Jellyfin itself.

## CCC API

This plugin uses the public [media.ccc.de API](https://api.media.ccc.de):

- `/public/conferences` — list all conferences
- `/public/conferences/{id}` — conference detail with events
- `/public/events/{guid}` — event detail with recordings
- `/public/events/search?q=` — full-text search

No API key required. Content is licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).

## License

MIT
