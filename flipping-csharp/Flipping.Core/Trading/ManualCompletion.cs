using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public static class ManualCompletion
{
    // Human confirmation closes ownership responsibility, never supplies a fictional sale price/profit.
    public static bool Apply(JsonObject position, JsonObject confirmation, JsonObject state)
    {
        if (!confirmation.B("confirmed") || confirmation.S("confirmation_id") == ""
            || confirmation.S("trade_id") != position.S("trade_id") || confirmation.I("item_id") != position.I("item_id")
            || confirmation.S("account_key") == "" || confirmation.S("account_key") != position.S("account_key")
            || state.S("account_key") != position.S("account_key")) return false;
        if (position.Obj("external_completion").S("confirmation_id") == confirmation.S("confirmation_id")) return false;
        if (position.S("stage") != "STOPPED" || position.S("recovery_stage") != "WAIT_BUY"
            || position.L("owned_quantity") != 0 || !state.B("logged_in") || !state.B("ge_open") || state.B("input_busy")
            || J.Now - state.L("observed_at") is < -1 or > 5
            || Lifecycle.HasOffer(state, position.L("item_id"))
            || Lifecycle.CountItem(state, position.I("item_id")) != position.L("inventory_before")) return false;
        position["external_completion"] = confirmation.Copy(); position.Obj("external_completion")["applied_at"] = state.L("observed_at");
        position["stage"] = "VOID"; position["recovery_stage"] = ""; position["completed_at"] = state.L("observed_at");
        position["realized_profit_gp"] = null; position["profit_unknown"] = true;
        position["last_risk_reason"] = "USER_COMPLETED_EXTERNALLY_PROFIT_UNKNOWN"; position.Remove("manual_review");
        return true;
    }

    // An identified manual sale of already owned stock can remain isolated while other items trade.
    public static bool IsIsolatedOwnedSale(JsonObject position, JsonObject state)
    {
        if (position.S("stage") != "STOPPED" || position.S("recovery_stage") is not ("HELD" or "PAUSED_HELD")
            || position.Obj("manual_review").S("reason") != "UNEXPECTED_ITEM_OFFER"
            || position.S("account_key") == "" || position.S("account_key") != state.S("account_key")
            || position.L("owned_quantity") <= 0 || position.S("storage_location", "INVENTORY") != "INVENTORY"
            || Lifecycle.CountItem(state, position.I("item_id")) != position.L("inventory_before")) return false;
        var offers = state.Objects("offers").Where(o => o.I("item_id") == position.I("item_id") && o.S("state") != "EMPTY").ToArray();
        return offers.Length == 1 && offers[0].S("state") is "SELLING" or "SOLD" or "CANCELLED_SELL"
            && offers[0].L("quantity") == position.L("owned_quantity");
    }

    // When a sell disappeared and the stock is no longer present, its proceeds
    // cannot be reconstructed safely.  After a short observation window the
    // record may be archived with unknown profit so unrelated trades can run.
    public static bool CanArchiveMissingSale(JsonObject value, JsonObject state, long graceSeconds = 30)
    {
        var review = value.Obj("manual_review");
        return value.S("stage") == "STOPPED" && value.S("recovery_stage") == "WAIT_SELL"
            && (review.S("reason").EndsWith("OFFER_MISSING") || review.S("reason").EndsWith("OFFER_CHANGED"))
            && review.L("observed_at") > 0 && state.L("observed_at") - review.L("observed_at") >= graceSeconds
            && value.S("account_key") != "" && value.S("account_key") == state.S("account_key")
            && value.S("storage_location", "INVENTORY") == "INVENTORY"
            && !Lifecycle.HasOffer(state, value.L("item_id"))
            && Lifecycle.CountItem(state, value.I("item_id")) == value.L("inventory_before");
    }

    public static JsonObject RunChecks()
    {
        var checks = new JsonArray();
        void Check(bool value, string name) { if (!value) throw new InvalidDataException(name); checks.Add(name); }
        var p = OrderManager.NewPosition(1987, "Grapes", 2668, 74, 78, J.Now - 10, "a");
        p["stage"] = "STOPPED"; p["recovery_stage"] = "WAIT_BUY";
        var c = J.O(("confirmed", true), ("confirmation_id", "human-confirmed"), ("trade_id", p["trade_id"]), ("item_id", 1987), ("account_key", "a"));
        var s = J.O(("observed_at", J.Now), ("account_key", "a"), ("logged_in", true), ("ge_open", true), ("input_busy", false), ("offers", new JsonArray()), ("inventory", new JsonArray()));
        foreach (var field in new[] { "confirmed", "confirmation_id", "trade_id", "item_id", "account_key" })
        { var wrong = c.Copy(); wrong[field] = field == "confirmed" ? J.Node(false) : field == "item_id" ? J.Node(2) : J.Node(""); Check(!Apply(p.Copy(), wrong, s), "reject invalid completion " + field); }
        var busy = s.Copy(); busy["input_busy"] = true; Check(!Apply(p.Copy(), c, busy), "busy completion waits");
        var stale = s.Copy(); stale["observed_at"] = J.Now - 20; Check(!Apply(p.Copy(), c, stale), "stale completion waits");
        var stock = s.Copy(); stock["inventory"] = J.A(J.O(("id", 1987), ("quantity", 1))); Check(!Apply(p.Copy(), c, stock), "remaining stock blocks archival");
        var open = s.Copy(); open["offers"] = J.A(J.O(("item_id", 1987), ("state", "BUYING"))); Check(!Apply(p.Copy(), c, open), "remaining offer blocks archival");
        Check(Apply(p, c, s) && p.S("stage") == "VOID" && p.B("profit_unknown") && p["realized_profit_gp"] is null, "confirmed external completion archives without invented profit");
        Check(!Apply(p, c, s), "external completion is idempotent");
        var missingSale = OrderManager.NewPosition(1987, "Grapes", 10, 74, 78, J.Now - 100, "a");
        missingSale["stage"] = "STOPPED"; missingSale["recovery_stage"] = "WAIT_SELL"; missingSale["owned_quantity"] = 10;
        missingSale["manual_review"] = J.O(("reason", "OFFER_MISSING"), ("observed_at", J.Now - 40));
        Check(CanArchiveMissingSale(missingSale, s), "old missing sale can be isolated with unknown profit");
        var held = OrderManager.NewPosition(573, "Air orb", 198, 1219, 1263, J.Now - 10, "a");
        held["stage"] = "STOPPED"; held["recovery_stage"] = "HELD"; held["owned_quantity"] = 198;
        held["manual_review"] = J.O(("reason", "UNEXPECTED_ITEM_OFFER"));
        var sale = s.Copy(); sale["offers"] = J.A(J.O(("item_id", 573), ("quantity", 198), ("state", "SELLING")));
        Check(IsIsolatedOwnedSale(held, sale), "manual owned sale does not freeze unrelated items");
        var manager = new OrderManager(new SimExchange(new JsonObject()), new JsonObject()); manager.Positions.Add(held);
        Check(manager.Reconcile(sale, allowIsolatedOwnedSales: true) && held.S("stage") == "STOPPED" && held["external_owned_sale"] is JsonObject,
            "known manual sale is observed without modifying its offer");
        var uncertainBuy = OrderManager.NewPosition(1987, "Grapes", 2, 74, 78, J.Now, "a");
        uncertainBuy["stage"] = "STOPPED"; uncertainBuy["recovery_stage"] = "WAIT_BUY"; manager.Positions.Add(uncertainBuy);
        Check(!manager.Reconcile(sale, allowIsolatedOwnedSales: true), "isolated sale never bypasses another unknown purchase");
        sale.Objects("offers").First()["quantity"] = 197; Check(!IsIsolatedOwnedSale(held, sale), "changed ownership quantity still needs review");
        var report = ProfitReport.Build("a", J.Now, [p]);
        Check(report.Objects("rows").Single().S("kind") == "EXTERNAL_COMPLETION" && report.Obj("totals").L("net_profit_gp") == 0,
            "unknown external result shown separately from profit totals");
        return J.O(("passed", checks.Count), ("checks", checks));
    }
}
