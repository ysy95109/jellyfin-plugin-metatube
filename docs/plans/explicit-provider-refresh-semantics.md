# Explicit provider refresh semantics

Priority: P2. Covers issue 9.

## Outcome

A direct movie or actor metadata lookup carrying the provider ID update flag must request fresh backend metadata, just as explicit search already does.

## Affected code

- `Jellyfin.Plugin.MetaTube/Providers/MovieProvider.cs`
- `Jellyfin.Plugin.MetaTube/Providers/ActorProvider.cs`
- Existing explicit-search paths and API overloads, as behavioral references.

## Implementation steps

1. In both `GetMetadata` methods, call the overload accepting `lazy`, passing `pid.Update != true`. Keep absent/false flags on the existing lazy path.
2. Preserve the existing one-shot flag behavior: after successful retrieval, save the canonical provider ID without the update flag, retaining the movie crop position. If retrieval fails or is cancelled, do not return a successful replacement ID that consumes the request.
3. Keep name-search and explicit-search flows consistent. Avoid adding a second forced fetch when exact search has already fulfilled the update request and returned a canonical ID.
4. Limit this task to the explicit provider-ID flag. Do not infer that every server metadata refresh should force backend refresh, and do not change unrelated image lookup behavior.

## Regression coverage

- Direct movie and actor metadata lookups: true flag sends `lazy=false`; absent/false sends `lazy=true`.
- Explicit search retains the same matrix.
- Successful result clears the one-shot update flag and preserves movie crop position.
- Failure/cancellation does not report success or consume the caller's original lookup state.
- Name search and no-result behavior remain unchanged.

## Acceptance

Use a strict loopback handler to assert request query values and request counts. In isolated server testing, edit a provider ID to include the update flag and refresh metadata, verifying the deployed backend receives the non-lazy request. Distinguish the observed request from whether that provider's remote source actually changed.

Dependencies: coordinate with cancellation and provider-ID serialization changes before merging.

## Implementation evidence (2026-09-14)

Branch: `bugfix/explicit-provider-refresh-semantics`. Both direct GetMetadata paths pass `pid.Update != true` to the existing lazy overload. Successful canonical IDs continue to clear the update flag and preserve movie position; input lookup IDs are unchanged. Existing search behavior is retained.

All six loopback regression cases passed: absent/false/true matrix for direct and explicit movie/actor lookup, request counts, canonical state, failure/pre-cancellation preservation, and exact-search-to-metadata behavior with only one forced fetch. Deployed backend receipt after a server provider-ID edit remains an isolated runtime acceptance check.
