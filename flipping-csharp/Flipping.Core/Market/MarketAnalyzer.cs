using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

/// <summary>Native read-only OSRS Wiki prices client. Trade observations never imply guaranteed fills.</summary>
public sealed class MarketAnalyzer
{
    private readonly JsonObject config;
    private readonly HttpClient http;
    private readonly WikiMarketTransport transport;
    private readonly ConcurrentDictionary<int, (long At, JsonArray Points)> historyCache = new();
    public MarketAnalyzer(JsonObject config, HttpClient? client = null)
    {
        this.config = config.Copy();
        http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (!http.DefaultRequestHeaders.UserAgent.Any()) http.DefaultRequestHeaders.UserAgent.ParseAdd("LocalFlippingCompanion/1.0 (personal-read-only-research)");
        transport = new WikiMarketTransport(this.config, http);
    }
    private async Task<JsonNode> Fetch(string endpoint, CancellationToken cancellationToken)
    {
        return await transport.FetchAsync(endpoint, cancellationToken).ConfigureAwait(false);
    }
    public static long Tax(long price, int itemId, JsonObject config)
    {
        if (config.Arr("tax_exempt_ids")?.Any(x => TryInteger(x, out var value) && value == itemId) == true) return 0;
        // Multiplication uses decimal to retain integer floor without overflow for valid long input.
        return (long)Math.Min(decimal.Floor((decimal)Math.Max(0, price) * config.L("tax_percent", 2) / 100), config.L("tax_cap_gp", 5_000_000));
    }
    private static bool TryInteger(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<long>(out value)) return true;
        if (v.TryGetValue<int>(out var i)) { value = i; return true; }
        if (v.TryGetValue<double>(out _) || v.TryGetValue<decimal>(out _) || v.TryGetValue<float>(out _)) return false;
        // JsonElement numbers retain their lexical form: reject fractional, quoted and Boolean fields.
        var raw = node.ToJsonString();
        return raw.Length > 0 && raw.All(c => char.IsAsciiDigit(c) || c == '-') && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }
    private static bool Positive(JsonNode? node, out long value) => TryInteger(node, out value) && value > 0;
    private static bool Fresh(JsonNode? price, JsonNode? timestamp, long now, long maxAge) => Positive(price, out _) && Positive(timestamp, out var time) && time <= now + 60 && now - time <= maxAge;
    public static JsonArray BuildExitQuotes(JsonObject snapshot, JsonObject config, long now)
    {
        var exits = new JsonArray(); var latest = snapshot.Obj("latest")?.Obj("data"); if (latest is null) return exits;
        foreach (var meta in snapshot.Objects("mapping"))
        {
            if (!Positive(meta["id"], out var id) || id > int.MaxValue || string.IsNullOrEmpty(meta.S("name"))) continue;
            var quote = latest[id.ToString()] as JsonObject; if (quote is null || !Fresh(quote["high"], quote["highTime"], now, config.L("max_price_age_seconds", 300))) continue;
            var highTime = quote.L("highTime"); var high = quote.L("high");
            var exit = J.O(("id", id), ("name", meta.S("name")), ("indicative_sell_gp", high), ("highTime", highTime), ("sell_time", highTime), ("source", "osrs_wiki_latest"), ("fresh_status", "FRESH"), ("generated_at_epoch", now), ("price_age_seconds", Math.Max(0, now - highTime)));
            if (Fresh(quote["low"], quote["lowTime"], now, config.L("max_price_age_seconds", 300)) && quote.L("low") <= high) { exit["indicative_buy_gp"] = quote.L("low"); exit["lowTime"] = quote.L("lowTime"); }
            var hour = snapshot.Obj("1h"); var hourly = hour?.Obj("data")?[id.ToString()] as JsonObject;
            if (hourly is not null && Positive(hour?["timestamp"], out var hourTime) && now - hourTime >= 0 && now - hourTime <= 7200)
            {
                foreach (var (source, target) in new[] { ("highPriceVolume", "volume_1h_buy"), ("lowPriceVolume", "volume_1h_sell") }) if (TryInteger(hourly[source], out var volume) && volume >= 0) exit[target] = volume;
                if (exit["volume_1h_buy"] is not null || exit["volume_1h_sell"] is not null) exit["volume_1h_timestamp"] = hourTime;
            }
            exits.Add(exit);
        }
        return exits;
    }
    public async Task<JsonObject> FetchExitQuotesAsync(IEnumerable<JsonObject> items, CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<long>(); var mapping = new JsonArray();
        foreach (var item in items)
            if (Positive(item["id"], out var id) && id <= int.MaxValue && !string.IsNullOrWhiteSpace(item.S("name")) && seen.Add(id)) mapping.Add(J.O(("id", id), ("name", item.S("name").Trim())));
        if (mapping.Count == 0) return J.O(("generated_at_epoch", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("exit_quotes", new JsonArray()));
        var latest = await Fetch("latest", cancellationToken).ConfigureAwait(false); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var data = J.O(("mapping", mapping), ("latest", latest as JsonObject ?? new()));
        return J.O(("generated_at_epoch", now), ("exit_quotes", BuildExitQuotes(data, config, now)));
    }
    public async Task<JsonObject> AnalyzeAsync(CancellationToken cancellationToken = default)
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var names = new[] { "mapping", "latest", "5m", "1h", "24h" };
        var windows = new Dictionary<string, int> { ["5m"] = 300, ["1h"] = 3600, ["24h"] = 86400 };
        var responses = await Task.WhenAll(names.Select(e => Fetch(windows.TryGetValue(e, out var window) ? e + "?timestamp=" + (before / window * window - window) : e, cancellationToken))).ConfigureAwait(false);
        var data = new JsonObject(); for (var i = 0; i < names.Length; i++) data[names[i]] = responses[i];
        var report = Screen(data, config); var now = report.L("generated_at_epoch");
        var ranked = report.Objects("candidates").OrderByDescending(c => Math.Max(0, c.L("estimated_profit_after_test_gp")) * c.D("capital_turnover_per_hour_proxy")).ToList();
        var contexts = new Dictionary<int, JsonObject>(); var errors = new JsonObject(); var checkedCandidates = new Dictionary<int, JsonObject>();
        foreach (var c in ranked)
            if (historyCache.TryGetValue((int)c.L("id"), out var cached) && now - cached.At >= 0 && now - cached.At < config.L("history_cache_seconds", 300))
            { var id = (int)c.L("id"); contexts[id] = Strategy.ChartContext(cached.Points.OfType<JsonObject>(), now, id, config); checkedCandidates[id] = Strategy.Evaluate(c, contexts[id], config); }
        var batch = (int)Math.Max(1, config.L("history_scan_top_n", 12)); var maxChecks = (int)Math.Max(batch, config.L("history_scan_max_n", batch));
        var target = Math.Max(1, config.L("history_target_candidates", config.L("target_slots", 8))); var slots = Math.Max(1, config.L("target_slots", 8)); var categoryCap = Math.Max(1, config.L("max_category_positions", slots));
        bool Enough()
        {
            var unique = checkedCandidates.Values.Where(c => c.B("strategy_eligible")).GroupBy(c => c.S("limit_group", c.L("id").ToString())).Select(g => g.First()).ToList();
            return unique.Count >= target && unique.GroupBy(c => c.S("category", "OTHER")).Sum(g => Math.Min(g.Count(), categoryCap)) >= slots;
        }
        var pending = ranked.Where(c => !checkedCandidates.ContainsKey((int)c.L("id"))).Take(maxChecks).ToList();
        using var throttle = new SemaphoreSlim(4);
        for (var offset = 0; offset < pending.Count; offset += batch)
        {
            if (Enough()) break;
            var selected = pending.Skip(offset).Take(batch).ToArray();
            var histories = await Task.WhenAll(selected.Select(async c =>
            {
                var id = (int)c.L("id"); await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (historyCache.TryGetValue(id, out var cache) && now - cache.At >= 0 && now - cache.At < config.L("history_cache_seconds", 300)) return (Id: id, Points: cache.Points, Error: (string?)null);
                    var response = await Fetch("timeseries?timestep=5m&id=" + id, cancellationToken).ConfigureAwait(false);
                    var points = (response as JsonObject)?.Arr("data") ?? new JsonArray(); historyCache[id] = (now, points);
                    return (Id: id, Points: points, Error: (string?)null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { return (Id: id, Points: new JsonArray(), Error: ex.GetType().Name); }
                finally { throttle.Release(); }
            })).ConfigureAwait(false);
            foreach (var h in histories) { contexts[h.Id] = Strategy.ChartContext(h.Points.OfType<JsonObject>(), now, h.Id, config); if (h.Error is not null) errors[h.Id.ToString()] = h.Error; }
            foreach (var c in selected) { var id = (int)c.L("id"); checkedCandidates[id] = Strategy.Evaluate(c, contexts[id], config); }
        }
        var rows = report.Objects("candidates").Select(c => checkedCandidates.GetValueOrDefault((int)c.L("id")) ?? Strategy.Evaluate(c, null, config)).OrderByDescending(c => c.B("strategy_eligible")).ThenByDescending(c => c.D("selection_score")).ToList();
        report["candidates"] = J.A(rows); report["strategy_summary"] = J.O(("history_checked", contexts.Count), ("history_errors", errors), ("eligible", rows.Count(c => c.B("strategy_eligible"))), ("target_candidates", target), ("max_history_checks", maxChecks), ("thresholds_are_heuristics", true), ("history_cache_seconds", config.L("history_cache_seconds", 300)));
        foreach (var id in historyCache.OrderBy(p => p.Value.At).Take(Math.Max(0, historyCache.Count - 256)).Select(p => p.Key).ToArray()) historyCache.TryRemove(id, out _);
        return report;
    }
    public static JsonObject? VerifiedCandidate(JsonObject candidate, JsonObject? result, JsonObject config, long? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!candidate.B("strategy_eligible", true) || result is null || !result.B("verified") || result.L("item_id") != candidate.L("id") || (result["normal_margin_verified"] is not null && !result.B("normal_margin_verified")) || now - result.L("completed_at") < 0 || now - result.L("completed_at") > 120) return null;
        var paid = result.L("actual_buy_gp"); var received = result.L("actual_net_sale_gp"); if (paid <= 0 || received <= 0) return null;
        var lo = received; var hi = received + config.L("tax_cap_gp", 5_000_000) + 1; var id = (int)candidate.L("id");
        while (lo < hi) { var mid = lo + (hi - lo + 1) / 2; if (mid - Tax(mid, id, config) <= received) lo = mid; else hi = mid - 1; }
        var buy = Math.Max(candidate.L("indicative_buy_gp"), lo); var sell = Math.Min(candidate.L("indicative_sell_gp"), paid); var net = sell - Tax(sell, id, config) - buy;
        if (net < Math.Max(1, config.L("min_net_margin_gp", 1)) || net / (double)buy < candidate.D("roi_threshold", config.D("min_roi", .01))) return null;
        var c = candidate.Copy(); c["indicative_buy_gp"] = buy; c["indicative_sell_gp"] = sell; c["estimated_net_margin_gp"] = net; c["net_roi_pct"] = Math.Round(net / (double)buy * 100, 2); c["estimated_position_profit_gp"] = net * c.L("quantity_proxy"); c["margin_verified"] = true; c["verified_test_loss_gp"] = Math.Max(0, result.L("actual_test_loss_gp"));
        if (candidate["chart_context"] is JsonObject context) { c = Strategy.Evaluate(c, context, config); if (!c.B("strategy_eligible")) return null; }
        return c;
    }
    public static JsonObject? ExecutionCandidate(JsonObject candidate, JsonObject? result, JsonObject config, long? at = null)
    {
        var tested = VerifiedCandidate(candidate, result, config, at);
        if (tested != null) { tested["verification_source"] = "LIVE_MARGIN_TEST"; return tested; }
        if (!config.B("skip_margin_test_for_trusted_history", false) || !candidate.B("strategy_eligible", true)) return null;
        var chart = candidate.Obj("chart_context");
        var trusted = candidate.L("empirical_samples") >= config.L("trusted_history_min_samples", 3)
            && candidate.D("observed_profitable_fraction") >= config.D("trusted_history_min_profitable_fraction", .60)
            && candidate.D("observed_fill_fraction") >= config.D("trusted_history_min_fill_fraction", .70)
            && chart.S("status") == "READY" && chart.D("coverage_fraction") >= config.D("trusted_history_min_chart_coverage", .75)
            && candidate.L("price_age_seconds") <= config.L("trusted_history_max_price_age_seconds", 90)
            && candidate.S("risk") != "Elevated"
            && candidate.D("funded_expected_stress_profit_after_fill_gp") >= Strategy.MinimumExpectedProfit(candidate, config)
            && candidate.D("funded_expected_profit_per_hour_proxy_gp") >= Strategy.MinimumExpectedHourly(candidate, config);
        if (!trusted) return null;
        var c = candidate.Copy();
        c["margin_verified"] = false;
        c["execution_price_verified"] = true;
        c["verification_source"] = "FRESH_MARKET_AND_ACCOUNT_HISTORY";
        c["verified_test_loss_gp"] = 0;
        return c;
    }
    public static JsonObject Screen(JsonObject data, JsonObject config, long? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(); var candidates = new List<JsonObject>(); var rejected = new JsonObject(); var flagged = new JsonArray();
        var mapping = data.Objects("mapping").ToArray(); var groupLimits = new Dictionary<string, long>(); var metadata = new JsonArray();
        foreach (var m in mapping)
        {
            var group = LimitRules.LimitGroup((int)m.L("id"), m.S("name")); metadata.Add(J.O(("id", m.L("id")), ("group", group)));
            if (Positive(m["limit"], out var limit)) groupLimits[group] = Math.Min(groupLimits.GetValueOrDefault(group, limit), limit);
        }
        void Reject(string reason) => rejected[reason] = rejected.L(reason) + 1;
        var latest = data.Obj("latest")?.Obj("data") ?? new(); var f = data.Obj("5m") ?? new(); var h = data.Obj("1h") ?? new(); var d = data.Obj("24h") ?? new();
        foreach (var meta in mapping)
        {
            var id = (int)meta.L("id"); var key = id.ToString(); var quote = latest[key] as JsonObject;
            if (quote is null || !Positive(quote["high"], out var high) || !Positive(quote["low"], out var low) || !Positive(meta["limit"], out var limit) || high < low) { Reject("missing_or_crossed_price"); continue; }
            if (!Fresh(quote["high"], quote["highTime"], now, config.L("max_price_age_seconds", 300)) || !Fresh(quote["low"], quote["lowTime"], now, config.L("max_price_age_seconds", 300))) { Reject("stale_quote"); continue; }
            if (now - f.L("timestamp") < 0 || now - f.L("timestamp") > config.L("max_average_age_seconds", 900)) { Reject("stale_volume"); continue; }
            var five = f.Obj("data")?[key] as JsonObject ?? new(); var hour = h.Obj("data")?[key] as JsonObject ?? new(); var day = d.Obj("data")?[key] as JsonObject ?? new();
            var v = new[] { five.D("highPriceVolume"), five.D("lowPriceVolume") }; var hv = new[] { hour.D("highPriceVolume"), hour.D("lowPriceVolume") }; var dv = new[] { day.D("highPriceVolume"), day.D("lowPriceVolume") };
            if (v.Min() < config.L("min_each_side_5m_volume", 20)) { Reject("low_volume"); continue; }
            var averages = new[] { hour.D("avgHighPrice"), hour.D("avgLowPrice") }; var daily = new[] { day.D("avgHighPrice"), day.D("avgLowPrice") }; var fp = new[] { five.D("avgHighPrice"), five.D("avgLowPrice") };
            if (averages.Any(p => p <= 0) || now - h.L("timestamp") < 0 || now - h.L("timestamp") > 7200) { Reject("missing_hour_baseline"); continue; }
            if (hv.Min() < config.L("min_each_side_1h_volume", 1) || hv.Sum() < config.L("min_total_1h_volume", 500)) { Reject("low_hourly_volume"); continue; }
            if (daily.Any(p => p <= 0) || dv.Min() <= 0 || now - d.L("timestamp") < 0 || now - d.L("timestamp") > 172800) { Reject("missing_daily_baseline"); continue; }
            if (fp.Any(p => p <= 0)) { Reject("missing_short_term_prices"); continue; }
            var declines = averages.Select((p, i) => p / daily[i] - 1).ToArray(); var changes = fp.Select((p, i) => p / averages[i] - 1).ToArray(); var spike = v.Sum() / (hv.Sum() / 12);
            var falling = Enumerable.Range(0, 2).Any(i => declines[i] < -config.D("falling_price_fraction", .03) && changes[i] < 0); var dump = spike > config.D("volume_spike_multiple", 4) && changes.Min() < -config.D("crash_fraction", .03);
            if (falling || dump)
            {
                var flag = dump ? "possible_dump" : "falling_price"; flagged.Add(J.O(("id", id), ("name", meta.S("name")), ("flag", flag), ("buy_price", low), ("sell_price", high), ("hour_vs_day_change_pct", Math.Round(declines.Min() * 100, 2)), ("five_vs_hour_change_pct", Math.Round(changes.Min() * 100, 2)), ("five_minute_volume_multiple", Math.Round(spike, 2)), ("note", "Wait for stabilization; this flag is not proof of manipulation."))); Reject(flag); continue;
            }
            var deviation = Math.Max(Math.Abs(high / averages[0] - 1), Math.Abs(low / averages[1] - 1)); if (deviation > config.D("max_price_deviation", .1)) { Reject("price_jump"); continue; }
            // Compare the short window with the hourly baseline and the
            // hourly window with the previous day.  These ratios are used as
            // soft confidence signals in ranking; the hard dump/falling-knife
            // filters above remain authoritative.
            var shortVsHour = v.Sum() * 12d / Math.Max(1, hv.Sum());
            var hourVsDayAverage = hv.Sum() / Math.Max(1, dv.Sum() / 24d);
            var shortShape = Math.Clamp(1 - Math.Abs(shortVsHour - 1) * config.D("short_volume_shape_penalty", .25), .35, 1);
            var dayShape = Math.Clamp(1 - Math.Abs(hourVsDayAverage - 1) * config.D("hour_volume_shape_penalty", .12), .45, 1);
            var volumeProfile = Math.Round(shortShape * dayShape, 4);
            var net = high - Tax(high, id, config) - low; var category = Strategy.ItemCategory(meta.S("name"));
            var consumable = config.Arr("consumable_ids")?.Any(n => TryInteger(n, out var cid) && cid == id) == true || new[] { "RUNES", "AMMUNITION", "POTIONS", "FOOD" }.Contains(category) || meta.S("name").Contains("potion(", StringComparison.OrdinalIgnoreCase) || meta.S("name").Contains("restore(", StringComparison.OrdinalIgnoreCase) || meta.S("name").Contains("brew(", StringComparison.OrdinalIgnoreCase);
            var roiThreshold = consumable && hv.Sum() > 10000 ? config.D("fast_consumable_min_roi", .006) : config.D("min_roi", .01);
            if (net < config.L("min_net_margin_gp", 1) || net <= 0 || net / (double)low < roiThreshold) { Reject("insufficient_net_margin"); continue; }
            var testLoss = high - (low - Tax(low, id, config)); if (config.B("monetary_limits_enabled", true) && testLoss > config.L("max_test_loss_gp", 1000000)) { Reject("estimated_test_loss"); continue; }
            var participation = config.D("volume_participation", .08); var buyFlow = Math.Min(v[1] * 12, Math.Min(hv[1], dv[1] / 24)) * participation; var sellFlow = Math.Min(v[0] * 12, Math.Min(hv[0], dv[0] / 24)) * participation;
            var cycleTarget = config.D("position_cycle_target_minutes"); long quantity;
            if (cycleTarget > 0 && buyFlow > 0 && sellFlow > 0) { cycleTarget = Math.Clamp(cycleTarget, 5, 60); quantity = Math.Min(limit, (long)(cycleTarget / (60 * (1 / buyFlow + 1 / sellFlow)))); }
            else quantity = Math.Min(limit, (long)(v.Min() * participation));
            if (config.B("monetary_limits_enabled", true) && config["budget_gp"] is not null)
            {
                var budget = config.L("budget_gp", int.MaxValue); var fraction = Math.Clamp(config.D("max_item_budget_fraction", .25), 0, 1);
                quantity = Math.Min(quantity, Math.Min((long)(budget * fraction), config.L("max_slot_allocation_gp", int.MaxValue)) / low);
                if (high > budget) { Reject("test_exceeds_budget"); continue; }
            }
            if (quantity <= 0 || buyFlow <= 0 || sellFlow <= 0) { Reject("position_too_small"); continue; }
            var cycle = 60 * quantity * (1 / buyFlow + 1 / sellFlow); var velocity = cycle <= 10 ? "Ultra-Fast" : cycle <= 60 ? "Medium" : "Slow"; var group = LimitRules.LimitGroup(id, meta.S("name"));
            candidates.Add(J.O(("id", id), ("name", meta.S("name")), ("members", meta.B("members")), ("limit_group", group), ("consumable", consumable), ("roi_threshold", roiThreshold), ("category", category),
                ("strategy_eligible", false), ("strategy_reasons", new JsonArray("HISTORY_NOT_CHECKED")), ("indicative_buy_gp", low), ("indicative_sell_gp", high), ("estimated_net_margin_gp", net), ("estimated_test_loss_gp", testLoss), ("quantity_proxy", quantity), ("estimated_position_profit_gp", quantity * net), ("estimated_profit_after_test_gp", quantity * net - testLoss), ("test_break_even_quantity", (testLoss + net - 1) / net),
                ("volume_5m_buy", (long)v[0]), ("volume_5m_sell", (long)v[1]), ("volume_1h_buy", (long)hv[0]), ("volume_1h_sell", (long)hv[1]), ("net_roi_pct", Math.Round(net / (double)low * 100, 2)), ("profit_per_full_4h_limit_gp", net * limit), ("volume_24h_buy", (long)dv[0]), ("volume_24h_sell", (long)dv[1]),
                ("velocity", velocity), ("cycle_minutes_proxy", Math.Round(cycle, 2)), ("position_cycle_target_minutes", cycleTarget > 0 ? Math.Round(cycleTarget, 2) : (double?)null), ("buy_flow_per_hour_proxy", Math.Round(buyFlow, 2)), ("sell_flow_per_hour_proxy", Math.Round(sellFlow, 2)), ("capital_turnover_per_hour_proxy", Math.Round(60 / cycle, 3)),
                ("five_minute_volume_multiple", Math.Round(spike, 3)), ("volume_5m_vs_1h_multiple", Math.Round(shortVsHour, 3)), ("volume_1h_vs_24h_hourly_average", Math.Round(hourVsDayAverage, 3)), ("volume_profile_score", volumeProfile),
                ("risk", spike >= config.D("volume_spike_multiple", 4) * .75 || volumeProfile < config.D("elevated_volume_profile_threshold", .55) ? "Elevated" : "Standard"), ("flippers_note", $"Two-sided liquidity supports a {velocity} screening rating. Hold time cannot be established from aggregate trades; reassess on fresh prices and actual fills. Spread may disappear."), ("hour_deviation_pct", Math.Round(deviation * 100, 2)), ("buy_limit", groupLimits[group]), ("price_age_seconds", Math.Max(now - quote.L("highTime"), now - quote.L("lowTime"))), ("margin_verified", false)));
        }
        return J.O(("generated_at_epoch", now), ("mode", "read_only"), ("budget_gp", config["budget_gp"]), ("limit_metadata", metadata), ("exit_quotes", BuildExitQuotes(data, config, now)),
            ("baseline_windows", J.O(("5m_start", f.L("timestamp")), ("1h_start", h.L("timestamp")), ("24h_start", d.L("timestamp")), ("type", "Previous completed UTC buckets, not rolling windows"))),
            ("candidates", J.A(candidates.OrderByDescending(c => c.D("capital_turnover_per_hour_proxy")).ThenByDescending(c => c.L("estimated_position_profit_gp")))), ("flagged_items", flagged), ("rejected_counts", rejected),
            ("allocation_note", "Focused profile: reserve capital for the preferred conviction positions, then use additional GE slots as cash is released; do not force trades when candidates are weak."), ("timing_note", "Weekend activity may differ; use measured volume rather than assuming a weekend increase."),
            ("limitations", new JsonArray("API prices are historical trades, not guaranteed executable offers", "Tax exemptions cover a verified subset only; other exemptions may be conservatively overtaxed", "Buy limit usage for this account is unknown", "Executable targets require a fresh live margin test and account verification", "No budget means quantity is a volume proxy only", "1h deviation is a jump filter, not a full volatility model", "Volumes count item units, not individual transactions")));
    }
}
