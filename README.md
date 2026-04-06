# 📺 Chaosflix — CCC Media Plugin for Jellyfin

Browse and stream **Chaos Computer Club** conference talks directly in Jellyfin.

All content is sourced from [media.ccc.de](https://media.ccc.de) via their public API — no downloads, no local storage needed.

## Features

- 🎬 **Stream talks directly** from the CCC CDN — no server-side downloads
- 🔥 **Popular Talks** — most viewed talks across conferences
- ⭐ **Recommended** — trending talks ranked by views and recency
- 📅 **Browse by Year** — conferences grouped by year (2024 → 38C3, Camp…)
- 📂 **Browse by Conference** — 38C3, Camp 2019, FOSSGIS, and hundreds more
- 🔗 **Related Talks** — discover similar talks via CCC's weighted recommendations
- 🏷️ **Tags as Genres** — filter by topic (security, ethics, hardware…)
- 👤 **Speaker metadata** — see all talks by a specific person
- 🕐 **Watch history & resume** — powered by Jellyfin (per-user, cross-device)
- 👥 **SyncPlay** — watch together with multiple users
- 🆕 **Latest talks** — newly released recordings on your home screen
- 🔄 **Scheduled sync** — background task keeps cache fresh (every 6h)
- ⚙️ **Quality & format preferences** — HD/SD, MP4/WebM, language selection

## Installation

### Option 1: Jellyfin Plugin Repository (recommended)

1. Open Jellyfin **Dashboard → Plugins → Repositories**
2. Click **Add** and enter:
   - **Name:** `Chaosflix`
   - **URL:** `https://raw.githubusercontent.com/praetorianer777/chaosflix-jellyfin/main/manifest.json`
3. Go to **Catalog → Channels** and install **Chaosflix**
4. Restart Jellyfin
5. The **Chaosflix** channel appears under **Home → My Media → Channels**

### Option 2: Manual Installation

1. Build the plugin (see [Building](#building) below)
2. Create the plugin directory and copy files:
   ```bash
   # Linux (standalone)
   mkdir -p ~/.local/share/jellyfin/plugins/Chaosflix
   cp artifacts/Jellyfin.Plugin.Chaosflix.dll artifacts/meta.json \
      ~/.local/share/jellyfin/plugins/Chaosflix/

   # Docker
   mkdir -p /path/to/jellyfin-config/plugins/Chaosflix
   cp artifacts/Jellyfin.Plugin.Chaosflix.dll artifacts/meta.json \
      /path/to/jellyfin-config/plugins/Chaosflix/
   ```
3. Restart Jellyfin
4. The **Chaosflix** channel appears under **Home → My Media → Channels**

## Channel Structure

```
Chaosflix
├── 🔥 Popular Talks          ← Top 50 by view count
├── ⭐ Recommended             ← Trending (views × recency)
└── 📅 Browse by Year
    ├── 2024
    │   ├── 38C3: Illegal Instructions
    │   │   ├── Talk 1  ▶️
    │   │   ├── Talk 2  ▶️
    │   │   └── ...
    │   ├── FOSSGIS 2024
    │   └── ...
    ├── 2023
    │   ├── 37C3: Unlocked
    │   └── ...
    └── ...
```

## Configuration

Go to **Dashboard → Plugins → Chaosflix** to set:

| Setting | Options | Default |
|---------|---------|---------|
| Preferred Quality | HD (1080p) / SD (576p) | HD |
| Preferred Format | MP4 (H.264) / WebM (VP9) | MP4 |
| Preferred Language | Original / Deutsch / English | Original |

### Scheduled Sync

The plugin automatically syncs conference data every **6 hours** via a scheduled task.
You can trigger a manual sync in **Dashboard → Scheduled Tasks → Chaosflix: Sync CCC Media**.

## Building

### With Dockerfile (recommended)

```bash
# Standard build — outputs to ./artifacts/
docker build --target artifact --output type=local,dest=./artifacts .

# Behind a corporate SSL proxy? Place .crt files in certs/ first:
cp /usr/local/share/ca-certificates/*.crt certs/
docker build --target artifact --output type=local,dest=./artifacts .
```

### Without Docker

```bash
dotnet publish Jellyfin.Plugin.Chaosflix/Jellyfin.Plugin.Chaosflix.csproj -c Release -o ./artifacts
```

### Rebuilding for a new Jellyfin Version

The plugin must be compiled against the same Jellyfin SDK version as your server.

#### Automatisch (empfohlen)

```bash
# Auto-detect latest Jellyfin version from NuGet
./upgrade-jellyfin.sh

# Or specify a version manually
./upgrade-jellyfin.sh 10.12.0
```

Das Script:
1. Updated NuGet-Pakete im `.csproj`
2. Updated `targetAbi` in `manifest.json`
3. Macht einen Test-Build via Docker
4. Zeigt Fehler + Lösungsvorschläge bei Breaking Changes

Danach:
```bash
git add -A && git commit -m "chore: upgrade to Jellyfin 10.12.0"
./release.sh 0.1.0 "Upgrade to Jellyfin 10.12.0"
git push origin main --tags
# Upload ZIP auf GitHub Release
```

#### Manuell

```bash
# 1. Check your Jellyfin server version (Dashboard → General)

# 2. Update the SDK references in the .csproj
sed -i 's/Version="10.11.7"/Version="10.12.0"/g' \
    Jellyfin.Plugin.Chaosflix/Jellyfin.Plugin.Chaosflix.csproj

# 3. Update targetAbi in manifest.json
sed -i 's/"targetAbi": "10.11.0.0"/"targetAbi": "10.12.0.0"/g' manifest.json

# 4. Build and release
./release.sh 0.1.0 "Upgrade to Jellyfin 10.12.0"
```

#### Was bei Major-Updates brechen kann

| Änderung | Symptom | Fix |
|----------|---------|-----|
| Target Framework (net9→net10) | `TargetFramework 'net9.0' is not supported` | `.csproj` + Docker SDK-Image updaten |
| Namespace-Umbenennung | `The type or namespace 'X' does not exist` | `using`-Statements anpassen |
| API-Signatur-Änderung | `does not contain a definition for 'X'` | Jellyfin Release Notes lesen, Code anpassen |
| DI-Registration | Plugin wird nicht geladen | `ChaosflixServiceRegistrator.cs` prüfen |

> **Tip:** Check available SDK versions: https://www.nuget.org/packages/Jellyfin.Controller
> Check release notes: https://github.com/jellyfin/jellyfin/releases

### Creating a Release (automated)

```bash
./release.sh <version> "<changelog>"

# Example:
./release.sh 0.0.2 "Rebuild for Jellyfin 10.12"
```

This updates all version strings, builds, creates the ZIP, and commits + tags.

## How It Works

```
Jellyfin UI  →  Chaosflix Channel  →  media.ccc.de API  →  CDN streaming
                     │
                     ├── 🔥 Popular (top by views)
                     ├── ⭐ Recommended (views × recency)
                     ├── 📅 Year → Conference → Talks
                     ├── 🔗 Related talks per event
                     ├── Speaker/tag metadata
                     └── Multiple quality/language sources per talk
```

The plugin implements Jellyfin's `IChannel` interface. All heavy lifting (watch history, playback position, user management, transcoding, SyncPlay) is handled by Jellyfin itself.

## CCC API

This plugin uses the public [media.ccc.de API](https://api.media.ccc.de):

- `/public/conferences` — list all conferences
- `/public/conferences/{id}` — conference detail with events
- `/public/events/{guid}` — event detail with recordings + related talks
- `/public/events/search?q=` — full-text search

No API key required. Content is licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).

## License

MIT
