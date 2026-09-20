# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.4.0] - 2026-09-20

### Breaking changes

- requires Jellyfin 12.1.0 or newer (raised from 10.11.0)

### Bug fixes

- report a raised minimum server version as breaking (#113) (eb1046f)
- stop Popular and Recommended reshuffling on every refresh (#111) (3b58b87)
- stop inventing a community rating from the view count (#108) (83a0649)
- judge a conflicted rebase by the branch being rebased (#106) (abb59ce)
- keep the unsatisfied-range of a 416 from the mirror (#105) (ea1e951)
- escape acronym and guid in API url paths (#103) (33d3099)
- record the v0.3.0 checksum and make a rerun possible (#101) (ab21fea)

### Other

- require Jellyfin 12.1 and build against net10.0 (#110) (09fba9c)
- drop the unused codec detection helpers (#109) (185cf41)
- give every suite run its own compose project and port (#107) (2cc6b7b)

## [0.3.0] - 2026-09-19

### Features

- show the plugin's state on its configuration page (#93) (450f8df)

### Other

- add a troubleshooting section to the README (d8d98bc)
- attach the subtitles media.ccc.de publishes (#94) (d1fdcc6)
- check the real media.ccc.de against what the plugin reads (#92) (fe92a11)
- run the suite against the newest Jellyfin nightly (#83) (e89af27)
- offer a default version no client has to re-encode (#84) (67ed8bd)

## [0.2.0] - 2026-09-19

### Features

- offer every recording as a selectable version (#74) (28c789c)

### Bug fixes

- detect the login route by url, not by #loginPage (#78) (4ab4d9e)
- read a command's arguments without its redirections (#77) (6e4659b)
- keep the recently added row to one copy per talk (#73) (fde2e1e)
- stop releases from a checkout behind the remote (#66) (602ae94)
- share watch state between the copies of a talk (#65) (5b4ab0e)

### Other

- give the client profiles the transcoding clients really send (#80) (62c1b79)
- describe the release chain, tests and CI as they are (#68) (2c4bb00)

## [0.1.1] - 2026-09-18

### Bug fixes

- hand out the item id as the media source id (#63) (94ae7f0)
- end a proxied stream quietly when the client hangs up (#59) (5c68ef3)

### Other

- run the real cast receiver in a browser (#61) (0b6dc5c)
- cover the call the cast receiver makes without a user id (#60) (3ee8b6b)
- prove a talk resumes across two clients (#57) (856b9b8)
- fetch the stream a Chromecast is handed (#56) (123bf53)
- install the published plugin into the latest Jellyfin (#52) (36923b6)

## [0.1.0] - 2026-09-18

### Features

- infer the next release version from the commits (eac6183)
- generate release notes from the commit history (b126365)

### Bug fixes

- let the release workflow own the artifact and checksum (#50) (6eab445)
- start the e2e stack from a clean state (3bdf29f)
- judge -C into a nested worktree by that worktree (beeb66f)
- annotate the release tag with the generated notes (5291be6)
- sign the proxy urls so the endpoint is not an open relay (e63a5bb)
- apply format changes to talks Jellyfin already knows (7a843fe)
- bound the API cache and its per-key locks (b0ba950)
- keep the sync task pre-warm alive until the next run (6301b25)
- make the stream proxy survive bad input and dead mirrors (cca0999)
- recognise combined short flags when a branch is created (ceca832)
- key the probe cache by the probed recording (a0a6d21)
- make the branch guard see git worktrees (dbc0c0c)
- make the plugin settings page load and save (74c4022)
- repair release.sh so a release cannot corrupt the manifest (96a0693)
- skip worktree checkouts when collecting test projects (3691cfc)
- give every folder its own channel item id (cd2aeba)
- close guard bypass via command wrappers (b811087)

### Other

- run the test suite and publish releases from Actions (#48) (c987711)
- check every client shape for needless re-encoding (a39196e)
- compile against the oldest supported Jellyfin (4f7a1b0)
- wait for list view cards before asserting contents (49129b4)
- add opt-in Android e2e suite driving ExoPlayer (f612be8)
- add Playwright e2e suite against a fake media.ccc.de (724e8f3)
- add unit tests for cache, API client, channel and proxy (1648399)
- add Claude Code issue workflow hooks and test gate (513d83b)
