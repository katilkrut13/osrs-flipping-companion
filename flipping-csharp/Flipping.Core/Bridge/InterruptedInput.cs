using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public sealed class UserIntervention() : OperationCanceledException("Physical user activity; automatic input paused");

/// <summary>A terminal input receipt releases transport only; it never proves a GE offer or collection.</summary>
public static class InterruptedInput
{
    public static bool CanRelease(string pendingId, JsonObject? request, JsonObject? receipt, JsonObject state, double now)
    {
        if (string.IsNullOrEmpty(pendingId) || request is null || receipt is null
            || request.S("id") != pendingId || receipt.S("id") != pendingId
            || receipt.S("status") is not ("dispatched" or "rejected")
            || !state.B("logged_in") || state.B("input_busy")
            || request.S("account_key") == "" || request.S("session") == ""
            || !ProtocolHelpers.Same(request, state, "account_key", "session")
            || request["sequence"] is null || state["sequence"] is null
            || state.L("sequence") <= request.L("sequence")) return false;
        var observed = state.D("observed_at");
        return double.IsFinite(now) && double.IsFinite(observed) && observed > 0
            && now - observed is >= -1 and <= 5 && observed >= request.D("created_at");
    }

    public static JsonObject RunChecks()
    {
        var checks = new JsonArray();
        void Check(bool value, string name) { if (!value) throw new InvalidDataException(name); checks.Add(name); }
        var request = J.O(("id", "p"), ("session", "s"), ("account_key", "a"), ("sequence", 10), ("created_at", 100));
        var receipt = J.O(("id", "p"), ("status", "dispatched"));
        var state = J.O(("session", "s"), ("account_key", "a"), ("sequence", 12), ("observed_at", 101), ("logged_in", true), ("input_busy", false));
        Check(CanRelease("p", request, receipt, state, 102), "terminal dispatch settles transport");
        receipt["status"] = "rejected"; receipt["reason"] = "InterruptedException";
        Check(CanRelease("p", request, receipt, state, 102), "partial interruption receipt settles transport without outcome claim");
        foreach (var field in new[] { "session", "account_key" })
        { var wrong = state.Copy(); wrong[field] = "other"; Check(!CanRelease("p", request, receipt, wrong, 102), "changed " + field + " blocks release"); }
        foreach (var status in new[] { "pending", "", "unknown" })
        { var wrong = receipt.Copy(); wrong["status"] = status; Check(!CanRelease("p", request, wrong, state, 102), "nonterminal " + status + " blocks release"); }
        var busy = state.Copy(); busy["input_busy"] = true; Check(!CanRelease("p", request, receipt, busy, 102), "busy input blocks release");
        var sameFrame = state.Copy(); sameFrame["sequence"] = 10; Check(!CanRelease("p", request, receipt, sameFrame, 102), "old sequence blocks release");
        Check(!CanRelease("p", request, receipt, state, 110), "stale state blocks release");
        Check(!CanRelease("different", request, receipt, state, 102), "receipt from another command blocks release");
        Check(!CanRelease("p", request, null, state, 102), "missing receipt remains uncertain");
        Check(!CanRelease("p", null, receipt, state, 102), "missing original context remains uncertain");
        return J.O(("passed", checks.Count), ("checks", checks));
    }
}
