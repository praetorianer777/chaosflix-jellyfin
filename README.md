# 📺 Chaosflix — CCC Media Plugin for Jellyfin

[![CI](https://github.com/praetorianer777/chaosflix-jellyfin/actions/workflows/ci.yml/badge.svg)](https://github.com/praetorianer777/chaosflix-jellyfin/actions/workflows/ci.yml)

Browse and stream **Chaos Computer Club** conference talks directly in Jellyfin.

All content is sourced from [media.ccc.de](https://media.ccc.de) via their public API — no downloads, no local storage needed.

## Features

- 🎬 **Stream talks** from the CCC CDN through the server — no downloads, no local storage
- 🔴 **Live now** — while a congress is running, the rooms on air with what is
  playing in them; the folder is absent the rest of the year
- 🔥 **Popular Talks** — most viewed talks across conferences
- ⭐ **Recommended** — trending talks ranked by views and recency
- 📅 **Browse by Year** — conferences grouped by year (2024 → 38C3, Camp…)
- 📂 **Browse by Conference** — 38C3, Camp 2019, FOSSGIS, and hundreds more
- 🔗 **Related Talks** — CCC's weighted recommendations fill the "More like this"
  row of any client, for the talks your library holds
- 🏷️ **Tags as Genres** — filter by topic (security, ethics, hardware…)
- 👤 **Browse by speaker** — every talk a person has given, across all conferences
- 🕐 **Watch history & resume** — powered by Jellyfin (per-user, cross-device)
- 👥 **SyncPlay** — watch together with multiple users
- 🆕 **Latest talks** — newly released recordings on your home screen
- 🔄 **Scheduled sync** — background task keeps cache fresh (every 6h)
- ⚙️ **Quality & format preferences** — HD/SD, MP4/WebM, language selection
- 🎚️ **Every recording selectable** — pick quality or language per playback from the player's version list

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

## Troubleshooting

**The channel is missing for one user.** Jellyfin decides per user which channels
exist at all: Dashboard → Users → _user_ → Access → **Channel Access**. When
Chaosflix is not ticked the server leaves it out of that user's views entirely,
so clients show nothing rather than an error. New users get access to all
channels by default, so this only bites where an admin once picked channels by
hand — a plugin installed afterwards is not added to that list.

**The channel is missing on one device but visible elsewhere.** The Android TV
app builds its library row once and registers no refresh trigger, so a client
that has been running since before the plugin was installed keeps its old set of
libraries. Force-stop the app (Settings → Apps → Jellyfin → Force stop) and
reopen it; returning to the launcher is not enough.

**A talk does not play.** Dashboard → Plugins → Chaosflix shows what the plugin
believes: whether media.ccc.de answers, what is cached, and a **check a talk**
box that resolves one talk end to end — API lookup, recording choice, signed
proxy url, proxy reachability and stream probe — and names the stage that fails.

## Channel Structure

```
Chaosflix
├── 🔴 Live now               ← only while a congress is on air
│   ├── Saal 1: current talk  ▶️
│   └── Saal ZIGZAG: …        ▶️
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

### Speakers and topics

Talks are not only reachable through the folders. Every talk carries its
speakers and its tags, and Jellyfin indexes both, so two more ways in exist
without the channel offering a folder for them.

**By speaker** works in any client: open a talk, click a name in its cast list,
and you get that person's other talks — across every conference your library
holds, not just the one you came from. Over the API that is
`/Persons` for the list and `/Items?personIds=<id>&recursive=true` for one
person's talks.

**By topic** works as a filter, but there is no list to pick from:
`/Items?genres=Security` returns the right talks, while Jellyfin's genre
browse stays empty for them — see Known Limitations.

Neither costs anything: they use the talks already in the library rather than
listing them a second time, which a channel folder would (see Known
Limitations). Which talks are in the library is decided by the **Conferences**
setting below.

## Configuration

Go to **Dashboard → Plugins → Chaosflix** to set:

| Setting | Options | Default |
|---------|---------|---------|
| Preferred Quality | HD (1080p) / SD (576p) | HD |
| Preferred Format | MP4 (H.264) / WebM (VP9) | MP4 |
| Preferred Language | Original / Deutsch / English | Original |
| Start on a version every client can play untouched | on / off | off |
| Conferences | comma-separated list | empty — every conference |
| Streaming Endpoint | url | empty — streaming.media.ccc.de |

**Conferences** narrows the whole channel to what you actually follow.
media.ccc.de publishes hundreds of them, and Browse by Year lists every one.
An entry is either a series as it appears in a media.ccc.de address —
`congress` for `media.ccc.de/c/congress/2025`, and likewise `gpn`,
`easterhegg` — which keeps every edition including the ones not announced yet,
or a single acronym like `38c3`. The filter also applies to Popular,
Recommended and the latest row, so they are drawn from the same conferences as
the folders, and the plugin stops fetching the details of everything else.
Left empty, nothing is filtered.

**Streaming Endpoint** is where the plugin asks which rooms are on air, which
is what fills 🔴 Live now. Left empty it uses the public one at
`streaming.media.ccc.de`; point it elsewhere for a mirror, or at a local
stand-in when testing. Outside a congress the endpoint reports nothing and the
folder is not shown at all.

Naming your conferences also makes their talks **findable by name**. Jellyfin
creates a channel item the first time something asks for the folder holding it
and at no other time, so a talk nobody has browsed to cannot be searched for.
The scheduled sync walks the conferences you named once per run, which puts
them in the library and therefore in Jellyfin's search. With no conferences
named it walks the 20 most recent ones instead — expanding all of media.ccc.de
four times a day would be a poor way to treat an API the CCC runs on donated
time.

Every recording of a talk is offered as its own version ("HD MP4 · Deutsch",
"SD WebM · English") in the player's version selector; these settings decide
which of them is the default. A viewer can pick another one per playback and
per device without changing anything here.

The order is the same for every client: Jellyfin builds a talk's version list
once and hands it to whoever asks next, so the plugin cannot sort it per
device. That matters for WebM, because a client whose only transcoding video
codec is H.264 — the Android app — has to re-encode VP9/Opus for the whole
talk. Preferring MP4, or the setting above, keeps the default version one that
every client streams untouched while WebM stays selectable.

### Status

The same page reports what the plugin currently believes to be true: the
endpoint in effect, when the sync task last ran and how long it took, what the
API and probe caches hold and how often they answer. Three actions cost
something and therefore only run when clicked — **Check endpoint now** (does
the endpoint answer, and how fast), **Clear caches** (the same invalidation a
configuration change triggers) and **Check a talk**, which resolves one talk
the way playback does (API lookup → recording choice → signed proxy url →
proxy request → probe) and names the stage that fails.

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
./upgrade-jellyfin.sh 12.1.0
```

Das Script:
1. Updated NuGet-Pakete im `.csproj`
2. Updated `targetAbi` in `meta.json` — `release.sh` trägt ihn in `manifest.json` nach
3. Zieht Target Framework und Docker-SDK mit, wenn der Major das verlangt
4. Macht einen Test-Build via Docker
5. Zeigt Fehler + Lösungsvorschläge bei Breaking Changes

Danach:
```bash
git add -A && git commit -m "chore: upgrade to Jellyfin 12.1.0"
./release.sh
git push origin main --tags   # the release workflow publishes the ZIP
```

#### Manuell

```bash
# 1. Check your Jellyfin server version (Dashboard → General)

# 2. Update the SDK references in the .csproj
sed -i 's/Version="12.1.0"/Version="12.2.0"/g' \
    Jellyfin.Plugin.Chaosflix/Jellyfin.Plugin.Chaosflix.csproj

# 3. Update targetAbi in meta.json — the single source of truth; release.sh
#    copies it into the manifest entry it writes. Published manifest entries
#    keep the targetAbi their ZIP was released with and are never rewritten.
sed -i 's/"targetAbi": "12.1.0.0"/"targetAbi": "12.2.0.0"/' \
    Jellyfin.Plugin.Chaosflix/meta.json

# 4. Release
./release.sh
```

#### Was bei Major-Updates brechen kann

| Änderung | Symptom | Fix |
|----------|---------|-----|
| Target Framework (z.B. net10→net11) | `TargetFramework 'netX.0' is not supported` | `tfm_for()` in `upgrade-jellyfin.sh` ergänzen; `.csproj`, Dockerfile und `dotnet-version` in den Workflows ziehen mit |
| Namespace-Umbenennung | `The type or namespace 'X' does not exist` | `using`-Statements anpassen |
| API-Signatur-Änderung | `does not contain a definition for 'X'` | Jellyfin Release Notes lesen, Code anpassen |
| DI-Registration | Plugin wird nicht geladen | `ChaosflixServiceRegistrator.cs` prüfen |

> **Tip:** Check available SDK versions: https://www.nuget.org/packages/Jellyfin.Controller
> Check release notes: https://github.com/jellyfin/jellyfin/releases

### Creating a Release (automated)

```bash
# The version is worked out from the commits since the last tag:
./release.sh

# Look first — prints the version and the notes, writes nothing:
./release.sh --dry-run

# A version given by hand always wins:
./release.sh 0.0.2

# The changelog can still be written by hand; it then overrides the generated one:
./release.sh 0.0.2 "Rebuild for Jellyfin 12.1"
```

This updates all version strings, writes the changelog and the release notes,
commits and tags. It builds nothing: the release workflow builds the plugin from
the tag, publishes the release with the ZIP attached and writes the checksum of
exactly those bytes into `manifest.json`. A ZIP built locally would never be
byte-identical to the published one, so its checksum described bytes nobody
could download (#49).

Releasing therefore needs no .NET SDK and no `zip` on the machine that tags:

```bash
git fetch origin && git reset --hard origin/main   # release what the remote has
./release.sh
git push origin main --tags                        # the tag publishes the release
```

`release.sh` refuses to run when the branch is behind its upstream and names the
missing commits, because a release cut from a stale checkout publishes code the
remote does not have (#64).

Without a version argument the next number is derived from the Conventional
Commit subjects since the previous tag, and the reasoning is printed before
anything is written:

| Since the last tag | Bump |
|--------------------|------|
| `feat!:` or a `BREAKING CHANGE:` trailer | MINOR while below 1.0.0 (a 0.x MAJOR bump would declare 1.0), MAJOR from 1.0.0 on |
| a `feat:` | MINOR |
| a raised `targetAbi` in `meta.json` | MINOR — it changes which servers may install the plugin |
| a `fix:` | PATCH |
| only `chore`/`docs`/`test`/`build`/`ci`/`refactor` | PATCH, and the output says so |
| nothing | the script aborts and changes nothing |

Without a changelog argument the release notes are generated from the
Conventional Commit subjects since the previous tag, grouped into breaking
changes, features, bug fixes and other changes. They go into three places: a new
section on top of `CHANGELOG.md`, a short list in `manifest.json` (that is what
Jellyfin's plugin catalogue shows), and `release-notes-v<version>.md`, which the
script prints. The script itself never pushes; pushing the tag is what publishes
the release.

## Testing

One entry point runs everything that gates a push:

```bash
./run-tests.sh
```

That is the shell tests, a Release build, the unit tests (`*.Tests.csproj`) and
the Playwright suite in `e2e/`, which starts a real Jellyfin in Docker against a
fake `media.ccc.de`. The branch guard runs it on every `git push`.

Five suites stay out of it because they need an emulator, a network clone, a
published release or the live media.ccc.de, and are opt-in:

| Suite | How | What it covers |
|---|---|---|
| CCC API contract | `CCC_CONTRACT=1 ./run-tests.sh` | the **real** api.media.ccc.de still has the fields the plugin reads ([`Contract/`](Jellyfin.Plugin.Chaosflix.Tests/Contract)) |
| Android phone app | `ANDROID_E2E=1 ./run-tests.sh` | real ExoPlayer on an emulator ([`e2e/android`](e2e/android)) |
| Cast receiver | `RECEIVER_E2E=1 ./run-tests.sh` | the real jellyfin-chromecast bundle in a browser |
| Android TV | `e2e/android/tv-repro.sh` | jellyfin-androidtv on a TV emulator, driven by hand |
| Published plugin | `e2e/install/install-from-manifest.sh` | a fresh Jellyfin installing the **released** plugin from `manifest.json`, checksum included |

The e2e stack is configurable: `JELLYFIN_TAG` (defaults to `12.1`, the version
`targetAbi` promises), `JELLYFIN_PORT`,
`COMPOSE_PROJECT_NAME` and `E2E_FIXTURE_SECONDS`. Unless you set the middle two
yourself, `run-tests.sh` picks a compose project and a host port from the path of
the checkout it runs in, so several worktrees can run the suite side by side.

## Continuous Integration

| Workflow | When | What |
|---|---|---|
| `ci.yml` | every PR and push to `main` | `./run-tests.sh` on a GitHub-hosted runner |
| `release.yml` | a `v*` tag | build, publish the release with its ZIP, commit the checksum |
| `android-e2e.yml` | nightly, or on demand | the Android emulator suite |
| `install-e2e.yml` | after a release, and nightly | install the published plugin into the newest Jellyfin |
| `contract.yml` | nightly, or on demand | the contract tests against the real media.ccc.de API |

The runner deliberately has no `ffmpeg`, so the fixture build exercises its
container fallback — that path was broken for as long as nothing ran it.

## Known Limitations

- Jellyfin stores a channel item under exactly one parent, so a talk listed in
  several folders needs one id per folder and becomes several library items.
  Watch state is mirrored between them, but the copies show up as duplicates in
  "Recently added" ([#54](https://github.com/praetorianer777/chaosflix-jellyfin/issues/54)).
- Talks cannot be downloaded for offline playback. Jellyfin offers a download
  only for items backed by a file on the server — `Video.CanDownload()` returns
  `IsFileProtocol`, and a channel item's path is whatever its media source says,
  which here is an http url. `/Items/{id}/Download` then serves that path off
  disk, so there is nothing for it to send
  ([#90](https://github.com/praetorianer777/chaosflix-jellyfin/issues/90)).
- Topics can be filtered but not browsed. A talk's tags are mapped to genres
  and filtering by one works (`/Items?genres=Security`), but Jellyfin's genre
  browse lists nothing: it builds genre entities during a library scan, which
  channel items never go through. Asking Jellyfin to create them turns out not
  to be enough either — with them present, `/Genres` listed three of five while
  all five resolved correctly by id and by name, stable across a restart, so the
  listing is unreliable independently of this plugin
  ([#88](https://github.com/praetorianer777/chaosflix-jellyfin/issues/88)).
- Playback runs through `/api/ChaosflixStream/proxy` with a signed url rather
  than straight from the CDN: ExoPlayer cannot follow the CDN's cross-domain
  redirects, so mirror failover, range requests and redirects are resolved
  server-side. The signature is per server process.

## How It Works

```
Jellyfin UI  →  Chaosflix Channel  →  media.ccc.de API  →  CDN streaming
                     │
                     ├── 🔴 Live now (only during a congress)
                     ├── 🔥 Popular (top by views)
                     ├── ⭐ Recommended (views × recency)
                     ├── 📅 Year → Conference → Talks
                     ├── 🔗 Related talks, as Jellyfin's similar items
                     ├── Speaker/tag metadata
                     └── Multiple quality/language sources per talk
```

The plugin implements Jellyfin's `IChannel` interface, plus
`ILocalSimilarItemsProvider` for the related talks. All heavy lifting (watch history, playback position, user management, transcoding, SyncPlay) is handled by Jellyfin itself.

## CCC API

This plugin uses the public [media.ccc.de API](https://api.media.ccc.de):

- `/public/conferences` — list all conferences
- `/public/conferences/{id}` — conference detail with events
- `/public/events/{guid}` — event detail with recordings + related talks
- `/public/events/search?q=` — full-text search

No API key required. Content is licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).

## License

MIT
