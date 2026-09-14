# Trailer ownership and collision-safe generation

Priority: P1. Covers issue 1 and the first-word filename collision.

## Outcome

Generating, replacing, or removing one movie's trailer must never delete or overwrite another movie's trailer or a manually maintained file. Movies sharing a directory must retain distinct generated trailers.

## Current failure and affected code

`Jellyfin.Plugin.MetaTube/ScheduledTasks/GenerateTrailersTask.cs` deletes every matching `*-Trailer.strm` in a shared folder and derives the output filename from the first word of `item.Name`. Two movies can therefore delete or overwrite each other's outputs. The no-URL branch also deletes all matching files.

## Implementation subtasks

1. Introduce a small trailer ownership helper used by the task. Keep a versioned manifest in each managed `trailers` folder containing the library item identity, exact generated basename, and hash of the last successfully written content. Use server item IDs through a Jellyfin/Emby-compatible representation, not display names or provider IDs: multiple library items can have the same provider ID.
2. Generate a stable basename containing the full item identity and the existing `-Trailer.strm` suffix. Do not use a title as a path component. Validate manifest basenames as single filenames and resolve paths within the selected trailers directory before reading, replacing, or deleting them.
3. Write trailer content to a unique temporary file in the same folder, then replace/move it into place. Save the manifest through a temporary-file replacement as well. Record ownership only after the trailer write succeeds. Serialize changes to a shared folder within a task run.
4. Replace wildcard deletion with ownership checks. Delete or overwrite only a manifest-owned file whose current content hash matches the last generated content. If someone edited that file, preserve it and report the conflict. If an unowned file occupies the desired path, preserve it and skip that movie with an actionable log entry.
5. Treat legacy `*-Trailer.strm` files without ownership records as unowned. Do not guess ownership from filenames, URLs, timestamps, or the number of movies in the folder. Document that legacy leftovers require manual cleanup; correctness takes precedence over automatic migration.
6. Compare the actual stored URL/content rather than trusting `DateLastSaved` as sufficient evidence of freshness. A changed URL must update the owned trailer even when timestamps are misleading. A rename must keep the same generated filename.
7. With no trailer URL, remove only that item's unmodified owned file and its manifest entry. Preserve `.ignore`, other movies, and manual content. Remove the directory only when there is no user content or remaining ownership metadata; do not use broad recursive deletion.
8. Preserve disabled mode and `.ignore` behavior. Add cancellation checks before filesystem mutations and propagate cancellation. Finish an already-started commit of ownership consistently; on interruption or uncertain ownership, leave files intact for a later run. A malformed manifest must cause a safe skip, not fallback to wildcard cleanup.

## Regression coverage

- Two movies with different names in one directory both keep their trailers.
- `Alpha One` and `Alpha Two` receive distinct files and correct URLs.
- A movie with no trailer URL cannot remove a neighboring movie's output.
- Manual `manual.strm`, `manual.mp4`, and `manual-Trailer.strm` all survive.
- A manually edited owned file is preserved; a conflicting unowned target is not overwritten.
- Repeated runs do not rewrite unchanged content; renaming a movie does not create a duplicate.
- Changed URL, misleading timestamps, removed URL, legacy files, corrupt manifests, and path-escaping manifest entries behave safely.
- A failed write or cancellation preserves the prior valid trailer and does not authorize deletion of unowned files.

## Acceptance and runtime checks

Run against disposable shared folders on Windows and Linux. Verify Jellyfin and Emby discover and play the generated trailer names, including alternate versions. Record before/after directory contents and ownership records. The task is complete only when unrelated files survive every cleanup scenario and cancellation is observable.

Dependencies: cancellation propagation plan. No release-script changes are expected.

## Implementation evidence (2026-09-14)

Branch: `bugfix/trailer-ownership-and-collision-safe-generation` (Jellyfin 10.11/main baseline).
`TrailerOwnership.ReconcileAsync` implements subtasks 1-8; the task delegates using the server item ID. A process-wide cancellation-aware gate also serializes overlapping task runs. File and manifest replacements are individually atomic; a crash between them leaves a hash mismatch that is preserved for manual resolution. Legacy files are intentionally not adopted and require manual cleanup.

Six Windows disposable-folder regression cases passed, covering shared folders, changed/unchanged content, removal, manual and edited files, malformed/escaping manifests, ignore, and pre-cancellation. Jellyfin compilation passed through the regression project. Linux filesystem checks and Jellyfin/Emby discovery/playback remain runtime acceptance gates; these are not claimed from fixture results.

Emby Debug build passed with zero warnings/errors.
