using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public static class RecoveryEvidence
{
    public static JsonObject? UnsubmittedStopEvidence(JsonObject p, JsonObject plan, JsonObject state, IEnumerable<JsonObject> events, IEnumerable<JsonObject> receipts)
    {
        var account = p["account_key"].S(); var id = (int)p["item_id"].L(); var created = p["created_at"].L();
        if (p["stage"].S() != "STOPPED" || p["recovery_stage"].S() != "BUY_SUBMITTING" || p["submission_cash_gp"] == null || p["slot"] == null
            || new[] { "buy_spent", "owned_quantity", "acquired_quantity", "sale_proceeds_gp" }.Any(k => p[k].L() != 0)
            || state["input_busy"].B() || account == "" || account != state["account_key"].S() || plan["account_key"].S() != account || plan["stage"].S() != "DONE"
            || plan["started_at"].L() < created || plan["started_at"].L() > created + 30 || plan["completed_at"].L() < plan["started_at"].L()
            || plan["completed_at"].L() > state["observed_at"].L() || Lifecycle.CountItem(state, id) != p["inventory_before"].L() || Lifecycle.HasOffer(state, id)) return null;
        var failures = events.Where(e => e["at"].L() == created && e["text"].S() == "Operation receipt/transition delayed; reconcile before any retry: UI target temporarily unavailable; no input sent").ToList();
        var entries = plan.Objects("entries").ToList(); var allReceipts = receipts.ToList();
        if (failures.Count != 1 || entries.Count == 0 || entries.Any(e => Lifecycle.Slot(e.Obj("offer")) == Lifecycle.Slot(p) || e.Obj("offer")["item_id"].L() == id)
            || entries[0]["cash_before"].L(-1) != p["submission_cash_gp"].L()) return null;
        if (allReceipts.Any(r => r["account_key"].S() == account && r.Obj("offer")["item_id"].L() == id && r["observed_at"].L(r["collect_started_at"].L()) >= created)) return null;
        var ids = new JsonArray(); var cash = p["submission_cash_gp"].L();
        foreach (var entry in entries)
        {
            var o = entry.Obj("offer"); if (entry["stage"].S() != "DONE" || !entry["applied"].B() || entry["cash_before"].L(-1) != cash) return null;
            var buying = o["state"].S() is "BOUGHT" or "CANCELLED_BUY";
            var matches = allReceipts.Where(r => Lifecycle.ValidCollectionReceipt(r, account, Lifecycle.Slot(o), (int)o["item_id"].L(), o["quantity"].L(), o["price"].L(), buying, plan["started_at"].L())
                && r["observed_at"].L() <= plan["completed_at"].L() && Lifecycle.SameFields(r.Obj("offer"), o, "state", "filled", "spent")
                && r["cash_before_gp"].L() == entry["cash_before"].L() && r["item_before"].L() == entry["inventory_before"].L()
                && r["cash_delta"].L() == entry["cash_delta"].L() && r["item_delta"].L() == entry["item_delta"].L()).ToList();
            if (matches.Count != 1) return null; ids.Add(matches[0]["receipt_id"]?.DeepClone()); cash += entry["cash_delta"].L();
        }
        return J.O(("status", "NOT_SUBMITTED"), ("account_key", account), ("trade_id", p["trade_id"]), ("item_id", id), ("quantity", p["quantity"]), ("price", p["buy_price"]),
            ("slot", p["slot"]), ("source", "PRECONFIRM_FAILURE_AND_COMPLETED_STOP_CHECKPOINT"), ("failure_at", failures[0]["at"]),
            ("stop_started_at", plan["started_at"]), ("checkpoint_cash_gp", p["submission_cash_gp"]), ("stop_receipt_ids", ids), ("reviewed_at", state["observed_at"]));
    }
}
