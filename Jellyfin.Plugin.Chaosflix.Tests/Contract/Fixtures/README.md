# Recorded media.ccc.de responses

Real responses from `api.media.ccc.de`, captured on 2026-09-19. They are trimmed — long
arrays keep only the first few entries — but every object is byte-for-byte what the API
returned, including the awkward parts (`length: null` on a subtitle recording, `persons: []`,
a `logo_url` containing `..`).

They exist so `CccContractFixtureTests` can run the assertions in `CccContract` offline, on
every push. That is a self-test of the assertions, not a check on the API: only
`CccApiContractTests` (opt-in, `CCC_CONTRACT=1`) can tell you whether the API still agrees.

Refresh them by hand when the API changes; nothing regenerates them automatically.
