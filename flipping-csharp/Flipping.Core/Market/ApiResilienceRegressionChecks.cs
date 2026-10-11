using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

/// <summary>Deterministic HTTP/cache checks using local handlers; never contacts the game or Wiki.</summary>
public static class ApiResilienceRegressionChecks
{
    public static async Task<JsonObject> RunAsync()
    {
        var checks = new JsonArray();
        void Check(bool ok, string name) { if (!ok) throw new InvalidDataException("API resilience regression: " + name); checks.Add(name); }
        static HttpResponseMessage Ok(JsonNode value) => new(HttpStatusCode.OK) { Content = new StringContent(value.ToJsonString()) };
        static JsonObject Bucket(long timestamp) => J.O(("timestamp", timestamp), ("data", new JsonObject()));
        static async Task<T?> Failure<T>(Func<Task> action) where T : Exception
        { try { await action(); return null; } catch (T ex) { return ex; } }
        var now = DateTimeOffset.FromUnixTimeSeconds(7200);
        var config = new JsonObject();
        var waits = new List<TimeSpan>(); var calls = 0;
        using (var http = new HttpClient(new ScriptHandler((_, _) =>
        {
            calls++;
            var reply = calls == 1 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : calls == 2 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Ok(Bucket(6900));
            if (calls == 1) reply.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return Task.FromResult(reply);
        })))
        {
            var client = new WikiMarketTransport(config, http, () => now, (wait, token) => { token.ThrowIfCancellationRequested(); waits.Add(wait); return Task.CompletedTask; });
            var result = await client.FetchAsync("5m?timestamp=6900", default);
            Check(calls == 3 && result.L("timestamp") == 6900, "429 and 503 recover within bounded attempts");
            Check(waits.SequenceEqual(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500) }), "Retry-After delta and exponential backoff are respected");
        }
        calls = 0; waits.Clear();
        using (var http = new HttpClient(new ScriptHandler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); })))
        {
            var client = new WikiMarketTransport(config, http, () => now, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
            var failure = await Failure<HttpRequestException>(async () => { await client.FetchAsync("latest", default); });
            Check(calls == 3 && waits.Count == 2 && failure?.StatusCode == HttpStatusCode.ServiceUnavailable, "persistent 503 stops after three attempts");
        }
        calls = 0;
        using (var http = new HttpClient(new ScriptHandler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)); })))
        {
            var failure = await Failure<HttpRequestException>(async () => { await new WikiMarketTransport(config, http).FetchAsync("latest", default); });
            Check(calls == 1 && failure?.StatusCode == HttpStatusCode.BadRequest, "permanent 400 is never retried");
        }
        calls = 0; waits.Clear();
        using (var http = new HttpClient(new ScriptHandler((_, _) =>
        {
            calls++; var reply = new HttpResponseMessage(HttpStatusCode.TooManyRequests); reply.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120)); return Task.FromResult(reply);
        })))
        {
            var client = new WikiMarketTransport(config, http, () => now, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
            var failure = await Failure<HttpRequestException>(async () => { await client.FetchAsync("latest", default); });
            Check(calls == 1 && waits.Count == 0 && failure?.StatusCode == HttpStatusCode.TooManyRequests, "long Retry-After surfaces failure instead of retrying early");
        }
        calls = 0; waits.Clear();
        using (var http = new HttpClient(new ScriptHandler((_, _) =>
        {
            calls++; var reply = calls == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Ok(Bucket(6900));
            if (calls == 1) reply.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(7)); return Task.FromResult(reply);
        })))
        {
            var client = new WikiMarketTransport(config, http, () => now, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
            await client.FetchAsync("latest", default);
            Check(calls == 2 && waits.Single() == TimeSpan.FromSeconds(7), "Retry-After HTTP date is respected");
        }
        foreach (var timeout in new[] { false, true })
        {
            calls = 0;
            using var http = new HttpClient(new ScriptHandler((_, _) =>
            {
                calls++; if (calls == 1) return Task.FromException<HttpResponseMessage>(timeout ? new TaskCanceledException("fixture timeout") : new HttpRequestException("fixture connection reset"));
                return Task.FromResult(Ok(Bucket(6900)));
            }));
            await new WikiMarketTransport(config, http, () => now, (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }).FetchAsync("latest", default);
            Check(calls == 2, timeout ? "HTTP timeout recovers on a bounded retry" : "connection failure recovers on a bounded retry");
        }
        calls = 0;
        using (var stop = new CancellationTokenSource())
        using (var http = new HttpClient(new ScriptHandler((_, _) => { calls++; stop.Cancel(); return Task.FromCanceled<HttpResponseMessage>(stop.Token); })))
        {
            var failure = await Failure<OperationCanceledException>(async () => { await new WikiMarketTransport(config, http).FetchAsync("latest", stop.Token); });
            Check(calls == 1 && failure is not null, "caller cancellation is never retried");
        }
        calls = 0;
        using (var stop = new CancellationTokenSource())
        using (var http = new HttpClient(new ScriptHandler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); })))
        {
            var client = new WikiMarketTransport(config, http, () => now, (_, token) => { stop.Cancel(); return Task.FromCanceled(token); });
            var failure = await Failure<OperationCanceledException>(async () => { await client.FetchAsync("latest", stop.Token); });
            Check(calls == 1 && failure is not null, "cancellation interrupts backoff without another request");
        }
        calls = 0;
        using (var http = new HttpClient(new ScriptHandler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("malformed JSON") }); })))
        {
            var failure = await Failure<JsonException>(async () => { await new WikiMarketTransport(config, http).FetchAsync("latest", default); });
            Check(calls == 1 && failure is not null, "malformed success payload is not cached or retried");
        }
        calls = 0;
        using (var http = new HttpClient(new ScriptHandler((request, _) => { calls++; return Task.FromResult(Ok(Bucket(long.Parse(request.RequestUri!.Query.Split('=')[1])))); })))
        {
            var client = new WikiMarketTransport(config, http, () => now);
            await client.FetchAsync("5m?timestamp=6900", default);
            await client.FetchAsync("5m?timestamp=6900", default);
            Check(calls == 2, "newly completed bucket is refreshed during publication delay");
            calls = 0; now = now.AddSeconds(120);
            var first = await client.FetchAsync("5m?timestamp=6900", default); first["timestamp"] = 7777;
            var second = await client.FetchAsync("5m?timestamp=6900", default);
            Check(calls == 1 && second.L("timestamp") == 6900, "completed bucket cache returns isolated copies without retimestamping");
            now = now.AddSeconds(300); await client.FetchAsync("5m?timestamp=7200", default); await client.FetchAsync("5m?timestamp=6900", default);
            Check(calls == 2, "new completed bucket uses a distinct key while the previous bucket stays cached");
            now = DateTimeOffset.FromUnixTimeSeconds(7200);
        }
        calls = 0;
        using (var http = new HttpClient(new ScriptHandler((_, _) => { calls++; return Task.FromResult(Ok(new JsonArray(J.O(("id", 1))))); })))
        {
            var shortCache = J.O(("market_mapping_cache_seconds", 10)); var client = new WikiMarketTransport(shortCache, http, () => now);
            var first = (JsonArray)await client.FetchAsync("mapping", default); first.Clear();
            var second = (JsonArray)await client.FetchAsync("mapping", default);
            Check(calls == 1 && second.Count == 1, "mapping cache isolates callers");
            now = now.AddSeconds(11); await client.FetchAsync("mapping", default);
            Check(calls == 2, "mapping refreshes after TTL"); now = DateTimeOffset.FromUnixTimeSeconds(7200);
        }
        calls = 0;
        using (var http = new HttpClient(new ScriptHandler((_, _) => { calls++; return Task.FromResult(Ok(Bucket(6600))); })))
        {
            var client = new WikiMarketTransport(config, http, () => now);
            await client.FetchAsync("5m?timestamp=6900", default); var result = await client.FetchAsync("5m?timestamp=6900", default);
            Check(calls == 2 && result.L("timestamp") == 6600, "mismatched bucket payload is never cached under requested timestamp");
        }
        calls = 0;
        using (var http = new HttpClient(new ScriptHandler((_, _) => { calls++; return Task.FromResult(Ok(Bucket(7000 + calls))); })))
        {
            var client = new WikiMarketTransport(config, http, () => now);
            var first = await client.FetchAsync("latest", default); var second = await client.FetchAsync("latest", default);
            Check(calls == 2 && first.L("timestamp") == 7001 && second.L("timestamp") == 7002, "latest prices always request fresh data and preserve source timestamps");
        }
        calls = 0;
        using (var http = new HttpClient(new ScriptHandler((_, _) => { calls++; return Task.FromResult(Ok(Bucket(7200))); })))
        {
            var client = new WikiMarketTransport(config, http, () => now);
            await client.FetchAsync("5m?timestamp=7200", default); await client.FetchAsync("5m?timestamp=7200", default);
            Check(calls == 2, "unfinished current buckets are not cached");
        }
        calls = 0;
        now = now.AddSeconds(120);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var http = new HttpClient(new ScriptHandler(async (_, token) => { Interlocked.Increment(ref calls); await release.Task.WaitAsync(token); return Ok(Bucket(6900)); })))
        {
            var client = new WikiMarketTransport(config, http, () => now);
            var requests = Enumerable.Range(0, 8).Select(_ => client.FetchAsync("5m?timestamp=6900", default)).ToArray();
            release.SetResult(); var results = await Task.WhenAll(requests);
            Check(calls == 1 && results.All(r => r.L("timestamp") == 6900), "concurrent reads of one completed bucket share one HTTP request");
        }
        calls = 0; release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var stop = new CancellationTokenSource())
        using (var http = new HttpClient(new ScriptHandler(async (_, token) => { Interlocked.Increment(ref calls); await release.Task.WaitAsync(token); return Ok(Bucket(6900)); })))
        {
            var client = new WikiMarketTransport(config, http, () => now);
            var producer = client.FetchAsync("5m?timestamp=6900", default); var waiter = client.FetchAsync("5m?timestamp=6900", stop.Token);
            stop.Cancel(); var failure = await Failure<OperationCanceledException>(async () => { await waiter; }); release.SetResult(); var result = await producer;
            Check(failure is not null && calls == 1 && result.L("timestamp") == 6900, "canceled cache waiter leaves the active producer intact");
        }
        return J.O(("passed", checks.Count), ("checks", checks), ("live_http_requests", false), ("live_game_inputs", false));
    }

    private sealed class ScriptHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request, cancellationToken);
    }
}
