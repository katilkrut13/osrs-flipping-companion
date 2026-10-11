using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

/// <summary>Retries transient read failures without making old market observations appear new.</summary>
internal sealed class WikiMarketTransport
{
    private sealed record Cached(DateTimeOffset StoredAt, DateTimeOffset ExpiresAt, JsonNode Value);
    private readonly ConcurrentDictionary<string, Cached> cache = new(StringComparer.Ordinal);
    // A fixed number of gates bounds memory even as completed bucket URLs change over time.
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly JsonObject config;
    private readonly HttpClient http;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    internal WikiMarketTransport(JsonObject config, HttpClient http, Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.config = config.Copy(); this.http = http;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.delay = delay ?? ((wait, token) => Task.Delay(wait, token));
    }

    internal async Task<JsonNode> FetchAsync(string endpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (cacheSeconds, bucketTimestamp) = CachePolicy(endpoint, utcNow());
        if (cacheSeconds <= 0) return await RequestAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (TryCached(endpoint, out var found)) return found;
        var gate = gates[(int)((uint)StringComparer.Ordinal.GetHashCode(endpoint) % gates.Length)];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryCached(endpoint, out found)) return found;
            var response = await RequestAsync(endpoint, cancellationToken).ConfigureAwait(false);
            // Only an exact completed bucket may be reused. A current/fallback bucket must
            // never be stored under the requested timestamp, even if the API returned 200.
            var valid = bucketTimestamp is null ? response is JsonArray :
                response is JsonObject obj && obj["data"] is JsonObject && obj["timestamp"].IsInteger() && obj.L("timestamp") == bucketTimestamp;
            if (valid)
            {
                var now = utcNow(); cache[endpoint] = new(now, now.AddSeconds(cacheSeconds), response.DeepClone());
                foreach (var key in cache.Where(p => p.Value.ExpiresAt <= now).Select(p => p.Key).ToArray()) cache.TryRemove(key, out _);
                foreach (var key in cache.OrderBy(p => p.Value.StoredAt).Take(Math.Max(0, cache.Count - 128)).Select(p => p.Key).ToArray()) cache.TryRemove(key, out _);
            }
            return response;
        }
        finally { gate.Release(); }
    }

    private bool TryCached(string endpoint, out JsonNode value)
    {
        var now = utcNow();
        if (cache.TryGetValue(endpoint, out var entry) && now >= entry.StoredAt && now < entry.ExpiresAt)
        { value = entry.Value.DeepClone(); return true; }
        value = null!; return false;
    }

    private (int Seconds, long? BucketTimestamp) CachePolicy(string endpoint, DateTimeOffset now)
    {
        if (endpoint == "mapping") return ((int)Math.Clamp(config.L("market_mapping_cache_seconds", 21600), 1, 86400), null);
        foreach (var (name, seconds) in new[] { ("5m", 300), ("1h", 3600), ("24h", 86400) })
        {
            var prefix = name + "?timestamp=";
            if (!endpoint.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (long.TryParse(endpoint.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp) &&
                timestamp >= 0 && timestamp % seconds == 0 && timestamp <= now.ToUnixTimeSeconds() / seconds * seconds - seconds &&
                now.ToUnixTimeSeconds() - (timestamp + seconds) >= 120)
                return ((int)Math.Clamp(config.L("market_bucket_cache_seconds", 172800), 1, 172800), timestamp);
        }
        // In particular, /latest and live timeseries are never served from this cache.
        return (0, null);
    }

    private async Task<JsonNode> RequestAsync(string endpoint, CancellationToken cancellationToken)
    {
        var attempts = (int)Math.Clamp(config.L("market_http_attempts", 3), 1, 5);
        var maxWait = TimeSpan.FromSeconds(Math.Clamp(config.D("market_http_max_retry_wait_seconds", 30), 0, 60));
        for (var attempt = 0; ; attempt++)
        {
            var wait = TimeSpan.FromMilliseconds(Math.Min(2000, 250 * (1 << attempt)));
            try
            {
                using var response = await http.GetAsync("https://prices.runescape.wiki/api/v1/osrs/" + endpoint, cancellationToken).ConfigureAwait(false);
                var retryable = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode is >= 500 and <= 599;
                if (retryable && attempt + 1 < attempts)
                {
                    if (response.Headers.RetryAfter?.Delta is TimeSpan delta) wait = delta;
                    else if (response.Headers.RetryAfter?.Date is DateTimeOffset date) wait = date - utcNow();
                    wait = wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
                    // If the server asks for a wait beyond our bounded retry budget, surface
                    // the failure. Retrying earlier would violate Retry-After.
                    if (wait > maxWait) response.EnsureSuccessStatusCode();
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) ??
                        throw new InvalidDataException("Empty Wiki price response");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt + 1 < attempts)
            { if (wait > maxWait) throw; }
            catch (HttpRequestException ex) when (ex.StatusCode is null && attempt + 1 < attempts)
            { if (wait > maxWait) throw; }
            cancellationToken.ThrowIfCancellationRequested();
            await delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }
}
