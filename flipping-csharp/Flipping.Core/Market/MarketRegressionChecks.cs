using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public static class MarketRegressionChecks
{
    public static async Task<JsonObject> RunApiChecksAsync(JsonObject? suppliedConfig = null)
    {
        var config = suppliedConfig?.Copy() ?? new JsonObject(); config["tax_percent"] = 2; config["tax_cap_gp"] = 5_000_000; config["tax_exempt_ids"] = new JsonArray(); config["monetary_limits_enabled"] = false; config["position_cycle_target_minutes"] = 0;
        var now = J.Now; var requests = new List<string>();
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery; requests.Add(path);
            if (path.Contains("mapping")) return new JsonArray(J.O(("id", 1), ("name", "Fixture"), ("limit", 100)));
            if (path.Contains("timeseries")) return J.O(("data", J.A(Enumerable.Range(1, 12).Select(i => J.O(("timestamp", now - i * 300), ("avgHighPrice", 1100), ("avgLowPrice", 1000), ("highPriceVolume", 1000), ("lowPriceVolume", 1000))))));
            if (path.Contains("latest")) return J.O(("data", J.O(("1", J.O(("high", 1100), ("low", 1000), ("highTime", now), ("lowTime", now))))));
            var volume = path.Contains("24h") ? 24000 : path.Contains("5m") ? 100 : 1000;
            return J.O(("timestamp", now - 300), ("data", J.O(("1", J.O(("avgHighPrice", 1100), ("avgLowPrice", 1000), ("highPriceVolume", volume), ("lowPriceVolume", volume))))));
        });
        using var http = new HttpClient(handler); var analyzer = new MarketAnalyzer(config, http); var checks = new JsonArray();
        void Check(bool condition, string name) { if (!condition) throw new InvalidDataException("Market API regression: " + name); checks.Add(name); }
        var noItems = await analyzer.FetchExitQuotesAsync([]); Check(requests.Count == 0 && noItems.Arr("exit_quotes").Count == 0, "empty owned stock skips HTTP");
        var exits = await analyzer.FetchExitQuotesAsync([J.O(("id", 1), ("name", " Fixture ")), J.O(("id", 1), ("name", "Duplicate")), J.O(("id", true), ("name", "Invalid"))]);
        Check(requests.Count == 1 && requests[0].EndsWith("latest"), "owned exit refresh uses one latest endpoint only");
        Check(exits.Objects("exit_quotes").Single().S("name") == "Fixture", "owned metadata deduplicated and trimmed");
        var first = await analyzer.AnalyzeAsync(); Check(first.Objects("candidates").Single().B("strategy_eligible"), "native five-window and history scan produces eligible candidate");
        Check(first.Obj("strategy_summary").L("history_checked") == 1, "history context attached to report");
        Check(first.Objects("exit_quotes").Single().L("indicative_sell_gp") == 1100, "full scan preserves independent exits");
        var historyCalls = requests.Count(p => p.Contains("timeseries")); await analyzer.AnalyzeAsync(); Check(requests.Count(p => p.Contains("timeseries")) == historyCalls, "fresh history cache avoids duplicate HTTP");
        Check(http.DefaultRequestHeaders.UserAgent.Any(), "Wiki API identification header supplied");
        return J.O(("passed", checks.Count), ("checks", checks));
    }
    private sealed class StubHandler(Func<HttpRequestMessage, JsonNode> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(reply(request).ToJsonString(), System.Text.Encoding.UTF8, "application/json") });
    }
    public static JsonObject Run(JsonObject? suppliedConfig = null)
    {
        var config = suppliedConfig?.Copy() ?? new JsonObject();
        config["tax_percent"] = 2; config["tax_cap_gp"] = 5_000_000; config["tax_exempt_ids"] = new JsonArray(); config["position_cycle_target_minutes"] = 0; config["monetary_limits_enabled"] = false;
        config["max_price_age_seconds"] = 300; config["min_net_margin_gp"] = 1; config["min_roi"] = .01; config["volume_spike_multiple"] = 4; config["volume_participation"] = .08;
        var checks = new JsonArray();
        void Check(bool condition, string name) { if (!condition) throw new InvalidDataException("Market regression: " + name); checks.Add(name); }
        JsonObject Snapshot()
        {
            JsonObject Bucket(long volume, long high = 1100, long low = 1000) => J.O(("timestamp", 900), ("data", J.O(("1", J.O(("highPriceVolume", volume), ("lowPriceVolume", volume), ("avgHighPrice", high), ("avgLowPrice", low))))));
            return J.O(("mapping", new JsonArray(J.O(("id", 1), ("name", "Fixture"), ("limit", 100)))), ("latest", J.O(("data", J.O(("1", J.O(("high", 1100), ("low", 1000), ("highTime", 999), ("lowTime", 999))))))), ("5m", Bucket(100)), ("1h", Bucket(1000)), ("24h", Bucket(24000)));
        }
        Check(MarketAnalyzer.Tax(49, 1, config) == 0 && MarketAnalyzer.Tax(50, 1, config) == 1, "integer floor tax");
        Check(MarketAnalyzer.Tax(1_000_000_000, 1, config) == 5_000_000, "5M tax cap");
        var exempt = config.Copy(); exempt["tax_exempt_ids"] = new JsonArray(1); Check(MarketAnalyzer.Tax(1000, 1, exempt) == 0, "tax exemptions");
        Check(LimitRules.LimitGroup(1, "Prayer potion(4)") == LimitRules.LimitGroup(2, "Prayer potion(1)") && LimitRules.LimitGroup(1, "Ring(4)") != LimitRules.LimitGroup(2, "Ring(1)"), "documented shared potion limits");
        Check(LimitRules.SlotCapacity(config, new Portfolio { SlotCapacity = 3 }) == 3 && LimitRules.SlotCapacity(config, new Portfolio { SlotCapacity = 8 }) == 8, "observed membership slot capacity");
        Check(LimitRules.IsStagnant(1720, 1000, null, config) && !LimitRules.IsStagnant(1719, 1000, null, config), "12-minute stagnation boundary");
        var data = Snapshot(); var report = MarketAnalyzer.Screen(data, config, 1000); var c = report.Objects("candidates").Single();
        Check(c.L("estimated_net_margin_gp") == 78 && c.L("estimated_test_loss_gp") == 120, "margin and reverse test tax");
        Check(c.L("quantity_proxy") == 8 && c.L("profit_per_full_4h_limit_gp") == 7800, "position versus full-limit profit");
        Check(c.D("volume_profile_score") > 0 && c.D("volume_5m_vs_1h_multiple") > 0 && c.D("volume_1h_vs_24h_hourly_average") > 0, "multi-window volume quality fields");
        var zeroVolume = Snapshot(); zeroVolume.Obj("1h").Obj("data").Obj("1")["highPriceVolume"] = 0;
        Check(!MarketAnalyzer.Screen(zeroVolume, config, 1000).Objects("candidates").Any() && MarketAnalyzer.Screen(zeroVolume, config, 1000).Objects("exit_quotes").Single().L("indicative_sell_gp") == 1100, "owned exits independent of volume rejection");
        var crossed = Snapshot(); crossed.Obj("latest").Obj("data").Obj("1")["high"] = 900;
        Check(MarketAnalyzer.Screen(crossed, config, 1000).Objects("exit_quotes").Single()["indicative_buy_gp"] is null, "crossed low preserves valid high exit");
        foreach (var bad in new JsonNode?[] { JsonValue.Create(true), JsonValue.Create("999"), JsonValue.Create(999d), JsonValue.Create(0), JsonValue.Create(1061), JsonValue.Create(1), null })
        {
            var invalid = Snapshot(); invalid.Obj("latest").Obj("data").Obj("1")["highTime"] = bad;
            Check(!MarketAnalyzer.Screen(invalid, config, 1000).Objects("exit_quotes").Any(), "strict stale/future exit timestamp " + (bad?.ToJsonString() ?? "null"));
        }
        var falling = Snapshot(); falling.Obj("24h").Obj("data").Obj("1")["avgHighPrice"] = 1500; falling.Obj("24h").Obj("data").Obj("1")["avgLowPrice"] = 1400; falling.Obj("5m").Obj("data").Obj("1")["avgHighPrice"] = 1090;
        Check(MarketAnalyzer.Screen(falling, config, 1000).Objects("flagged_items").Single().S("flag") == "falling_price", "falling knife requires downward direction");
        var dump = falling.Copy(); dump.Obj("5m").Obj("data").Obj("1")["highPriceVolume"] = 1000; dump.Obj("5m").Obj("data").Obj("1")["lowPriceVolume"] = 1000; dump.Obj("5m").Obj("data").Obj("1")["avgHighPrice"] = 1000;
        Check(MarketAnalyzer.Screen(dump, config, 1000).Objects("flagged_items").Single().S("flag") == "possible_dump", "dump spike and crash conjunction");
        config["require_chart_context"] = true; var points = Enumerable.Range(1, 12).Select(i => J.O(("timestamp", 10000 - i * 300), ("avgHighPrice", 1100), ("avgLowPrice", 1000), ("highPriceVolume", 1000), ("lowPriceVolume", 1000))).ToArray();
        var chart = Strategy.ChartContext(points, 10000, 1, config); var stable = c.Copy(); stable["quantity_proxy"] = 100;
        Check(chart.S("status") == "READY" && chart.L("buckets") == 12 && Strategy.Evaluate(stable, chart, config).B("strategy_eligible"), "stable two-sided history eligible");
        Check(!Strategy.Evaluate(stable, null, config).B("strategy_eligible"), "missing chart blocks new entries");
        Check(Strategy.ChartContext(Enumerable.Repeat(points[0], 12), 10000, 1, config).L("buckets") == 1, "duplicate history cannot inflate coverage");
        var verified = stable.Copy(); verified["strategy_eligible"] = true;
        var actual = J.O(("verified", true), ("item_id", 1), ("completed_at", 999), ("actual_buy_gp", 1050), ("actual_net_sale_gp", 980), ("actual_test_loss_gp", 70));
        Check(MarketAnalyzer.VerifiedCandidate(verified, actual, config, 1000)?.L("estimated_net_margin_gp") == 29, "reverse margin bounds net 29 GP");
        Check(MarketAnalyzer.VerifiedCandidate(verified, actual, config, 1120) is null, "stale margin cannot authorize entries");
        var focused = config.Copy(); focused["enable_tiered_growth_sizing"] = false; focused["budget_gp"] = 2_000_000; focused["cash_reserve_fraction"] = 0; focused["max_slot_allocation_gp"] = int.MaxValue; focused["max_item_budget_fraction"] = 1; focused["target_fill_slots"] = true; focused["capital_allocation_mode"] = "FOCUSED"; focused["preferred_capital_positions"] = 3; focused["target_slots"] = 8; focused["min_expected_trade_profit_gp"] = 5_000;
        var p = new Portfolio { FreeCashGp = 2_000_000, SlotCapacity = 8, Verified = true, BuyLimitsVerified = true, ObservedAt = 1000 };
        Check(Strategy.AllocationLimits(focused, p) == (666666, 2_000_000), "focused capital uses three convictions over eight slots");
        var evaluated = Strategy.Evaluate(stable, chart, focused);
        var cautious = evaluated.Copy(); cautious["market_quality_score"] = .4; cautious["expected_fill_fraction"] = .5;
        var normalFunding = Strategy.FundedEconomics(evaluated, focused, p); var cautiousFunding = Strategy.FundedEconomics(cautious, focused, p);
        Check(cautiousFunding.D("funded_selection_score") < normalFunding.D("funded_selection_score") && cautiousFunding.D("funded_expected_stress_profit_after_fill_gp") < normalFunding.D("funded_expected_stress_profit_after_fill_gp"), "quality and observed fill adjust funded ranking");
        var decisionReport = J.O(("candidates", new JsonArray(verified))); Check(DecisionEngine.Decisions(decisionReport, focused, p, 1000).Rows.OfType<JsonObject>().Single().S("action") == "BUY", "verified funded decision produces buy intent");
        p.BuyLimitsVerified = false; Check(DecisionEngine.Decisions(decisionReport, focused, p, 1000).Rows.OfType<JsonObject>().Single().S("action") == "HOLD", "unverified account cannot authorize new buy");
        p.BuyLimitsVerified = true;
        var lowFill = verified.Copy(); lowFill["expected_fill_fraction"] = .1;
        var lowFillReport = J.O(("candidates", new JsonArray(lowFill)));
        Check(DecisionEngine.Decisions(lowFillReport, focused, p, 1000).Rows.OfType<JsonObject>().Single().S("action") == "HOLD", "low observed fill evidence blocks weak expected trade");
        var growth = focused.Copy(); growth["enable_tiered_growth_sizing"] = true; growth["budget_gp"] = 30_000_000;
        growth["liquid_max_capital_fraction"] = .14; growth["liquid_min_expected_profit_gp"] = 1; growth["liquid_min_expected_gp_hour"] = 1;
        var growthPortfolio = new Portfolio { FreeCashGp = 30_000_000, SlotCapacity = 8, Members = true, Verified = true, BuyLimitsVerified = true, ObservedAt = 1000 };
        var liquid = evaluated.Copy(); liquid["consumable"] = true; liquid["volume_1h_buy"] = 50_000; liquid["volume_1h_sell"] = 50_000;
        liquid["quantity_proxy"] = 100_000; liquid["buy_limit"] = 100_000;
        var liquidFunding = Strategy.FundedEconomics(liquid, growth, growthPortfolio, 1);
        Check(liquidFunding.S("portfolio_tier") == "LIQUID_CORE" && liquidFunding.L("funded_allocation_cap_gp") == 4_200_000,
            "growth profile caps liquid position at fourteen percent of account capital");
        var trusted = liquid.Copy(); foreach (var pair in liquidFunding) trusted[pair.Key] = pair.Value?.DeepClone();
        trusted["empirical_samples"] = 3; trusted["observed_profitable_fraction"] = .67; trusted["observed_fill_fraction"] = .8; trusted["price_age_seconds"] = 10;
        growth["skip_margin_test_for_trusted_history"] = true; growth["trusted_history_min_samples"] = 3;
        Check(MarketAnalyzer.ExecutionCandidate(trusted, null, growth, 1000)?.S("verification_source") == "FRESH_MARKET_AND_ACCOUNT_HISTORY",
            "trusted profitable account history can skip a redundant margin test");
        var liquidPosition = J.O(("portfolio_tier", "LIQUID_CORE"));
        growth["liquid_stagnation_minutes"] = 6;
        Check(!LimitRules.IsStagnant(1359, 1000, null, growth, liquidPosition) && LimitRules.IsStagnant(1360, 1000, null, growth, liquidPosition),
            "liquid positions use a six minute progress timeout");
        var staleReport = decisionReport.Copy(); staleReport["generated_at_epoch"] = 700;
        var staleDiagnostics = DecisionEngine.Decisions(staleReport, focused, p, 1000).Diagnostics;
        Check(!staleDiagnostics.B("market_report_fresh") && staleDiagnostics.Objects("details").Single().Arr("decision_reasons").Any(n => n.S() == "STALE_MARKET_REPORT"), "stale report cannot authorize new buy");
        var poor = Enumerable.Range(0, 5).Select(i => J.O(("item_id", 1), ("stage", "DONE"), ("owned_quantity", 100), ("completed_at", 1000 + i), ("started_at", 100 + i), ("realized_profit_gp", -100))).ToArray();
        Check(!Adaptation.AdaptReport(decisionReport, poor, config).Objects("candidates").Single().B("strategy_eligible"), "observed losses can block new entry without relaxing safety");
        var partial = Enumerable.Range(0, 5).Select(i => J.O(("item_id", 1), ("stage", "DONE"), ("planned_quantity", 100), ("acquired_quantity", 25), ("owned_quantity", 0), ("completed_at", 2000 + i), ("started_at", 1400 + i), ("realized_profit_gp", 100))).ToArray();
        var adapted = Adaptation.AdaptReport(decisionReport, partial, config).Objects("candidates").Single();
        Check(adapted.D("observed_fill_fraction") == .25 && adapted.D("expected_fill_fraction") < .5 && adapted.D("observed_cycle_minutes_proxy") == 10, "partial fill history feeds conservative adaptation");
        return J.O(("passed", checks.Count), ("checks", checks));
    }
}

