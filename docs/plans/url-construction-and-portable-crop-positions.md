# URL construction and portable crop positions

Priority: P2. Covers issues 6, 7, and 10.

## Outcome

API URLs must preserve a configured server path prefix, encode provider/ID values as individual URL segments, and serialize numeric values independently of culture. Stored crop positions must remain readable across server locales.

## Affected code

- `Jellyfin.Plugin.MetaTube/ApiClient.cs`
- `Jellyfin.Plugin.MetaTube/Helpers/ProviderId.cs`
- `Jellyfin.Plugin.MetaTube/Extensions/ProviderIdsExtensions.cs`
- Provider/API regression tests.

## Implementation subtasks

1. Replace `Path.Combine` with a URL-specific builder. Validate the configured server as an absolute HTTP(S) URI, preserve its path prefix, and append API routes with exactly one separator. Support prefixes with or without a trailing slash. Fail with a configuration-specific error for unsupported query/fragment-bearing server values rather than silently discarding those parts.
2. Escape provider and decoded ID independently once at the API URL boundary. Keep search/translation parameters in query construction, not string concatenation. Do not change the established escaping layer used to store provider IDs without round-trip tests.
3. Cover slash, backslash, colon, question mark, hash, percent, Unicode, and rooted-looking IDs. Reject standalone dot/dot-dot route segments if URI normalization would escape the intended route. Verify the final wire path against a strict loopback route handler; a syntactically valid `Uri` alone is insufficient.
4. Format image ratio and position using round-trip format and invariant culture; use invariant numeric formatting for other wire values. Serialize `ProviderId.Position` with the same invariant rule.
5. Parse crop positions with invariant culture and `NumberStyles.Float`, excluding thousands separators. For legacy identifiers, accept a strictly defined single-comma decimal form without dots or grouping, translating that comma to a decimal point. Thus `0,5` becomes `0.5`, never `5`. Reject malformed/non-finite positions and fall back to the existing unspecified-position behavior.
6. Write only the canonical invariant form on subsequent saves. Preserve existing provider/ID, optional position, and update-flag field structure. Do not bulk rewrite library identifiers as part of this task.

## Regression coverage

- Root server, `/metatube`, and `/metatube/` prefixes for info, image, search, and translation routes.
- Reserved characters and encoded percent sequences survive storage, decoding, and API routing without double escaping or path-prefix loss.
- `/abc` remains inside the expected provider/ID route; dot-segment inputs cannot normalize outside it.
- `en-US`, `fr-FR`, and `de-DE` produce identical numeric query values and canonical stored identifiers.
- Legacy `0,5` reads as `0.5` under every tested culture; ambiguous grouping, invalid values, NaN, and infinity do not silently become usable positions.
- Optional position/update fields still round-trip correctly.

## Acceptance and compatibility checks

Verify actual HTTP paths with a strict fixture and with a deployed MetaTube backend behind a subpath proxy. Confirm the backend/router accepts escaped separators as intended; if it cannot represent a particular ID, report an explicit unsupported-ID error rather than sending a different route. Restore test culture after each test and keep culture-sensitive tests isolated.

The legacy comma fallback is a compatibility policy, not general locale-dependent number parsing. Document it alongside identifier tests.

## Implementation evidence (2026-09-14)

Branch: `bugfix/url-construction-and-portable-crop-positions`. `ApiClient.ComposeUrl` preserves the prefix and rejects invalid server configuration; `Route` escapes individual segments and rejects dot segments. `ProviderId` writes invariant round-trip positions and reads the documented single-comma compatibility form, excluding nonfinite and grouped input.

All 11 regression cases passed on Windows, including actual HTTP raw targets with reserved/Unicode values and three server-prefix forms, three cultures, invalid configuration, and dot segments. The tests run net10 against the unchanged net9/Jellyfin 10.11 plugin target. A deployed subpath proxy/backend has not been tested; acceptance of escaped separators by that router remains a runtime gate.
