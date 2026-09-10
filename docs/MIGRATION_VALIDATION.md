# Jellyfin 12.0 migration validation — 2026-09-10

The migration is implemented and the Windows smoke test passed. Publication remains blocked on the remaining release acceptance checks below. No public release or catalog update was performed.

## Build and regression evidence

- .NET SDK: 10.0.400 on Windows.
- Jellyfin references: Controller/Model 12.0.0, target `net10.0`.
- Emby reference: MediaBrowser.Server.Core 4.9.1.80, target `net8.0`.
- Debug, Release, Debug.Emby and Release.Emby all built with zero warnings/errors.
- 14 C# regression cases passed against a synthetic loopback HTTP backend and disposable folders.
- 8 Python catalog/package tests passed, including historical ABI selection, retry idempotency, dependency mismatch rejection and verification of both published ZIP hashes.
- ZIP inspection confirmed one root `MetaTube.dll` per archive, matching the corresponding built assembly and release version.
- Workflow YAML, embedded Python and the package-inspection PowerShell script parsed successfully. `git diff --check` passed. GitHub-hosted workflows have not yet run.

The regression suite exposed an existing empty-search bug. Both movie and actor providers now return no metadata when search finds no match, instead of requesting backend metadata with an empty ID. Plugin identity, configuration schema, backend API and Emby build targets remain unchanged.

## Runtime-tested local candidate

Version: `2026.910.1200.0` (local candidate, not a published version).

| ZIP | SHA-256 |
| --- | --- |
| Jellyfin.MetaTube@v2026.910.1200.0.zip | `6f223d407d9fe884dc31a94d1710a4ab9617348377ebc5328c3e0a78f4095f33` |
| Emby.MetaTube@v2026.910.1200.0.zip | `e965e7ffd3b7fc288af4867273f6425bc21ffee4e70de2b274cb5a37d4386965` |

Windows server: official Jellyfin 12.0 portable package, bundled .NET/ASP.NET 10.0.11 and FFmpeg 8.1.2. Test data/config/cache were isolated beneath ignored `artifacts/runtime`; server/backend listened only on loopback. The backend was the repository's synthetic fixture, not a deployed MetaTube service.

Verified with the Jellyfin candidate ZIP:

- Installed through a staging repository catalog and loaded after a server restart; plugin status was Active.
- Both scheduled tasks registered. Movie and actor searches worked through Jellyfin's HTTP API.
- A scanned synthetic movie retained name, original title, overview, rating, provider ID, studio, genres, actor/director credits and images. Normal library item queries worked after normalizing the Windows path used by the test setup.
- Metadata organization completed, persisted the subtitle genre/badge, and trailer generation completed in the disposable media folder.
- After rescan, Jellyfin discovered the generated `.strm` as a Trailer and resolved its HTTP media source. The trailer was downloaded through Jellyfin's video stream endpoint and successfully decoded by FFmpeg.
- In Jellyfin 12's default web layout, the MetaTube settings form opened, saved a changed name template, and displayed that value after navigation/reload and server restart. Settings and movie metadata were independently confirmed through the API after restart.

Local evidence: `artifacts/runtime/settings.png`, `persistence-evidence.json`, `movie-detail.json`, `trailer-results.json`, and server/backend logs; C# results are under `tests/TestResults`. These generated artifacts are ignored. Do not publish the runtime directory: it also contains disposable test authentication data.

## Remaining acceptance gates

- Linux Jellyfin 12.0 runtime validation. Docker is unavailable and WSL is not installed on this host. The new CI matrix covers Windows/Linux builds and regressions, but does not substitute for runtime testing.
- Emby 4.9.x runtime smoke test; only its two build configurations and package structure were verified here.
- Real MetaTube backend validation, an existing 10.11 configuration/library upgrade, and live 10.11 catalog selection. The ABI-selection regression test is not a live old-server test.
- Full alternate-version/shared-folder trailer safety, repeated refresh/credit behavior and in-flight task cancellation acceptance checks from `RELEASING.md`. The automated tests cover single-movie folder cleanup, stable repeated runs and pre-cancelled tasks.
- A successful manually dispatched CI validation run and completed runtime evidence for those exact CI-produced ZIPs before promotion. Local ZIPs are not automatically eligible for the promotion workflow.

Use `RELEASING.md` for the full checklist and upgrade/rollback instructions. The promotion workflow requires an explicit completed-runtime confirmation and evidence, verifies artifact provenance/hashes, uploads the tested packages first, then updates the catalog without removing historical versions or force-pushing.
