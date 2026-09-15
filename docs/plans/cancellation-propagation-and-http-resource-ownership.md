# Cancellation propagation and HTTP resource ownership

Priority: P2 for cancellation; maintenance for deterministic disposal. Covers issue 8 and the HTTP resource-lifetime optimization.

## Outcome

Caller cancellation must remain observable through optional enrichment, retries, and scheduled tasks. Each request, response, and stream must have an explicit owner without disposing image content before the server consumes it.

## Affected code

- `Jellyfin.Plugin.MetaTube/Providers/MovieProvider.cs`
- `Jellyfin.Plugin.MetaTube/Translation/TranslationHelper.cs`
- `Jellyfin.Plugin.MetaTube/ApiClient.cs`
- All three scheduled tasks, including Emby-only `UpdatePluginTask`.

## Implementation subtasks

1. Audit broad catches in actor image enrichment, real-name conversion, translation, and scheduled tasks. Add an `OperationCanceledException` rethrow before ordinary recoverable-error handling. Exclude cancellation from translation retry filters. Preserve the existing attempt limit for other failures.
2. Pass the caller token through supported waits and I/O. Check it between enrichment stages, before subsequent item/file mutations, and before reporting successful completion. Cover the final-item case; a check at the next loop iteration is insufficient.
3. Preserve recoverable non-cancellation failures as logged enrichment failures. Treat cancellation-shaped transport timeouts consistently and document that they are not retried by the generic translation retry helper. Do not use a blanket catch to turn them into success.
4. In metadata/translation HTTP calls, scope `HttpRequestMessage` and `HttpResponseMessage` through deserialization with deterministic disposal on success, malformed JSON, backend error, and cancellation. Keep the shared `HttpClient` alive.
5. For Jellyfin image calls, dispose the outgoing request after send while transferring ownership of the returned response to the caller. On any failure before transfer, dispose the response locally.
6. For Emby image calls, inspect `HttpResponseInfo`'s stream disposal contract. Transfer the response content through an owning stream wrapper that disposes the underlying `HttpResponseMessage` when Emby disposes the stream. Ensure construction/read failures release both objects and avoid double-disposal hazards. Do not put the response in a scope that ends before image consumption.
7. Scope Emby updater response/download streams and propagate cancellation. Check the token immediately before non-cancellable extraction; do not claim that the ZIP library can roll back or interrupt extraction if its API cannot. Report this cancellation boundary explicitly in runtime evidence.
8. Introduce only the minimal injectable HTTP/handler seam needed to observe disposal and controlled cancellation in tests. Keep production client pooling behavior unchanged.

## Regression coverage

- Cancel while actor lookup, real-name conversion, translation delay, and translation HTTP are pending; metadata must not return success.
- A cancelled translation attempt is attempted once and releases its semaphore so a later request can proceed.
- Cancel during the final scheduled-task item; cancellation propagates and no success progress is emitted afterward.
- A normal actor-image or translation failure retains existing best-effort behavior.
- Tracking handlers/content verify metadata request/response disposal on success and all failure paths.
- Returned image content remains readable until caller disposal; disposal releases the owning response for both server adapters.
- Existing pre-cancelled tests continue to pass.

## Acceptance

Run controlled mid-flight cancellation against the loopback backend, then verify cancellation and image playback in isolated Jellyfin and Emby. Use instrumented disposal checks rather than claiming socket-leak elimination from a short load test. Coordinate trailer write boundaries and badge persistence with their respective plans.

## Implementation evidence (2026-09-14)

Branch: `bugfix/cancellation-propagation-and-http-resource-ownership`. Enrichment/task catches rethrow cancellation, translation excludes all OperationCanceledException forms from its existing five-attempt retry loop, and final progress/mutation boundaries check caller cancellation. Metadata requests/responses are scoped through deserialization. Jellyfin images transfer the response; Emby uses ResponseOwnedStream registered in HttpResponseInfo's IDisposable[] constructor.

Important contract finding: a reflection probe against installed MediaBrowser.Common 4.9.1.80 showed the parameterless HttpResponseInfo does NOT dispose an assigned Content stream. Its IDisposable[] constructor does dispose registered streams (verified with MemoryStream). Registration and stream ownership are both required.

Controlled tests cover pending actor lookup, real-name lookup, translation HTTP and delay, semaphore release, transport cancellation, recoverable actor failure, final-item cancellation, metadata success/error/malformed JSON disposal, and image content lifetime. Emby Debug build passed with zero warnings/errors. Runtime image playback/cancellation on isolated Jellyfin and Emby remains pending; wrapper instrumentation does not establish socket-leak elimination.

The updater scopes API/download streams and checks cancellation immediately before and after synchronous ZIP extraction. Once extraction starts, the ZIP API cannot interrupt it or roll back written files. Trailer filesystem ownership commits are supplied by the separate trailer branch; integrate that branch before accepting trailer behavior.

All 11 regression cases passed.
