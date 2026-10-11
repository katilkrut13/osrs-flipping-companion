using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public static class AllocationRegressionChecks
{
    public static JsonObject Run(JsonObject? suppliedConfig = null)
    {
        var config = suppliedConfig?.Copy() ?? new JsonObject();
        config["budget_gp"] = 2_000_000; config["max_slot_allocation_gp"] = int.MaxValue;
        config["enable_tiered_growth_sizing"] = false;
        config["max_item_budget_fraction"] = 1; config["cash_reserve_fraction"] = 0;
        config["target_slots"] = 8; config["target_fill_slots"] = true;
        config["capital_allocation_mode"] = "BALANCED"; config["max_category_positions"] = 8;
        config["learning_min_samples"] = 3; config["learning_confidence_samples"] = 6;
        config["learning_loss_fraction_block"] = .4; config["require_chart_context"] = true;
        config["min_expected_trade_profit_gp"] = 1;
        config["minimum_decision_score"] = 0; config["simulation_slot_capacity"] = 8;
        var checks = new JsonArray();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidDataException("Allocation regression: " + name);
            checks.Add(name);
        }
        JsonObject Candidate(int id) => J.O(("id", id), ("name", "Fixture " + id), ("category", "RUNES"),
            ("buy_limit", 10000), ("quantity_proxy", 10000), ("indicative_buy_gp", 1000), ("indicative_sell_gp", 1123),
            ("estimated_net_margin_gp", 100), ("stress_net_margin_gp", 80), ("estimated_test_loss_gp", 0),
            ("cycle_minutes_proxy", 20), ("selection_score", 10000), ("profit_per_hour_proxy_gp", 10000),
            ("strategy_eligible", true), ("market_quality_score", 1), ("two_sided_volume_balance", 1),
            ("chart_context", J.O(("status", "READY"), ("coverage_fraction", 1), ("positive_spread_fraction", 1))));
        var portfolio = new Portfolio { FreeCashGp = 2_000_000, SlotCapacity = 8, Members = true, Verified = true,
            BuyLimitsVerified = true, ObservedAt = 1000 };
        var eight = Enumerable.Range(1, 8).Select(Candidate).ToArray();
        Check(Strategy.EligiblePositionCount(eight, config, portfolio) == 8,
            "balanced mode admits eight distinct eligible opportunities");
        Check(Strategy.RankFundedCandidates(eight, config, portfolio).All(c => c.L("funded_quantity") == 250 && c.L("funded_allocation_position_count") == 8),
            "eight opportunities each receive one eighth of available cash");
        Check(Strategy.RankFundedCandidates(eight.Take(3), config, portfolio).All(c => c.L("funded_quantity") == 666 && c.L("funded_allocation_position_count") == 3),
            "three eligible opportunities use available capital without five empty-slot reserves");
        var fundedOne = Strategy.RankFundedCandidates(eight.Take(1), config, portfolio).Single();
        Check(fundedOne.L("funded_quantity") == 2000 && fundedOne.L("funded_allocation_cap_gp") == 2_000_000,
            "single eligible opportunity uses its configured per-item allowance");
        var limited = config.Copy(); limited["max_item_budget_fraction"] = .25;
        Check(Strategy.RankFundedCandidates(eight.Take(1), limited, portfolio).Single().L("funded_cost_gp") == 500_000,
            "dynamic position count preserves explicit concentration limits");
        var constrained = config.Copy(); constrained["max_category_positions"] = 2;
        Check(Strategy.EligiblePositionCount(eight, constrained, portfolio) == 2,
            "category capacity reduces the funded position count");
        var family = eight[1].Copy(); family["limit_group"] = "shared";
        var sameFamily = eight[0].Copy(); sameFamily["limit_group"] = "shared";
        Check(Strategy.EligiblePositionCount([family, sameFamily, family.Copy()], config, portfolio) == 1,
            "duplicate items and shared limit families cannot dilute allocation");
        var unusable = eight[1].Copy(); unusable["strategy_eligible"] = false;
        var exhausted = eight[2].Copy(); exhausted["buy_limit"] = 0;
        var expensive = eight[3].Copy(); expensive["indicative_buy_gp"] = 3_000_000;
        Check(Strategy.EligiblePositionCount([eight[0], unusable, exhausted, expensive], config, portfolio) == 1,
            "rejected exhausted and unaffordable candidates do not reserve capital");
        var pricey = eight.Select(c => { var copy = c.Copy(); copy["indicative_buy_gp"] = 800_000; return copy; }).ToArray();
        Check(Strategy.EligiblePositionCount(pricey, config, portfolio) == 2
            && Strategy.RankFundedCandidates(pricey, config, portfolio).All(c => c.L("funded_quantity") == 1),
            "expensive candidates reduce the split instead of making all eight positions unaffordable");
        var costly = eight.Select(c => { var copy = c.Copy(); copy["estimated_test_loss_gp"] = 30_000; return copy; }).ToArray();
        Check(Strategy.EligiblePositionCount(costly, config, portfolio) == 5,
            "test cost is recoverable at the selected capital split");
        var f2p = new Portfolio { FreeCashGp = 2_000_000, SlotCapacity = 3, Members = false };
        var memberItem = eight[0].Copy(); memberItem["members"] = true;
        Check(Strategy.EligiblePositionCount(eight, config, f2p) == 3 && Strategy.EligiblePositionCount([memberItem], config, f2p) == 0,
            "observed membership limits slots and available items");
        foreach (var count in new[] { 1, 3, 8 })
        {
            var exchange = new SimExchange(config, 2_000_000);
            var observed = exchange.Observe();
            var observedPortfolio = new Portfolio { FreeCashGp = exchange.Cash, SlotCapacity = exchange.Capacity,
                Members = true, Verified = true, BuyLimitsVerified = true, ObservedAt = exchange.Now };
            var scanCandidates = eight.Take(count).Select(c => c.Copy()).ToArray();
            var scan = J.O(("generated_at_epoch", exchange.Now), ("candidates", J.A(scanCandidates.Cast<JsonNode?>())));
            var decision = DecisionEngine.Decisions(scan, config, observedPortfolio, exchange.Now);
            var rows = decision.Rows.OfType<JsonObject>().ToArray();
            var details = decision.Diagnostics.Objects("details").ToArray();
            var expectedCap = 2_000_000 / count; var expectedQuantity = expectedCap / 1000;
            Check(rows.Length == count && rows.All(r => r.S("action") == "BUY"
                    && r.L("max_slot_allocation_gp") == expectedCap && r.L("funded_allocation_position_count") == count)
                && details.Length == count && details.All(d => d.L("planned_quantity") == expectedQuantity),
                $"decision engine propagates balanced {count}-position caps and quantities into buy intents");
            var selected = scanCandidates.Single(c => c.L("id") == rows[0].L("item_id")).Copy();
            selected["funded_allocation_position_count"] = rows[0]["funded_allocation_position_count"]!.DeepClone();
            var plannedQuantity = details.Single(d => d.L("item_id") == selected.L("id")).L("planned_quantity");
            var manager = new OrderManager(exchange, config);
            var position = manager.Add(selected, plannedQuantity, observedPortfolio, observed, marginVerified: true);
            Check(!exchange.IsLive && position.S("stage") == "WAIT_BUY" && position.L("quantity") == expectedQuantity
                    && exchange.Offers.Single().Value.L("quantity") == expectedQuantity
                    && exchange.Cash == 2_000_000 - expectedQuantity * 1000,
                $"order manager accepts the decision engine's legitimate {count}-position allocation");
        }

        JsonObject Outcome(int i, long acquired, long profit = 1000) => J.O(("trade_id", "trade-" + i),
            ("item_id", 1), ("stage", "DONE"), ("quantity", 100), ("acquired_quantity", acquired),
            ("owned_quantity", 0), ("started_at", 1000 + i), ("completed_at", 1600 + i), ("realized_profit_gp", profit));
        var baseCandidate = Candidate(1); baseCandidate["quantity_proxy"] = 100;
        var report = J.O(("candidates", J.A(baseCandidate)));
        JsonObject Adapt(IEnumerable<JsonObject> outcomes) => Adaptation.AdaptReport(report, outcomes, config).Objects("candidates").Single();
        var successful = Enumerable.Range(0, 3).Select(i => Outcome(i, 100)).ToArray();
        var good = Adapt(successful);
        Check(good.D("observed_fill_fraction") == 1 && good.D("expected_fill_fraction") == 1 && good.L("quantity_proxy") == 100,
            "three full successful fills do not impose an arbitrary fifty-percent prior");
        var partial = Adapt(Enumerable.Range(0, 6).Select(i => Outcome(i, 25)));
        Check(partial.D("expected_fill_fraction") == .25 && partial.L("quantity_proxy") == 100,
            "partial fill evidence calibrates expected proceeds without shrinking offered quantity");
        var partialFunding = Strategy.FundedEconomics(partial, config, portfolio, 1);
        Check(partialFunding.L("funded_quantity") == 100 && partialFunding.D("funded_expected_profit_after_fill_gp") == 2500,
            "a quarter expected fill is applied once to profit rather than squared through sizing");
        var legacy = Enumerable.Range(0, 3).Select(i =>
        {
            var r = Outcome(i, 100); r.Remove("acquired_quantity"); r["owned_quantity"] = 100; return r;
        }).ToArray();
        Check(Adapt(legacy).D("observed_fill_fraction") == 1,
            "legacy owned-quantity outcomes retain their documented full fills");
        var missingProfit = Outcome(9, 100); missingProfit["realized_profit_gp"] = null;
        var zeroDuration = Outcome(10, 100); zeroDuration["completed_at"] = zeroDuration["started_at"]!.DeepClone();
        Check(Adapt([successful[0], successful[0].Copy(), missingProfit, zeroDuration]).L("empirical_samples") == 1,
            "duplicate incomplete and impossible outcomes cannot create learning confidence");
        Check(!Adapt(Enumerable.Range(0, 3).Select(i => Outcome(i, 100, -100))).B("strategy_eligible"),
            "repeated verified losses still block fresh entry");
        var adaptedAgain = Adaptation.AdaptReport(J.O(("candidates", J.A(partial))),
            Enumerable.Range(0, 6).Select(i => Outcome(i, 25)), config).Objects("candidates").Single();
        Check(adaptedAgain.D("selection_score") == partial.D("selection_score") && adaptedAgain.L("quantity_proxy") == partial.L("quantity_proxy"),
            "repeated calibration does not compound penalties");
        var externallyCollected = Enumerable.Range(3, 3).Select(i =>
        {
            var outcome = Outcome(i, 100); outcome["completed_at"] = 100_000 + i;
            outcome["external_collection_evidence"] = J.O(("confirmed", true), ("applied_at", 100_000 + i));
            return outcome;
        }).ToArray();
        var mixedTiming = Adapt(successful.Concat(externallyCollected));
        Check(mixedTiming.L("empirical_samples") == 6 && mixedTiming.L("empirical_velocity_samples") == 3
            && mixedTiming.D("observed_cycle_minutes_proxy") == good.D("observed_cycle_minutes_proxy")
            && mixedTiming.D("observed_median_profit_per_hour_gp") == good.D("observed_median_profit_per_hour_gp")
            && mixedTiming.D("empirical_ranking_factor") == good.D("empirical_ranking_factor"),
            "manual reconciliation timestamps never dilute measured cycle duration or velocity ranking");
        var allExternal = Adapt(externallyCollected);
        Check(allExternal.D("expected_fill_fraction") == 1 && allExternal.D("observed_profitable_fraction") == 1
            && allExternal.L("empirical_velocity_samples") == 0 && allExternal["observed_cycle_minutes_proxy"] is null
            && allExternal["observed_median_profit_per_hour_gp"] is null && allExternal.D("empirical_ranking_factor") == 1,
            "externally confirmed profits and fills remain usable without inventing a velocity sample");

        var neutral = Candidate(1); var penalized = Candidate(2); penalized["empirical_ranking_factor"] = .8;
        var neutralFunding = neutral.Copy(); foreach (var pair in Strategy.FundedEconomics(neutral, config, portfolio, 1)) neutralFunding[pair.Key] = pair.Value?.DeepClone();
        var penalizedFunding = penalized.Copy(); foreach (var pair in Strategy.FundedEconomics(penalized, config, portfolio, 1)) penalizedFunding[pair.Key] = pair.Value?.DeepClone();
        var ratio = DecisionPolicy.Score(penalizedFunding, "NORMAL", config) / DecisionPolicy.Score(neutralFunding, "NORMAL", config);
        Check(Math.Abs(ratio - .8) < .0001,
            "decision policy applies the empirical ranking factor once across funded scoring");
        var candidates = Strategy.RankFundedCandidates([neutral, penalized], config, portfolio)
            .OrderByDescending(c => DecisionPolicy.Score(c, "NORMAL", config)).ToArray();
        Check(candidates[0].L("id") == 1,
            "equal market opportunities rank healthier observed performance first");
        return J.O(("passed", checks.Count), ("checks", checks), ("live_game_inputs", false));
    }
}

