# Jellyfin 12.0 release validation

## Build once, promote the tested artifacts

1. Run the `.NET` workflow manually on the default branch. Both Windows and Ubuntu jobs must pass all four builds and ZIP inspection. Run C# regressions and Python catalog tests on the matching `codex/dev/<branch-name>` branch with identical production source before promotion; retain their results with the runtime evidence.
2. Download `packages-ubuntu-latest`. These portable ZIPs are the release candidates. Do not rebuild them after runtime testing. Record the run ID, commit, version and SHA-256 values from `build-evidence.json`.
3. Complete every runtime check below using those exact ZIPs. Retain logs/screenshots and the test report outside the public repository if they contain private data. Use synthetic media and credentials for shareable reports.
4. Run `Promote tested release` from the default branch, supplying the validation run ID, completed runtime confirmation and an evidence URL or report. This manually gated workflow cannot infer runtime success from a build.
5. Promotion verifies provenance and hashes, validates the catalog change, uploads the existing ZIPs, downloads and verifies the Jellyfin asset, then commits the catalog update. It never force-pushes the catalog. A retry with identical artifacts preserves the catalog entry; changed bytes under an existing version are rejected.

The repository must already have a `dist` branch containing its existing `manifest.json`. Fork maintainers should seed that branch with the historical catalog before publishing. Download URLs use the repository running the workflow. Historical entries are retained unchanged, including their original URLs.

## Required runtime matrix

Use isolated data/config/cache directories and disposable writable media copies. Do not run these tests against a production library. Keep scheduled tasks disabled until their fixture folders are ready.

| Environment | Package | Required result |
| --- | --- | --- |
| Windows, Jellyfin 12.0.0 | Jellyfin candidate ZIP | All checks below |
| Linux, Jellyfin 12.0.0 | Same Jellyfin candidate ZIP | All checks below |
| Existing supported Emby 4.9.x | Emby candidate ZIP | Startup, config persistence, metadata/images and task smoke tests |

Record OS, server version, runtime version, MetaTube backend version, plugin version and hashes. Synthetic-backend regression tests do not establish compatibility with a deployed backend.

From the matching `codex/dev/<branch-name>` checkout, `tests/runtime/backend.py` serves a loopback-only synthetic backend, candidate ZIP and staging catalog. Supply a small PNG and MP4 fixture:

```sh
python tests/runtime/backend.py --package Jellyfin.Plugin.MetaTube/bin/Jellyfin.MetaTube@vVERSION.zip --image /absolute/path/image.png --trailer /absolute/path/trailer.mp4
```

Add `http://127.0.0.1:18197/manifest.json` as the isolated server's plugin repository and use `http://127.0.0.1:18197` as MetaTube's backend. The server and backend must run on the same host (inside the same network namespace for containers). Use native absolute paths when creating fixture libraries. Stop the backend when testing is complete.

- Serve a staging catalog containing the candidate's GUID, target ABI, URL and checksum. Install from the server dashboard, restart, and confirm MetaTube loads without assembly/type/DI errors. Confirm movie/actor metadata providers, image providers and both scheduled tasks are available.
- Open the configuration page in Jellyfin's default layout. Save server/token, templates, filters, translations and image settings. Reload the page and restart; verify settings persist. Preserve existing `MetaTube.xml` in an upgrade test. Never include tokens in evidence.
- Identify movies and actors by name and explicit provider ID. Refresh and restart; verify title, original title, overview, date, rating, genres, studio, series/collection metadata, cast/director identities and actor images. Verify no duplicate credits after repeated refreshes.
- Fetch poster, thumbnail, backdrop and actor images; exercise crop selection, badges and external links. Confirm failures from a missing/unauthorized backend are visible without losing saved configuration.
- Scan nested movie folders and alternate versions. Compare expected MetaTube-tagged movies with scheduled-task coverage. Run metadata organization twice; verify persisted changes and stable second-run output.
- Generate trailers in disposable folders. Verify disabled mode, `.ignore`, repeated runs, URL replacement and cleanup; manual media/trailer files must survive. Rescan and play the discovered trailer. Include multiple movies in a shared folder and alternate versions; any unwanted overwrite blocks publication.
- Cancel running tasks and confirm prompt termination without subsequent item/file mutations.
- On a 10.11 test server, verify the catalog does not offer the 12.0 package and still offers a historical compatible package. Do not load the 12.0 DLL on 10.11.

## Upgrade and rollback

Follow the [official Jellyfin 12.0 guidance](https://jellyfin.org/posts/jellyfin-release-12.0/): stop and back up the complete data/config directories, preserve MetaTube configuration while removing its old binary, upgrade, wait for migrations, perform the full scan, then install the validated plugin. Downgrading requires restoring the complete pre-upgrade backup.

## Evidence template

```text
Validation workflow run / commit:
Plugin version / ZIP SHA-256 values:
Windows Jellyfin version / runtime / result:
Linux Jellyfin version / runtime / result:
Emby version / runtime / result:
MetaTube backend version:
Staging catalog installation and restart:
Settings, metadata, credits, images, tasks, trailers:
10.11 catalog selection:
Logs/screenshots:
Limitations or failures (any required failure blocks promotion):
```
