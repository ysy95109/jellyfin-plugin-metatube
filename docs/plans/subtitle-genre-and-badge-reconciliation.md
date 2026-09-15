# Subtitle detection, genre persistence, and badge reconciliation

## Subtitle genre ownership correction (2026-09-15)

The organizer now records the subtitle genre it introduces in
`<DataPath>/metatube/subtitle-genres-v1.json`. Removing subtitles removes that
owned value even after substitution, a rule change, or a service restart.
Unrelated genres are substituted separately; a target already supplied by an
unrelated genre is not claimed or deleted. Pending before/after metadata values
allow failed or interrupted saves to be retried before ownership is committed.
Unreadable subtitle folders still leave genre and badge state untouched.

Existing renamed genres without ownership history are deliberately preserved:
their names alone cannot distinguish old subtitle output from manually assigned
or backend genres. They require manual cleanup if already stale. The original
`中文字幕` marker continues to follow detected subtitle state.

Regression coverage includes subtitle removal across restart, shared target
collisions, changed/disabled/deleting rules, failed metadata persistence, and
recovery after a metadata save interrupted before the ownership commit.

Priority: P2. Covers issues 3, 4, 11 and regex/directory-listing reuse.

## Outcome

The organizer must detect supported Chinese subtitle names, persist an empty genre list when appropriate, and reconcile plugin-managed badges independently of genre transitions. Repeated runs should converge without redundant network requests or metadata writes.

## Affected code

- `Jellyfin.Plugin.MetaTube/ScheduledTasks/OrganizeMetadataTask.cs`
- `Jellyfin.Plugin.MetaTube/Providers/MovieImageProvider.cs`
- A focused subtitle detector and persisted badge-ownership helper, introduced as needed.
- `tests/MetaTube.Tests/TaskTests.cs` and synthetic backend fixtures.

## Implementation subtasks

1. Extract subtitle filename parsing from the scheduled task. Match the complete movie basename followed by dot-separated suffix tokens and a supported subtitle extension. Detect Chinese language tokens even when `default`, `forced`, or descriptive tokens appear before or after them. Support the existing Chinese aliases and Chinese region/script forms (`zh`, `zho`, `chi`, `chs`, `cht`, `zh-CN`, `zh-TW`, `zh-Hans`, `zh-Hant`). Match case-insensitively and do not match a neighboring movie merely because its name shares a prefix.
2. Preserve existing embedded-subtitle filename tags. Reuse immutable compiled regexes or straightforward token parsing. Enumerate each containing directory once per task run, using OS-appropriate path comparison; cache the resulting filename snapshot only for that run. Treat an unreadable folder as unknown subtitle state, log it, and skip destructive genre/badge decisions for that item.
3. Compute desired genres from a normalized copy of the existing collection, subtitle state, and substitution rules. Preserve the current duplicate-removal and ordering behavior. Remove the `!orderedGenres.Any()` early exit; skip persistence only when old and desired collections are equivalent, treating null and empty consistently.
4. Compute desired badge state separately from whether the subtitle genre changed. Use actual detected subtitle state, not the post-substitution genre label. Enabling badges, changing the badge URL, retrying an earlier metadata failure, and disabling badges must all be evaluated on the next run.
5. Track successfully applied plugin badge state durably, keyed by library item identity. Store the applied image identity/URL, badge settings, crop position, and the previous unbadged image reference needed to reverse the plugin's change. Use a versioned, atomically persisted state file in the plugin's writable data location; do not use a transient static dictionary as the only record.
6. Restrict reconciliation to plugin-managed posters. Preserve manually selected/custom posters and detect when the current poster no longer matches the tracked image. For a known MetaTube image, preserve its selected source image and crop rather than replacing it with the backend's default cover. For an untracked or legacy image whose ownership cannot be established, preserve it and explain the skipped reconciliation in logs. Confirm how each supported server represents cached remote images before implementing image identity comparison.
7. Apply image changes through the supported server image/persistence path. Persist an image-only change even when genres are already correct. Commit the badge ownership state only after the corresponding image/item change succeeds. On failure, retain a retryable desired/applied mismatch; genre persistence must not suppress the next badge attempt.
8. Coordinate `MovieImageProvider` with the same desired badge policy so normal image refresh does not immediately undo the organizer's output. Disabling badges should restore only the plugin-managed image it changed, without replacing a later manual selection.

## Regression coverage

- Removing the sole subtitle genre, and substitution removing all genres, both persist an empty list.
- `TEST.zh.srt`, `TEST.zh.default.srt`, `TEST.default.zh.forced.ass`, mixed case, region/script aliases, and basename-prefix collisions.
- Unreadable directory causes no false subtitle removal; directory enumeration occurs once per folder per run.
- Existing subtitle genre followed by enabling badges; badge URL/crop change; disabling badges; unchanged second run.
- Metadata or image persistence fails once and succeeds on the next run without requiring another genre transition.
- Image-only changes persist; manual poster selection survives; selected MetaTube preview/crop remains selected.
- Renamed/substituted subtitle genre does not cause repeated badge work.

## Acceptance and runtime checks

Verify actual poster appearance and persistence after restart on Jellyfin and Emby, including a normal image refresh after organization. An assigned URL in a mock is not proof of downloaded/cached image behavior. Measure directory enumeration and backend call counts before/after; no benchmark-only framework is required.

Dependencies: null-safe substitution, URL/crop handling, and cancellation propagation. Reference naming contract: https://jellyfin.org/docs/general/server/media/movies/.

## Implementation evidence (2026-09-14)

Branch: `bugfix/subtitle-genre-and-badge-reconciliation`; URL, substitution, and cancellation branches are merged prerequisites. `SubtitleDetector` handles suffix tokens/aliases and caches each directory snapshot (including unreadable state) once per organizer run. Empty desired genres persist; unknown directories skip destructive decisions. Badges are evaluated independently from genre updates and use detected subtitles, including normal MovieImageProvider refreshes.

`BadgeOwnership` stores versioned applied/pending state at `<DataPath>/metatube/badges-v1.json`, keyed by library item ID. It preserves source URL and crop, fingerprints cached images, and uses the supported IProviderManager.SaveImage stream overload plus library persistence. Pending intent is atomic before image mutation; applied state is committed only after item persistence. A failed metadata commit is recovered on a later run without redownloading. Manual edits and unknown posters are preserved. The image is rechecked after download before replacement.

`MovieImageProvider` records successful downloaded bytes in `<DataPath>/metatube/image-sources-v1.json`. This gives newly cached remote posters durable provenance rather than guessing ownership from server cache filenames. Equal bytes observed at multiple URLs are deliberately ambiguous and skipped. Legacy cached images without provenance, and servers that transform the stored bytes, remain unowned and are logged/skipped. Explicit remote MetaTube selections preserve their preview URL. No backend default-cover lookup is used for reconciliation.

51 combined C# cases passed (24 subtitle/badge cases plus 27 prerequisite cases). Tests verify aliases, prefix collisions, one directory read per shared folder, unknown folders, empty genres, enable/change/crop/disable, restart of the ownership service, image-only persistence, failed-download and failed-metadata retry, selected preview retention, manual edits, and cached-image provenance ambiguity. Emby Debug build passed with zero warnings/errors. The fixture's SaveImage adapter writes downloaded bytes to a cache file; it does not prove server image transformations or actual poster appearance. Jellyfin/Emby appearance, restart, normal refresh, and Linux runtime acceptance remain separate gates.

Measured fixture counts: two movie paths in one directory use one listing; an unchanged badge run performs zero additional downloads/persistence; retry after a successful image save and failed item commit performs zero additional downloads. No real-backend speedup is claimed.

### Runtime follow-up (2026-09-15)

Follow-up fix `d39e186` restricts the provenance catalog to primary images, so identical thumbnail/backdrop bytes cannot invalidate a poster's source. Its regression passes in the 80-case Jellyfin 12 integration suite; final Debug, Release, Debug.Emby, and Release.Emby builds pass.

On isolated Windows Jellyfin 12, automatic image refresh created the durable source record, organization downloaded and persisted the badge-request image with crop 0.5, a repeated run made zero backend requests, and disabling badges restored the unbadged source with no pending state. Substitution persisted empty genre lists for both fixture movies. The backend supplies a fixed synthetic image, so these checks prove request/cached-file/persistence behavior, not visual badge rendering.

Jellyfin's manual remote-image download endpoint bypasses the plugin image-response method and does not establish provenance. Such an untracked cached selection is intentionally preserved; a subsequent automatic provider image refresh establishes provenance when the bytes/source are unambiguous. This limitation also applies to legacy caches. Real badge appearance, badge persistence across a server restart after application, Linux, and Emby runtime acceptance remain pending. See `docs/BUGFIX_VALIDATION.md` on the integration branch for exact candidate scope and evidence locations.
