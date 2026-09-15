using System.Diagnostics;
using Jellyfin.Plugin.MetaTube;
using Jellyfin.Plugin.MetaTube.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace MetaTube.Tests;

public sealed class RealBackendFactAttribute : FactAttribute
{
    public RealBackendFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("METATUBE_SMOKE_SERVER")))
            Skip = "Opt in with METATUBE_SMOKE_SERVER; sends at most four read-only actor searches.";
    }
}

public class ActorRealBackendTests(ITestOutputHelper output) : TestEnvironment
{
    [RealBackendFact]
    public async Task Bounded_real_backend_metadata_equivalence()
    {
        Config.Server = Environment.GetEnvironmentVariable("METATUBE_SMOKE_SERVER");
        Config.Token = string.Empty;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var handler = new CountingHandler();
        using var client = new HttpClient(handler);
        ApiClient.TestHttpClient = client;
        try
        {
            var queries = new[] { "上原亜衣", "三上悠亜" };
            async Task<ActorLookupData> Lookup(string query, CancellationToken token) =>
                ActorLookupData.Select(await ApiClient.SearchActorAsync(query, token));
            var watch = Stopwatch.StartNew();
            var expected = new List<ActorLookupData>();
            foreach (var query in queries) expected.Add(await Lookup(query, deadline.Token));
            Assert.All(expected, value => Assert.NotNull(value));
            output.WriteLine($"Sequential baseline: requests={handler.Calls}, elapsed_ms={watch.ElapsedMilliseconds}");
            var cache = new ActorLookupCache(() => Config.ActorLookupGeneration, Lookup);
            watch.Restart();
            var cold = await Task.WhenAll(queries.Select(q => cache.GetAsync(q, deadline.Token)));
            Assert.Equal(expected, cold);
            output.WriteLine($"Concurrent cold: additional_requests={handler.Calls - 2}, elapsed_ms={watch.ElapsedMilliseconds}, peak={handler.Peak}");
            watch.Restart();
            var warm = await Task.WhenAll(queries.Select(q => cache.GetAsync(q, deadline.Token)));
            Assert.Equal(expected, warm);
            Assert.Equal(4, handler.Calls);
            Assert.InRange(handler.Peak, 1, 2);
            output.WriteLine($"Warm: additional_requests=0, elapsed_ms={watch.ElapsedMilliseconds}; selected IDs/images equivalent; all HTTP responses successful.");
        }
        finally { ApiClient.TestHttpClient = null; }
    }

    private sealed class CountingHandler : DelegatingHandler
    {
        internal int Calls;
        internal int Peak;
        private int _active;
        internal CountingHandler() : base(new HttpClientHandler()) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Interlocked.Increment(ref Calls) > 4) throw new InvalidOperationException("Smoke request budget exceeded");
            var active = Interlocked.Increment(ref _active);
            int prior;
            do { prior = Peak; } while (active > prior && Interlocked.CompareExchange(ref Peak, active, prior) != prior);
            try
            {
                var response = await base.SendAsync(request, token);
                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    response.Dispose();
                    throw new IOException($"Real backend returned HTTP {status}");
                }
                return response;
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
