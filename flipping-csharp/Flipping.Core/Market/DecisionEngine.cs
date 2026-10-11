using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public static class DecisionEngine
{
    public static string Session(long now)
    {
        var hour = DateTimeOffset.FromUnixTimeSeconds(now).UtcDateTime.Hour;
        return hour >= 17 && hour < 23 ? "PEAK" : hour >= 2 && hour < 8 ? "OFF_PEAK" : "NORMAL";
    }
    public static (JsonArray Rows, JsonObject Diagnostics) Decisions(JsonObject report, JsonObject config, Portfolio portfolio, long now)
    {
        var rows = new JsonArray(); var details = new JsonArray();
        var healthy = portfolio.Verified && portfolio.BuyLimitsVerified && now - portfolio.ObservedAt >= 0 && now - portfolio.ObservedAt <= 30 && portfolio.FreeCashGp >= 0
            && portfolio.Offers.Select(o => o.Slot).Distinct().Count() == portfolio.Offers.Count && portfolio.Offers.All(o => o.OpenedAt <= now && (o.LastProgressAt is null || o.LastProgressAt <= now));
        var reportAt = report.L("generated_at_epoch");
        var reportHasTimestamp = reportAt > 0;
        var reportAge = reportHasTimestamp ? now - reportAt : 0;
        var reportFresh = !reportHasTimestamp || reportAge is >= 0 and <= 120;
        var allocationCount = Strategy.EligiblePositionCount(report.Objects("candidates"), config, portfolio);
        var (cap, cash) = Strategy.AllocationLimits(config, portfolio, allocationCount);
        JsonObject Row(int id, string name, long buy, long sell, long profit, string risk, string action) => J.O(("item_id", id), ("item_name", name), ("recommended_buy_price", buy), ("recommended_sell_price", sell), ("net_profit_per_item", profit), ("max_slot_allocation_gp", cap), ("stagnation_timeout_minutes", config.L("stagnation_timeout_minutes", 12)), ("risk_level", risk), ("action", action));
        long NetMargin(JsonObject c)
        {
            var buy = c.L("indicative_buy_gp"); var sell = c.L("indicative_sell_gp");
            return buy > 0 && sell > 0 ? sell - MarketAnalyzer.Tax(sell, (int)c.L("id"), config) - buy : c.L("estimated_net_margin_gp");
        }
        var byId = report.Objects("candidates").GroupBy(c => c.L("id")).ToDictionary(g => g.Key, g => g.First());
        foreach (var flagged in report.Objects("flagged_items"))
        {
            var id = (int)flagged.L("id"); if (portfolio.Offers.Any(o => o.ItemId == id)) continue;
            var buy = flagged.L("buy_price"); var sell = flagged.L("sell_price");
            rows.Add(Row(id, flagged.S("name"), buy, sell, sell - MarketAnalyzer.Tax(sell, id, config) - buy, "HIGH", "HOLD"));
            details.Add(J.O(("item_id", id), ("reason", flagged.S("flag") == "possible_dump" ? "DUMP ALERT" : "FALLING KNIFE")));
        }
        foreach (var offer in portfolio.Offers)
        {
            if (offer.Filled == offer.Quantity) continue;
            var stagnant = LimitRules.IsStagnant(now, offer.OpenedAt, offer.LastProgressAt, config); var c = byId.GetValueOrDefault(offer.ItemId);
            var offerBuy = c?.L("indicative_buy_gp") ?? (offer.Side == "BUY" ? offer.Price : 0); var offerSell = c?.L("indicative_sell_gp") ?? (offer.Side == "SELL" ? offer.Price : 0);
            var offerProfit = c is null ? 0 : NetMargin(c);
            rows.Add(Row(offer.ItemId, offer.ItemName, offerBuy, offerSell, offerProfit, c is null ? "HIGH" : "MEDIUM", healthy && stagnant ? "CANCEL" : "HOLD"));
            details.Add(J.O(("item_id", offer.ItemId), ("slot", offer.Slot), ("reason", stagnant ? "STAGNANT" : "Existing offer"), ("fill_fraction", offer.Filled / (double)offer.Quantity)));
        }
        var slotsLeft = Math.Max(0, LimitRules.SlotCapacity(config, portfolio) - portfolio.Offers.Count);
        var activeIds = portfolio.Offers.Select(o => o.ItemId).ToHashSet(); var activeGroups = portfolio.Offers.Select(o => LimitRules.LimitGroup(o.ItemId, o.ItemName)).ToHashSet();
        var categories = portfolio.Offers.GroupBy(o => Strategy.ItemCategory(o.ItemName)).ToDictionary(g => g.Key, g => g.Count());
        var session = Session(now);
        var rankedCandidates = Strategy.RankFundedCandidates(report.Objects("candidates"), config, portfolio)
            .Select(c =>
            {
                var explanation = DecisionPolicy.Explain(c, session, config);
                foreach (var pair in explanation) c[pair.Key] = pair.Value?.DeepClone();
                return c;
            })
            .OrderByDescending(c => c.B("strategy_eligible", true))
            .ThenByDescending(c => c.D("decision_score"))
            .ThenByDescending(c => c.D("funded_selection_score"))
            .ThenByDescending(c => c.D("market_selection_score"))
            .ToList();
        foreach (var c in rankedCandidates)
        {
            var id = (int)c.L("id"); if (activeIds.Contains(id)) continue;
            var group = c.S("limit_group", LimitRules.LimitGroup(id, c.S("name"))); var remaining = Math.Max(0, c.L("buy_limit") - LimitRules.UsedLimit(c, portfolio));
            var qty = Math.Min(c.L("funded_quantity", c.L("quantity_proxy")), Math.Min(remaining, Math.Min(cash, cap) / Math.Max(1, c.L("indicative_buy_gp"))));
            var category = c.S("category", Strategy.ItemCategory(c.S("name"))); var diversified = categories.GetValueOrDefault(category) < config.L("max_category_positions", 2);
            var cost = c.L("verified_test_loss_gp", c.L("estimated_test_loss_gp"));
            var nominalMargin = NetMargin(c); var stressMargin = c.L("stress_net_margin_gp", nominalMargin);
            var expectedFill = Math.Clamp(c.D("funded_expected_fill_fraction", c.D("expected_fill_fraction", 1)), .1, 1);
            var profit = qty * nominalMargin - cost; var stressProfit = qty * stressMargin - cost;
            var expectedProfit = qty * expectedFill * nominalMargin - cost; var expectedStressProfit = qty * expectedFill * stressMargin - cost;
            var minimumExpected = Strategy.MinimumExpectedProfit(c, config);
            var expectedHourly = expectedStressProfit * 60 / Math.Max(1, c.D("funded_cycle_minutes_proxy", 60));
            var decisionReasons = new List<string>();
            if (!healthy) decisionReasons.Add("PORTFOLIO_NOT_VERIFIED");
            if (!reportFresh) decisionReasons.Add("STALE_MARKET_REPORT");
            if (slotsLeft <= 0) decisionReasons.Add("NO_GE_SLOT");
            if (qty <= 0) decisionReasons.Add("INSUFFICIENT_CASH_OR_LIMIT");
            if (c.S("risk") == "Elevated") decisionReasons.Add("ELEVATED_MARKET_RISK");
            if (!c.B("strategy_eligible", true)) decisionReasons.AddRange(c.Arr("strategy_reasons").Select(n => n.S()));
            if (!diversified) decisionReasons.Add("CATEGORY_CAP");
            if (activeGroups.Contains(group)) decisionReasons.Add("LIMIT_FAMILY_ACTIVE");
            if (portfolio.Members != true && c.B("members")) decisionReasons.Add("MEMBERS_ITEM_UNAVAILABLE");
            if (nominalMargin <= 0) decisionReasons.Add("NON_POSITIVE_AFTER_TAX_MARGIN");
            if (expectedStressProfit < minimumExpected) decisionReasons.Add("LOW_EXPECTED_STRESS_PROFIT");
            if (expectedHourly < Strategy.MinimumExpectedHourly(c, config)) decisionReasons.Add("LOW_EXPECTED_CAPITAL_VELOCITY");
            if (c.D("decision_score") < config.D("minimum_decision_score", 0)) decisionReasons.Add("DECISION_SCORE_BELOW_THRESHOLD");
            var canBuy = decisionReasons.Count == 0;
            var rowRisk = c.S("risk") == "Elevated" || c.S("velocity") == "Slow" || !c.B("strategy_eligible", true) || expectedFill < .6 ? "MEDIUM" : "LOW";
            var row = Row(id, c.S("name"), c.L("indicative_buy_gp"), c.L("indicative_sell_gp"), nominalMargin, rowRisk, canBuy ? "BUY" : "HOLD");
            row["funded_allocation_position_count"] = allocationCount;
            row["decision_score"] = c.D("decision_score"); row["decision_confidence"] = c.D("decision_confidence"); row["decision_grade"] = c.S("decision_grade");
            rows.Add(row);
            details.Add(J.O(("item_id", id), ("planned_quantity", canBuy ? qty : 0), ("planned_cost_gp", canBuy ? qty * c.L("indicative_buy_gp") : 0), ("expected_fill_fraction", expectedFill), ("strategy_reasons", c.Arr("strategy_reasons")), ("decision_reasons", J.A(decisionReasons.Select(s => (JsonNode?)JsonValue.Create(s)))), ("selection_score", c.D("selection_score")), ("decision_score", c.D("decision_score")), ("decision_confidence", c.D("decision_confidence")), ("decision_grade", c.S("decision_grade")), ("decision_factors", c.Obj("decision_factors")), ("market_quality_score", c.D("market_quality_score", c.D("funded_market_quality_score"))), ("category", category), ("category_cap_reached", !diversified), ("limit_group", group), ("remaining_buy_limit", remaining), ("potential_profit_after_test_gp", profit), ("potential_stress_profit_after_test_gp", stressProfit), ("expected_profit_after_fill_gp", expectedProfit), ("expected_stress_profit_after_fill_gp", expectedStressProfit), ("funded_selection_score", c.D("funded_selection_score")), ("funded_cycle_minutes_proxy", c.D("funded_cycle_minutes_proxy")), ("strategy_style", c.S("strategy_style")), ("trade_thesis", c.Obj("trade_thesis")), ("reason", canBuy ? "Eligible" : string.Join(", ", decisionReasons))));
            if (canBuy) { cash -= qty * c.L("indicative_buy_gp"); slotsLeft--; activeIds.Add(id); activeGroups.Add(group); categories[category] = categories.GetValueOrDefault(category) + 1; }
        }
        return (rows, J.O(("session_gmt", Session(now)), ("slot_capacity", LimitRules.SlotCapacity(config, portfolio)), ("portfolio_verified", healthy), ("market_report_fresh", reportFresh), ("market_report_age_seconds", reportAge), ("details", details), ("execution", "Intents only; no game orders submitted")));
    }
}
