using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OsrsFlipping.Core;

public static class Strategy
{
    private static readonly HashSet<string> Foods = ["shark", "manta ray", "anglerfish", "lobster", "swordfish", "tuna", "monkfish", "cooked karambwan", "salmon", "trout", "bass", "bread", "cake", "shrimps"];
    internal static double Median(IEnumerable<double> values)
    {
        var a = values.Order().ToArray();
        return a.Length == 0 ? 0 : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    private static double Clamp01(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    /// <summary>
    /// Combines independent market-quality signals into a conservative score.
    /// This is a ranking aid, not a fill probability.  A quote can still be
    /// rejected by the hard filters in <see cref="Evaluate"/>.
    /// </summary>
    public static double MarketQuality(JsonObject candidate, JsonObject config)
    {
        var balance = Clamp01(candidate.D("two_sided_volume_balance", 1));
        var coverage = Clamp01(candidate.Obj("chart_context").D("coverage_fraction", 1));
        var volatility = Math.Max(0, candidate.Obj("chart_context").D("median_abs_return_fraction", 0));
        var volatilityQuality = 1 / (1 + volatility * 100);
        var volumeShape = Clamp01(candidate.D("volume_profile_score", 1));
        // Keep the score interpretable and avoid allowing a single large
        // volume bucket to dominate a weak two-sided market.
        return Math.Round(Math.Clamp(balance * coverage * volatilityQuality * volumeShape, 0, 1), 4);
    }
    public static string ItemCategory(string name)
    {
        name = name.ToLowerInvariant();
        var basis = Regex.Replace(name, @"\(p\+*\)$", "").Trim();
        if (basis.EndsWith(" rune")) return "RUNES";
        if (new[] {"arrow", "bolt", "bolts", "dart", "cannonball"}.Any(basis.EndsWith)) return "AMMUNITION";
        if (Regex.IsMatch(name, @"\([1-4]\)$") && LimitRules.PotionWords.Any(name.Contains)) return "POTIONS";
        if (Foods.Contains(name)) return "FOOD";
        if (new[] {"logs", "ore", " bar", "essence", "arrowtips", "feather", "flax"}.Any(name.Contains)) return "MATERIALS";
        return "OTHER";
    }
    public static string PortfolioTier(JsonObject c)
    {
        var hourly = c.L("volume_1h_buy") + c.L("volume_1h_sell");
        var price = c.L("indicative_buy_gp");
        if (c.B("consumable") && hourly >= 20_000 || hourly >= 100_000) return "LIQUID_CORE";
        if (hourly >= 2_000 && price <= 3_000_000) return "MID_LIQUID";
        return "OPPORTUNITY";
    }
    public static long MinimumExpectedProfit(JsonObject c, JsonObject config) => !config.B("enable_tiered_growth_sizing", false)
        ? config.L("min_expected_trade_profit_gp", 1) : PortfolioTier(c) switch
    {
        "LIQUID_CORE" => config.L("liquid_min_expected_profit_gp", 12_000),
        "MID_LIQUID" => config.L("mid_min_expected_profit_gp", 35_000),
        _ => config.L("opportunity_min_expected_profit_gp", 75_000)
    };
    public static double MinimumExpectedHourly(JsonObject c, JsonObject config) => !config.B("enable_tiered_growth_sizing", false)
        ? config.D("minimum_expected_gp_hour", 0) : PortfolioTier(c) switch
    {
        "LIQUID_CORE" => config.D("liquid_min_expected_gp_hour", 18_000),
        "MID_LIQUID" => config.D("mid_min_expected_gp_hour", 25_000),
        _ => config.D("opportunity_min_expected_gp_hour", 30_000)
    };
    private static double TierCapitalFraction(JsonObject c, JsonObject config) => !config.B("enable_tiered_growth_sizing", false)
        ? config.D("max_item_budget_fraction", .25) : PortfolioTier(c) switch
    {
        "LIQUID_CORE" => config.D("liquid_max_capital_fraction", .14),
        "MID_LIQUID" => config.D("mid_max_capital_fraction", .18),
        _ => config.D("opportunity_max_capital_fraction", .20)
    };
    public static int AllocationPositionCount(JsonObject config, Portfolio portfolio, int? eligibleCandidateCount = null)
    {
        if (!config.B("target_fill_slots")) return 1;
        var free = Math.Max(1, LimitRules.SlotCapacity(config, portfolio) - portfolio.Offers.Count);
        var mode = config.S("capital_allocation_mode", "EQUAL_SLOTS").ToUpperInvariant();
        var available = Math.Clamp(eligibleCandidateCount ?? free, 1, free);
        return mode switch
        {
            "FOCUSED" => Math.Min(available, Math.Clamp(config.I("preferred_capital_positions", 3), 1, free)),
            "BALANCED" => available,
            _ => free
        };
    }
    /// <summary>Counts usable distinct opportunities, respecting the same account and diversification constraints as order entry.</summary>
    public static int EligiblePositionCount(IEnumerable<JsonObject> candidates, JsonObject config, Portfolio portfolio)
    {
        var slots = Math.Max(0, LimitRules.SlotCapacity(config, portfolio) - portfolio.Offers.Count);
        if (slots == 0) return 0;
        var uncapped = config.Copy(); uncapped["target_fill_slots"] = false;
        var (cap, spendable) = AllocationLimits(uncapped, portfolio);
        var ranked = candidates.OrderByDescending(c => c.D("selection_score", c.D("profit_per_hour_proxy_gp"))).ToArray();
        // The largest feasible split wins. Eight individually affordable items
        // can otherwise each become unaffordable after an eight-way split.
        for (var desired = slots; desired >= 1; desired--)
        {
            var perPosition = Math.Min(cap, spendable / desired);
            var groups = portfolio.Offers.Select(o => LimitRules.LimitGroup(o.ItemId, o.ItemName)).ToHashSet();
            var ids = portfolio.Offers.Select(o => (long)o.ItemId).ToHashSet();
            var categories = portfolio.Offers.GroupBy(o => ItemCategory(o.ItemName)).ToDictionary(g => g.Key, g => g.Count());
            var accepted = 0;
            foreach (var c in ranked)
            {
                var id = c.L("id"); var price = c.L("indicative_buy_gp");
                var group = c.S("limit_group", LimitRules.LimitGroup((int)id, c.S("name")));
                var category = c.S("category", ItemCategory(c.S("name")));
                var remaining = Math.Max(0, c.L("buy_limit") - LimitRules.UsedLimit(c, portfolio));
                var quantity = Math.Min(c.L("quantity_proxy"), Math.Min(remaining, perPosition / Math.Max(1, price)));
                var expectedProfit = quantity * Math.Clamp(c.D("expected_fill_fraction", 1), .1, 1)
                    * c.D("stress_net_margin_gp", c.D("estimated_net_margin_gp")) - c.L("verified_test_loss_gp", c.L("estimated_test_loss_gp"));
                if (id <= 0 || ids.Contains(id) || groups.Contains(group) || !c.B("strategy_eligible", true) || c.S("risk") == "Elevated"
                    || price <= 0 || quantity <= 0 || c.L("estimated_net_margin_gp") <= 0
                    || expectedProfit < MinimumExpectedProfit(c, config) || portfolio.Members != true && c.B("members")
                    || categories.GetValueOrDefault(category) >= config.L("max_category_positions", 2)) continue;
                accepted++; ids.Add(id); groups.Add(group); categories[category] = categories.GetValueOrDefault(category) + 1;
                if (accepted == desired) return desired;
            }
        }
        return 0;
    }
    public static (long Cap, long Spendable) AllocationLimits(JsonObject config, Portfolio portfolio, int? eligibleCandidateCount = null)
    {
        var reserved = portfolio.Offers.Where(o => o.Side == "BUY").Sum(o => o.Price * o.Quantity);
        var committed = Math.Max(portfolio.DeployedCostGp, reserved);
        var capital = Math.Min(config.L("budget_gp", int.MaxValue), portfolio.FreeCashGp + committed);
        var reserve = (long)Math.Ceiling(capital * config.D("cash_reserve_fraction", .1));
        var fraction = Math.Clamp(config.D("max_item_budget_fraction", .25), 0, 1);
        var cap = Math.Min(config.L("max_slot_allocation_gp", int.MaxValue), (long)(capital * fraction));
        var spendable = Math.Max(0, Math.Min(portfolio.FreeCashGp - reserve, capital - committed));
        if (config.B("target_fill_slots"))
        {
            var positions = AllocationPositionCount(config, portfolio, eligibleCandidateCount);
            cap = Math.Min(cap, spendable / positions);
        }
        return (cap, spendable);
    }
    public static (long Quantity, double Profit) TestPositionPlan(JsonObject c, JsonObject config, Portfolio portfolio, int? eligibleCandidateCount = null)
    {
        eligibleCandidateCount ??= c["funded_allocation_position_count"].IsInteger() ? c.I("funded_allocation_position_count") : null;
        var (cap, cash) = AllocationLimits(config, portfolio, eligibleCandidateCount);
        if (c.L("indicative_sell_gp") > Math.Min(cap, cash)) return (0, 0);
        var loss = c.L("estimated_test_loss_gp");
        var after = new Portfolio { FreeCashGp = Math.Max(0, portfolio.FreeCashGp - loss), DeployedCostGp = portfolio.DeployedCostGp, Offers = portfolio.Offers, SlotCapacity = portfolio.SlotCapacity };
        (cap, cash) = AllocationLimits(config, after, eligibleCandidateCount);
        var remaining = Math.Max(0, c.L("buy_limit") - LimitRules.UsedLimit(c, portfolio) - 1);
        var quantity = Math.Min(c.L("quantity_proxy"), Math.Min(remaining, Math.Min(cap, cash) / Math.Max(1, c.L("indicative_buy_gp"))));
        return (quantity, quantity * Math.Clamp(c.D("expected_fill_fraction", 1), .1, 1) * c.D("stress_net_margin_gp", c.D("estimated_net_margin_gp")) - loss);
    }
    public static JsonObject FundedEconomics(JsonObject c, JsonObject config, Portfolio portfolio, int? eligibleCandidateCount = null)
    {
        eligibleCandidateCount ??= c["funded_allocation_position_count"].IsInteger() ? c.I("funded_allocation_position_count") : null;
        var (cap, cash) = AllocationLimits(config, portfolio, eligibleCandidateCount);
        var remaining = Math.Max(0, c.L("buy_limit") - LimitRules.UsedLimit(c, portfolio));
        var price = c.L("indicative_buy_gp");
        var totalCapital = Math.Max(0, portfolio.FreeCashGp + portfolio.DeployedCostGp);
        cap = Math.Min(cap, (long)(totalCapital * Math.Clamp(TierCapitalFraction(c, config), .01, 1)));
        var qty = Math.Min(c.L("quantity_proxy"), Math.Min(remaining, Math.Min(cap, cash) / Math.Max(1, price)));
        var cost = c.L("verified_test_loss_gp", c.L("estimated_test_loss_gp"));
        var profit = qty * c.L("estimated_net_margin_gp") - cost;
        var stressProfit = qty * c.L("stress_net_margin_gp", c.L("estimated_net_margin_gp")) - cost;
        var baseCycle = c.D("cycle_minutes_proxy", 60 / Math.Max(c.D("capital_turnover_per_hour_proxy", 1), .01));
        var observedCycle = c.D("observed_cycle_minutes_proxy", 0);
        var cycle = Math.Max(config.D("min_cycle_proxy_minutes", 5), baseCycle * qty / Math.Max(c.L("quantity_proxy"), 1));
        if (observedCycle > 0) cycle = Math.Max(cycle, observedCycle);
        var chart = c.Obj("chart_context") ?? new();
        var quality = c.D("market_quality_score", MarketQuality(c, config));
        var expectedFill = Math.Clamp(c.D("expected_fill_fraction", 1), .1, 1);
        var expectedProfit = qty * expectedFill * c.L("estimated_net_margin_gp") - cost;
        var expectedStressProfit = qty * expectedFill * c.L("stress_net_margin_gp", c.L("estimated_net_margin_gp")) - cost;
        var expectedHourly = Math.Max(0, expectedStressProfit) * 60 / cycle;
        var score = expectedHourly * quality * c.D("empirical_ranking_factor", 1);
        var tier = PortfolioTier(c); var minProfit = MinimumExpectedProfit(c, config); var minHourly = MinimumExpectedHourly(c, config);
        return J.O(("funded_quantity", qty), ("funded_cost_gp", qty * price), ("funded_profit_after_test_gp", profit), ("funded_stress_profit_gp", stressProfit),
            ("funded_allocation_position_count", AllocationPositionCount(config, portfolio, eligibleCandidateCount)), ("funded_allocation_cap_gp", cap),
            ("funded_expected_fill_fraction", expectedFill), ("funded_expected_profit_after_fill_gp", expectedProfit), ("funded_expected_stress_profit_after_fill_gp", expectedStressProfit),
            ("funded_cycle_minutes_proxy", Math.Round(cycle, 2)), ("funded_profit_per_hour_proxy_gp", Math.Round(Math.Max(0, profit) * 60 / cycle, 2)),
            ("funded_expected_profit_per_hour_proxy_gp", Math.Round(Math.Max(0, expectedProfit) * 60 / cycle, 2)),
            ("funded_market_quality_score", quality), ("funded_selection_score", Math.Round(score, 2)), ("market_selection_score", c.D("selection_score", c.D("profit_per_hour_proxy_gp"))),
            ("portfolio_tier", tier), ("tier_min_expected_profit_gp", minProfit), ("tier_min_expected_gp_hour", minHourly),
            ("growth_threshold_met", expectedStressProfit >= minProfit && expectedHourly >= minHourly),
            ("strategy_style", cycle <= 20 ? "ACTIVE_SPREAD" : "PATIENT_SPREAD"), ("opportunity_basis", "Affordable quantity, remaining limit, stressed spread and two-sided flow; not a guaranteed return"));
    }
    public static List<JsonObject> RankFundedCandidates(IEnumerable<JsonObject> candidates, JsonObject config, Portfolio portfolio)
    {
        var available = candidates.ToList();
        var eligibleCount = EligiblePositionCount(available, config, portfolio);
        return available.Select(c => { var copy = c.Copy(); foreach (var p in FundedEconomics(c, config, portfolio, eligibleCount)) copy[p.Key] = p.Value?.DeepClone(); return copy; })
            .OrderByDescending(c => c.B("strategy_eligible", true)).ThenByDescending(c => c.D("funded_selection_score")).ThenByDescending(c => c.D("market_selection_score")).ToList();
    }
    public static JsonObject ChartContext(IEnumerable<JsonObject> points, long now, int itemId, JsonObject config)
    {
        var valid = points.Where(p => p.L("timestamp") >= now - 3600 && p.L("timestamp") <= now - 300).GroupBy(p => p.L("timestamp")).Select(g => g.Last()).OrderBy(p => p.L("timestamp"))
            .Where(p => p.D("avgHighPrice") > 0 && p.D("avgLowPrice") > 0 && p.L("highPriceVolume") > 0 && p.L("lowPriceVolume") > 0).ToArray();
        if (valid.Length < config.L("history_min_buckets", 8)) return J.O(("status", "INSUFFICIENT"), ("buckets", valid.Length), ("reason", "Insufficient two-sided closed 5m buckets"));
        var mids = valid.Select(p => (p.D("avgHighPrice") + p.D("avgLowPrice")) / 2).ToArray();
        var returns = mids.Skip(1).Select((p, i) => Math.Abs(p / mids[i] - 1));
        var drift = Median(mids.TakeLast(3)) / Median(mids.Take(3)) - 1;
        var spreads = valid.Select(p => p.D("avgHighPrice") - MarketAnalyzer.Tax((long)p.D("avgHighPrice"), itemId, config) - p.D("avgLowPrice")).ToArray();
        var lows = valid.Select(p => p.D("avgLowPrice")).Order().ToArray();
        var highs = valid.Select(p => p.D("avgHighPrice")).Order().ToArray();
        JsonArray Band(double[] v) => new(v[(v.Length - 1) / 4], v[(v.Length - 1) * 3 / 4]);
        return J.O(("status", "READY"), ("buckets", valid.Length), ("coverage_fraction", valid.Length / 12d), ("median_abs_return_fraction", Median(returns)),
            ("recent_drift_fraction", drift), ("positive_spread_fraction", spreads.Count(s => s > 0) / (double)spreads.Length), ("median_net_spread_gp", Median(spreads)),
            ("min_mid_gp", mids.Min()), ("max_mid_gp", mids.Max()), ("historical_buy_band_gp", Band(lows)), ("historical_sell_band_gp", Band(highs)), ("median_low_gp", Median(lows)), ("median_high_gp", Median(highs)));
    }
    public static JsonObject Evaluate(JsonObject candidate, JsonObject? context, JsonObject config)
    {
        var c = candidate.Copy(); var chart = context?.Copy() ?? J.O(("status", "NOT_CHECKED")); c["chart_context"] = chart;
        var reasons = new List<string>(); var buy = c.L("indicative_buy_gp"); var sell = c.L("indicative_sell_gp");
        var min = Math.Min(c.D("volume_1h_buy"), c.D("volume_1h_sell")); var max = Math.Max(c.D("volume_1h_buy"), c.D("volume_1h_sell"));
        var balance = max > 0 ? min / max : 0; c["two_sided_volume_balance"] = Math.Round(balance, 4);
        if (balance < config.D("min_volume_balance", .1)) reasons.Add("ONE_SIDED_FLOW");
        double volatility = 0;
        if (chart.S("status") != "READY") { if (config.B("require_chart_context", true)) reasons.Add("HISTORY_NOT_VERIFIED"); }
        else
        {
            volatility = chart.D("median_abs_return_fraction");
            if (chart.D("recent_drift_fraction") < -config.D("max_recent_decline_fraction", .02)) reasons.Add("RECENT_DOWNTREND");
            if (volatility > config.D("max_median_return_fraction", .015)) reasons.Add("UNSTABLE_PRICE");
            if (chart.D("positive_spread_fraction", 1) < config.D("min_positive_spread_fraction", .7)) reasons.Add("INCONSISTENT_SPREAD");
            if (c.D("estimated_net_margin_gp") > Math.Max(2, chart.D("median_net_spread_gp") * config.D("max_spread_multiple", 3))) reasons.Add("UNUSUAL_SPREAD");
        }
        var quality = MarketQuality(c, config);
        c["market_quality_score"] = quality;
        if (quality < config.D("min_market_quality_score", .2)) reasons.Add("LOW_MARKET_QUALITY");
        var haircut = Math.Max(1, (long)Math.Ceiling(sell * Math.Max(config.D("stress_price_fraction", .002), volatility)));
        var stressedSell = Math.Max(1, sell - haircut); var stressedBuy = buy + (long)Math.Ceiling(buy * config.D("stress_buy_fraction", .001));
        var stressedNet = stressedSell - MarketAnalyzer.Tax(stressedSell, (int)c.L("id"), config) - stressedBuy;
        if (stressedNet <= 0) reasons.Add("FRAGILE_MARGIN");
        c["stress_sell_price_gp"] = stressedSell; c["stress_net_margin_gp"] = stressedNet; c["stress_buy_price_gp"] = stressedBuy;
        var cost = c.L("verified_test_loss_gp", c.L("estimated_test_loss_gp")); var profit = c.L("quantity_proxy") * c.L("estimated_net_margin_gp") - cost;
        if (profit <= 0) reasons.Add("TEST_COST_NOT_RECOVERED"); if (c.L("quantity_proxy") * stressedNet - cost <= 0) reasons.Add("STRESS_TEST_COST_NOT_RECOVERED");
        var cycle = c.D("cycle_minutes_proxy", 60 / Math.Max(c.D("capital_turnover_per_hour_proxy", .01), .01));
        var stressProfit = Math.Max(0, c.L("quantity_proxy") * stressedNet - cost);
        c["estimated_profit_after_test_gp"] = profit; c["profit_per_hour_proxy_gp"] = Math.Round(Math.Max(0, profit) * 60 / Math.Max(cycle, 1), 2); c["stress_position_profit_after_test_gp"] = stressProfit;
        c["selection_score"] = Math.Round(stressProfit * 60 / Math.Max(cycle, 1) * quality, 2);
        c["strategy_reasons"] = J.A((reasons.Count > 0 ? reasons : ["TWO_SIDED_LIQUIDITY", "POSITIVE_STRESSED_MARGIN", "TEST_COST_RECOVERABLE"]).Select(s => (JsonNode?)JsonValue.Create(s)));
        c["strategy_eligible"] = reasons.Count == 0 && c.S("risk") != "Elevated"; c["score_type"] = "Heuristic, not a predicted return or fill probability";
        var band = chart["historical_buy_band_gp"] as JsonArray;
        var zone = band is null ? "UNVERIFIED" : buy < band[0]!.GetValue<double>() ? "BELOW_RECENT_RANGE" : buy > band[1]!.GetValue<double>() ? "ABOVE_RECENT_RANGE" : "WITHIN_RECENT_RANGE";
        c["trade_thesis"] = J.O(("method", cycle <= 20 ? "ACTIVE_SPREAD" : "PATIENT_SPREAD"), ("entry_price_zone", zone), ("historical_buy_band_gp", band), ("historical_sell_band_gp", chart["historical_sell_band_gp"]),
            ("entry_requires", "Fresh quote, exact account limits and verified margin test"), ("exit_review", "Track actual fills, refresh market, reprice only after reconciliation"), ("market_failures", J.A(reasons.Select(s => (JsonNode?)JsonValue.Create(s)))), ("basis", "Closed trade observations; not an order book or predicted price"));
        return c;
    }
}
