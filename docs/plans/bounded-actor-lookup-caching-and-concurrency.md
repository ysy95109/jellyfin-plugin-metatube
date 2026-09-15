# Bounded actor lookup caching and concurrency

Priority: performance optimization; no speedup magnitude is assumed.

## Outcome

Reduce repeated actor searches and sequential enrichment latency while retaining actor ordering, first-result selection, Gfriends image preference, cancellation, and backend isolation.

## Affected code

- `Jellyfin.Plugin.MetaTube/Providers/MovieProvider.cs`, especially `SetActorImageUrl` and the actor loop.
- A focused actor lookup cache/service and regression fixtures.

## Implementation subtasks

1. Measure the current path using a synthetic delayed backend: request count, peak concurrent requests, and elapsed time for repeated casts and overlapping movies. Record the baseline before introducing concurrency.
2. Separate lookup from `PersonInfo` mutation. Cache immutable lookup data, then apply the existing first-result and Gfriends rules independently to each person's result. Never cache mutable `PersonInfo` objects or reorder the emitted cast.
3. Add a bounded in-memory cache, initially limited to 256 successful nonempty entries with a five-minute absolute TTL. Key by trimmed actor query and backend/configuration generation; retain query case unless existing behavior proves it equivalent. Do not cache failures, cancellations, or empty results initially.
4. Invalidate the cache on server/token changes. Use a generation or safe fingerprint internally, never log raw credentials. Results from requests started under an earlier generation must not populate the new generation. Document that direct provider refresh and image providers do not use this enrichment cache.
5. Limit actor HTTP lookups to four concurrent requests across provider instances, with cancellation-aware acquisition. Process per-movie work through a bounded worker loop rather than creating an unbounded task per actor. Collect results by original index and add people in the original order.
6. Keep in-flight requests caller-owned in this first implementation: do not share cancellable tasks across metadata calls. Recheck the cache after acquiring a slot to reduce redundant completed lookups. Cross-request in-flight deduplication can remain a measured follow-up; it must not let one caller cancel another's request.
7. Enforce capacity eviction and TTL expiry with thread-safe operations; do not hold cache locks during network I/O. Release concurrency slots on every success/failure/cancellation path. Preserve best-effort handling for ordinary lookup failures and propagate cancellation.

## Regression and measurement coverage

- Repeated successful actor lookup uses the cache within TTL; expired/evicted entries fetch again.
- Server or token change cannot reuse prior backend results, including a late response from an older generation.
- Failure and cancellation do not poison the cache or leak concurrency slots.
- Concurrent movie requests never exceed the configured internal limit of four actor requests.
- Cast order, provider ID selection, Gfriends preference, and behavior for actors without images match the baseline.
- Compare cold-cache and warm-cache call counts and elapsed time under fixed fixture delays. Report actual measurements rather than promising a particular improvement.

## Acceptance

Complete after substitution and cancellation fixes. Run a bounded real-backend smoke test to check throttling behavior and metadata equivalence. Keep the initial limits internal; adding user-facing cache controls or broad provider caching is outside this task.

## Implementation evidence (2026-09-15)

Implemented on `enhance/bounded-actor-lookup-caching-and-concurrency`, based on
`main` with the required substitution and cancellation branches merged in
`e8d7182`. This branch retains Jellyfin 10.11/net9 and Emby/net8; it does not depend
on the Jellyfin 12 migration. The original integration checkout and its existing
validation-report edit were preserved using an isolated worktree.

### Subtask mapping

1. Before changing the actor loop, `ActorMeasurementTests` measured the original
   sequential production `MovieProvider` against a loopback backend with eight
   actors and 100 ms delay per actor search. The same fixture now asserts the
   four-request ceiling, unchanged cast ordering, and zero warm-cache requests.
2. `ActorLookupData` snapshots immutable strings for the first result's provider
   ID and the last matching Gfriends image. Each call creates independent
   `PersonInfo` instances. `ActorEnrichmentTests` covers deliberately out-of-order
   replies, mutation isolation, missing actors, failed lookups, first-result IDs,
   and a first result with no image but a usable Gfriends image.
3. `ActorLookupCache.Shared` retains at most 256 successful nonempty selections
   for five minutes from insertion. Queries are trimmed, with ordinal case
   preserved. Expired entries are removed; capacity eviction removes the earliest
   expiry. Successful actors without images may be cached; empty responses,
   exceptions, and cancellations are not.
4. `PluginConfiguration` assigns a unique in-memory generation on construction
   and whenever Server or Token changes. The cache clears on the next access
   under a different generation, and discards late responses from older ones.
   Even changing away and back invalidates entries. Credentials are neither
   included in cache keys nor logged. Direct actor refresh, direct searches,
   image providers, and movie/real-name searches bypass this enrichment cache.
5. `MovieProvider` uses at most four workers per movie and applies the completed
   cast in original index order. All provider instances share one cache and
   semaphore, limiting enrichment actor HTTP requests to four across movies.
6. Each HTTP request belongs to its caller. A queued lookup rechecks the cache
   after slot acquisition, but already-running requests are not deduplicated.
   Cancelling one caller does not cancel another caller's request.
7. Cache operations are locked without holding a lock across network I/O.
   Acquisition is cancellation-aware and slots are released in `finally`.
   Ordinary failures retain best-effort enrichment; cancellation propagates.

### Measurements

Single observations on this Windows host; elapsed time includes scheduling,
JIT, HTTP connection setup, and fixture overhead. These are not speedup guarantees.

| Fixed-delay fixture | Before: requests / peak / ms | After: requests / peak / ms |
| --- | --- | --- |
| One movie, cold | 8 / 1 / 2050 | 8 / 4 / 214 |
| Same movie, warm | 8 / 1 / 860 | 0 / 0 / 1 |
| Two overlapping movies, cold | 16 / 2 / 882 | 16 / 4 / 430 |

An earlier enhanced run made 12 requests for overlapping casts; later runs made
16. That variation is expected: only completed results are reused. No guarantee
of in-flight deduplication is made.

The user supplied a real backend for a bounded read-only smoke test. The opt-in
`ActorRealBackendTests` queried two actors with a four-request budget and a
45-second deadline. Sequential baseline: two requests, 1121 ms. Concurrent cold
cache: two more requests, peak two, 203 ms. Warm cache: zero additional requests,
1 ms. All HTTP responses succeeded; no throttling response was observed at this
small load. Selected provider IDs and image sources matched the uncached results.
The first measurement includes connection setup and backend cache effects, so
it is not a controlled production latency comparison.

### Validation and reproduction

- 27 C# tests passed, including the explicitly enabled real-backend smoke test.
- Cache tests exercise absolute TTL, 256-entry capacity, case-sensitive keys,
  server/token changes, late replies, failed/empty/cancelled results, queued
  cancellation, caller isolation, and cache rechecking after slot acquisition.
- Build configurations and final whitespace validation are recorded below.

From the enhancement checkout:

```powershell
dotnet test tests/MetaTube.Tests --logger 'console;verbosity=detailed'
# Optional: this makes four read-only searches against an explicitly chosen backend.
$env:METATUBE_SMOKE_SERVER = '<backend URL>'
dotnet test tests/MetaTube.Tests --filter FullyQualifiedName~ActorRealBackendTests --logger 'console;verbosity=detailed'
Remove-Item Env:METATUBE_SMOKE_SERVER
foreach ($configuration in @('Debug','Release','Debug.Emby','Release.Emby')) {
    dotnet restore Jellyfin.Plugin.MetaTube -p:Configuration=$configuration --force
    dotnet build Jellyfin.Plugin.MetaTube -c $configuration --no-restore
}
git diff --check
```

The live smoke test is skipped by default and does not embed the user's endpoint
or credentials. It covers backend compatibility at a small load; it does not
establish a backend rate limit. Installed-server Jellyfin/Emby refreshes, Linux
runtime behavior, and sustained production-load performance remain separate
acceptance checks. No production server was reconfigured or plugin installed.

Final build result: Debug, Release, Debug.Emby, and Release.Emby all passed with
zero warnings and zero errors. `git diff --check` passed. No push was performed.

## Runtime settings revision (2026-09-15)

The user requested runtime controls, superseding the original instruction to keep
limits internal. Jellyfin's settings form and Emby's generated options now expose:

| Setting | Default | Range |
| --- | --- | --- |
| Actor cache capacity | 256 entries | 0–10000; 0 disables caching |
| Actor cache lifetime | 5 minutes | 1–1440 minutes |
| Concurrent actor lookups | 4 | 1–32 across movies |

Saving requires no restart. Capacity/lifetime changes clear cached entries on the
next use. Existing lookups may finish, but cannot populate a cache with changed
settings; backend changes still discard stale results entirely. A lower concurrency
limit waits for active requests to drain, without cancelling them or replacing the
shared limiter. Queued lookups observe increases within the limiter's internal
250 ms configuration refresh interval. New movies use the newly configured worker
count; already-running movies retain their bounded worker count. No unlimited
per-actor task fan-out is introduced. Invalid persisted values are clamped to the
same bounds shown by both settings forms; older configuration files retain defaults.

Added `ActorRuntimeSettingsTests` for runtime resizing, TTL, disabling caching,
concurrency changes with active requests, and XML persistence/defaults/bounds.
`tests/actor-settings-form.cjs` executes the embedded form's numeric save/reload
path. The public controls replace the original fixed-limit acceptance assumption.

Runtime-settings validation: 30 C# tests passed; the opt-in real-backend test was
skipped for this settings-only run. The form fixture passed. All four build
configurations passed with zero warnings/errors. The added in-flight test confirms
that tuning cache settings preserves the current result without caching it under
obsolete settings. Whitespace checks passed.
