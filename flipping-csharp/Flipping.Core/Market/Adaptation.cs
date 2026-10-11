using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public static class Adaptation
{
    private static long Acquired(JsonObject record) => Math.Max(record.L("acquired_quantity"), record.L("owned_quantity"));
    private static bool ValidOutcome(JsonObject record)
        => record.S("stage") == "DONE" && record.L("item_id") > 0 && Acquired(record) > 0
            && record["started_at"].IsInteger() && record["completed_at"].IsInteger()
            && record.L("started_at") >= 0 && record.L("completed_at") > record.L("started_at")
            && record["realized_profit_gp"].IsInteger() && !record.B("profit_unverified")
            && record.S("accounting_status") != "UNVERIFIED";

    public static JsonObject AdaptReport(JsonObject report, IEnumerable<JsonObject> outcomes, JsonObject config)
    {
        var result = report.Copy();
        var groups = outcomes.Where(ValidOutcome)
            .GroupBy(p => p.S("trade_id") is { Length: > 0 } id ? p.S("account_key") + ":" + id : p.ToJsonString())
            .Select(g => g.Last()).GroupBy(p => p.L("item_id")).ToDictionary(g => g.Key, g => g.ToList());
        var candidates = new List<JsonObject>();
        foreach (var original in report.Objects("candidates"))
        {
            var c = original.Copy(); var records = groups.GetValueOrDefault(c.L("id")) ?? [];
            var basis = c.D("adaptation_base_selection_score", c.D("selection_score", c.D("profit_per_hour_proxy_gp")));
            var fillPrior = Math.Clamp(c.D("market_expected_fill_fraction", c.D("expected_fill_fraction", 1)), .1, 1);
            c["adaptation_base_selection_score"] = basis; c["market_expected_fill_fraction"] = fillPrior;
            c["selection_score"] = basis; c["expected_fill_fraction"] = fillPrior; c["empirical_ranking_factor"] = 1;
            c["empirical_samples"] = records.Count; c["adaptation"] = "INSUFFICIENT_SAMPLES";
            c["empirical_velocity_samples"] = 0;
            c["observed_cycle_minutes_proxy"] = null; c["observed_median_profit_per_hour_gp"] = null;
            if (records.Count >= config.L("learning_min_samples", 5))
            {
                var recent = records.OrderBy(p => p.L("completed_at")).TakeLast(20).ToArray();
                var losses = recent.Count(p => p.L("realized_profit_gp") < 0);
                // Manual collection confirms quantities and accounting, but its
                // completion timestamp is the later reconciliation time. Keep
                // valid profit/fill observations without treating that interval
                // as a measured market fill or capital-velocity duration.
                var timed = records.Where(p => p["external_collection_evidence"] is null)
                    .OrderBy(p => p.L("completed_at")).TakeLast(20).ToArray();
                var sufficientTiming = timed.Length >= config.L("learning_min_samples", 5);
                var empirical = sufficientTiming ? Strategy.Median(timed.Select(p => p.D("realized_profit_gp") * 3600 / (p.L("completed_at") - p.L("started_at")))) : (double?)null;
                c["empirical_velocity_samples"] = timed.Length;
                c["externally_reconciled_samples"] = recent.Count(p => p["external_collection_evidence"] is not null);
                var fillFractions = recent.Select(p =>
                {
                    // Current positions persist quantity; older imports sometimes use
                    // planned_quantity and owned_quantity instead. Neither format
                    // should turn a documented acquisition into a zero fill.
                    var planned = Math.Max(1, p.L("planned_quantity", p.L("quantity", Acquired(p))));
                    var acquired = Math.Clamp(Acquired(p), 0, planned);
                    return acquired / (double)planned;
                }).ToArray();
                var durations = timed
                    .Select(p => (p.L("completed_at") - p.L("started_at")) / 60d).ToArray();
                var observedFill = Strategy.Median(fillFractions);
                var observedCycle = sufficientTiming ? Strategy.Median(durations) : 0;
                // Blend observed fills into the market prior. A full-fill history
                // must preserve the full-fill prior; an arbitrary .5 prior would
                // penalize the first successful trades. This is empirical
                // calibration, not a trained predictive model.
                var fillConfidence = Math.Clamp(recent.Length / Math.Max(1, config.D("learning_confidence_samples", 6)), 0, 1);
                var expectedFill = Math.Clamp(fillPrior + fillConfidence * (observedFill - fillPrior), .1, 1);
                c["empirical_sample_confidence"] = Math.Round(fillConfidence, 4);
                c["observed_fill_fraction"] = Math.Round(observedFill, 4);
                c["expected_fill_fraction"] = Math.Round(expectedFill, 4);
                c["observed_cycle_minutes_proxy"] = observedCycle > 0 ? Math.Round(observedCycle, 2) : null;
                c["observed_profitable_fraction"] = Math.Round(recent.Count(p => p.L("realized_profit_gp") > 0) / (double)recent.Length, 4);
                c["observed_median_profit_per_hour_gp"] = empirical;
                var performanceBasis = c.D("profit_per_hour_proxy_gp", basis);
                var velocityConfidence = Math.Clamp(timed.Length / Math.Max(1, config.D("learning_confidence_samples", 6)), 0, 1);
                var factor = empirical is { } hourly ? Math.Clamp(1 + velocityConfidence * .25 * (hourly / Math.Max(performanceBasis, 1) - 1), .75, 1.15) : 1;
                c["empirical_ranking_factor"] = factor; c["selection_score"] = basis * factor;
                // Keep the affordable offered quantity intact. FundedEconomics
                // applies expectedFill exactly once to expected proceeds; shrinking
                // quantity here as well would square the fill penalty.
                c["adaptation"] = "EMPIRICAL_RANKING";
                if (losses / (double)recent.Length >= config.D("learning_loss_fraction_block", .4))
                {
                    c["strategy_eligible"] = false; var reasons = c.Arr("strategy_reasons")?.DeepClone() as JsonArray ?? new(); reasons.Add("OBSERVED_POOR_PERFORMANCE"); c["strategy_reasons"] = reasons; c["adaptation"] = "BLOCKED_BY_OBSERVED_RESULTS";
                }
            }
            candidates.Add(c);
        }
        result["candidates"] = J.A(candidates.OrderByDescending(c => c.D("selection_score")));
        result["learning_note"] = "Account-bound realized outcomes; limited samples and changing markets do not predict future returns";
        return result;
    }
}
