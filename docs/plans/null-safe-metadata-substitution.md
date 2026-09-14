# Null-safe metadata substitution

Priority: P2. Covers issue 5, including absent tables, absent collections, and null elements.

## Outcome

Enabling substitution with a blank table must be a safe no-op. Missing optional metadata and null entries must not prevent otherwise valid metadata from being returned.

## Affected code

- `Jellyfin.Plugin.MetaTube/Configuration/PluginConfiguration.cs`
- `Jellyfin.Plugin.MetaTube/Helpers/SubstitutionTable.cs`
- `Jellyfin.Plugin.MetaTube/Providers/MovieProvider.cs`
- `Jellyfin.Plugin.MetaTube/ScheduledTasks/OrganizeMetadataTask.cs`

## Implementation subtasks

1. Initialize all three substitution table backing fields to empty parsed tables. Preserve the serialized raw-property names and parsing semantics. Assigning a null or blank raw value must produce an empty table, including configuration deserialization and older configurations that omit fields.
2. Make collection substitution always return a non-null sequence. Normalize a null input to an empty sequence and filter null/blank elements before dictionary lookup, whether the table is empty or populated.
3. Normalize optional actor and genre collections before substitution in `MovieProvider`, including data returned by real-name conversion. Retain final cleanup after enrichment. Keep title substitution's existing replacement semantics; do not add an unrelated case-matching behavior change.
4. Preserve case-insensitive exact collection-key matching, replacement order, deletion through blank targets, and the provider's duplicate cleanup. Use the same safe contract in the organizer so an empty result can be persisted by the genre plan.

## Regression coverage

- New default configuration and deserialized legacy configuration return usable empty tables.
- Missing actors/genres with substitution enabled and an empty table return successful metadata with empty collections.
- A populated table handles null/blank collection entries without `ArgumentNullException`.
- A blank replacement removes matching values; all values can be removed.
- Ordinary title replacements, case-insensitive actor/genre matching, and existing configuration round trips retain their behavior.

## Acceptance

Exercise both provider metadata and organizer callers against the loopback fixture. Build both server targets. No live provider request is necessary to prove this null-handling fix; retain normal metadata smoke tests at release acceptance.

## Implementation evidence (2026-09-14)

Branch: `bugfix/null-safe-metadata-substitution`. The three parsed backing tables initialize empty, collection substitution normalizes missing/blank elements, and movie metadata normalizes optional actor/genre data after real-name enrichment and before substitution. Existing final deduplication remains intact. Organizer genre-empty persistence belongs to its separate plan.

Regression coverage exercises XML legacy defaults, null/blank settings and collection elements, exact case-insensitive keys, deletion, title replacement, and loopback movie metadata. Emby Debug compilation passed with zero warnings/errors. The provider fixture allows Jellyfin's native null People collection when no people are added; returned genres are empty. No live-provider evidence is claimed.

All five regression cases passed.
