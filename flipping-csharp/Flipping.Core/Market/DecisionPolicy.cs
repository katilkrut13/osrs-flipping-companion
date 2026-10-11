using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

/// <summary>
/// Converts the already screened market facts into a portfolio-aware priority.
/// This is deliberately a ranking policy, not a fill or profit guarantee.
/// </summary>
public static class DecisionPolicy
{
    private static double Clamp01(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    /// <summary>
    /// Scores a funded candidate after tax, stress pricing, test cost and the current
    /// position size have already been calculated by <see cref="Strategy.FundedEconomics"/>.
    /// The score keeps absolute GP/hour as the main signal, then applies bounded
    /// quality multipliers so a spectacular but thin or stale spread cannot dominate.
    /// </summary>
    public static JsonObject Explain(JsonObject candidate, string session, JsonObject config)
    {
        var baseScore = Math.Max(0, candidate.D("funded_selection_score", candidate.D("selection_score")));
        var cost = Math.Max(0, candidate.L("funded_cost_gp"));
        var hourly = Math.Max(0, candidate.D("funded_profit_per_hour_proxy_gp"));
        var balance = Clamp01(candidate.D("two_sided_volume_balance", 1));
        var chart = candidate.Obj("chart_context");
        var coverage = Clamp01(chart.D("coverage_fraction", 1));
        var volatility = Math.Max(0, chart.D("median_abs_return_fraction"));
        var volatilityLimit = Math.Max(config.D("max_median_return_fraction", .015), .0001);
        var stability = Clamp01(1 - volatility / volatilityLimit);
        if (chart.S("status") != "READY") stability = config.B("require_chart_context", true) ? 0 : .5;

        var spreadPersistence = Clamp01(chart.D("positive_spread_fraction", 1));
        var stressMargin = Math.Max(0, candidate.L("stress_net_margin_gp", candidate.L("estimated_net_margin_gp")));
        var normalMargin = Math.Max(1, candidate.L("estimated_net_margin_gp"));
        var marginQuality = Clamp01(stressMargin / (double)normalMargin);
        var testCost = Math.Max(0, candidate.L("verified_test_loss_gp", candidate.L("estimated_test_loss_gp")));
        var costRecovery = testCost == 0 ? 1 : Clamp01(candidate.D("funded_stress_profit_gp") / (testCost * 2d));
        var priceAge = Math.Max(0, candidate.L("price_age_seconds"));
        var maxAge = Math.Max(1, config.L("max_price_age_seconds", 300));
        var freshness = Clamp01(1 - priceAge / (double)maxAge);

        var thesis = candidate.Obj("trade_thesis");
        var zone = thesis.S("entry_price_zone", "UNVERIFIED");
        var zoneFactor = zone switch
        {
            "BELOW_RECENT_RANGE" => 1.08,
            "WITHIN_RECENT_RANGE" => 1.00,
            "ABOVE_RECENT_RANGE" => .82,
            _ => .90
        };

        // A small, bounded capital-velocity bonus stops a large absolute trade from
        // always beating a faster opportunity that uses a fraction of the bankroll.
        var efficiency = cost > 0 ? hourly / cost : 0;
        var targetEfficiency = Math.Max(config.D("capital_efficiency_target_fraction", .01), .0001);
        var velocityFactor = .85 + .30 * Clamp01(efficiency / targetEfficiency);

        // The funded score already contains the bounded empirical multiplier.
        // Applying it here again would double both rewards and penalties.
        var quality = .20 + .20 * balance + .16 * coverage + .18 * stability + .14 * spreadPersistence + .08 * marginQuality + .04 * costRecovery;
        quality = Math.Clamp(quality, .10, 1.20);
        var confidence = Clamp01(.24 * balance + .18 * coverage + .22 * stability + .16 * spreadPersistence + .12 * marginQuality + .08 * costRecovery);
        confidence *= .85 + .15 * freshness;
        var sessionFactor = session switch
        {
            "PEAK" when candidate.B("consumable") => 1.04,
            "OFF_PEAK" when !candidate.B("consumable") => 1.03,
            _ => 1.00
        };
        var score = baseScore * quality * velocityFactor * zoneFactor * sessionFactor;
        var grade = confidence >= .80 ? "A" : confidence >= .65 ? "B" : confidence >= .50 ? "C" : "D";
        return J.O(("decision_score", Math.Round(score, 2)), ("decision_confidence", Math.Round(confidence, 4)),
            ("decision_grade", grade), ("decision_quality", Math.Round(quality, 4)),
            ("capital_efficiency_per_hour", Math.Round(efficiency, 6)), ("capital_velocity_factor", Math.Round(velocityFactor, 4)),
            ("entry_zone_factor", zoneFactor), ("session_factor", sessionFactor), ("decision_zone", zone),
            ("decision_factors", J.O(("two_sided_balance", Math.Round(balance, 4)), ("history_coverage", Math.Round(coverage, 4)),
                ("stability", Math.Round(stability, 4)), ("spread_persistence", Math.Round(spreadPersistence, 4)),
                ("stress_margin_quality", Math.Round(marginQuality, 4)), ("test_cost_recovery", Math.Round(costRecovery, 4)),
                ("quote_freshness", Math.Round(freshness, 4)))),
            ("decision_basis", "Risk-adjusted GP/hour with bounded liquidity, stability, price-zone, test-cost and empirical-outcome factors; heuristic only"));
    }

    public static double Score(JsonObject candidate, string session, JsonObject config)
        => Explain(candidate, session, config).D("decision_score");
}
