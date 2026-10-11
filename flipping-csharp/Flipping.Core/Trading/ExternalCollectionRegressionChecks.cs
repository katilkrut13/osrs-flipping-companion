using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public static class ExternalCollectionRegressionChecks
{
    public static JsonObject Run()
    {
        var config = J.O(("tax_percent", 2), ("tax_cap_gp", 5_000_000));
        var checks = new JsonArray();
        void Check(bool condition, string name) { if (!condition) throw new InvalidDataException("External collection: " + name); checks.Add(name); }
        (OrderManager Manager, JsonObject P, JsonObject State, JsonObject Confirmation) Setup()
        {
            var exchange = new SimExchange(config); var manager = new OrderManager(exchange, config);
            var p = OrderManager.NewPosition(2349, "Bronze bar", 22, 59, 61, 100, exchange.Account!);
            p["stage"] = "STOPPED"; p["recovery_stage"] = "WAIT_SELL"; p["owned_quantity"] = 8;
            p["acquired_quantity"] = 22; p["buy_spent"] = 1298; p["sale_proceeds_gp"] = 868;
            p["submission_cash_gp"] = 1907121; manager.Positions.Add(p);
            var state = exchange.Observe(); state["cash_inventory_gp"] = 1907601;
            var plan = ExternalCollection.Plan(p, state, config) ?? throw new InvalidDataException("Fixture plan missing");
            plan["confirmed"] = true; plan["confirmation_id"] = "explicit-operator-confirmation";
            return (manager, p, state, plan);
        }
        var (manager, p, state, confirmation) = Setup();
        Check(p.S("stage") == "STOPPED" && p.L("sale_proceeds_gp") == 868, "exact balance by itself never closes sale");
        Check(ExternalCollection.Apply(manager, confirmation, state, config), "explicit confirmed sale applied");
        Check(p.S("stage") == "DONE" && p.L("realized_profit_gp") == 50 && p.L("sale_proceeds_gp") == 1348 && p.L("owned_quantity") == 0, "partial sale plus manual net collection realizes exact fifty GP");
        Check(!ExternalCollection.Apply(manager, confirmation, state, config) && p.L("sale_proceeds_gp") == 1348, "repeated confirmation never duplicates proceeds");
        Check(p["collection_receipt"] is null && p.Obj("external_collection_evidence").B("confirmed"), "operator audit retained without fabricated bridge receipt");
        var otherAccountState = state.Copy(); otherAccountState["account_key"] = "different-account";
        var replayRejected = false;
        try { ExternalCollection.Apply(manager, confirmation, otherAccountState, config); }
        catch (InvalidOperationException) { replayRejected = true; }
        Check(replayRejected && p.L("sale_proceeds_gp") == 1348, "applied confirmation cannot be replayed against another account");
        foreach (var kind in new[] { "unconfirmed", "wrong_account", "wrong_session", "changed_cash", "item_present", "offer_present", "stale_confirmation", "changed_quantity", "changed_proceeds" })
        {
            var f = Setup();
            switch (kind)
            {
                case "unconfirmed": f.Confirmation["confirmed"] = false; break;
                case "wrong_account": f.State["account_key"] = "different"; break;
                case "wrong_session": f.State["session"] = "different"; break;
                case "changed_cash": f.State["cash_inventory_gp"] = f.State.L("cash_inventory_gp") + 1; break;
                case "item_present": f.State["inventory"] = J.A(J.O(("id", 2349), ("quantity", 8))); break;
                case "offer_present": f.State["offers"] = J.A(J.O(("item_id", 2349), ("state", "SELLING"))); break;
                case "stale_confirmation": f.State["observed_at"] = f.State.L("observed_at") + 1801; break;
                case "changed_quantity": f.Confirmation["sold_quantity"] = 9; break;
                case "changed_proceeds": f.Confirmation["net_proceeds_gp"] = 481; break;
            }
            var rejected = false;
            try { ExternalCollection.Apply(f.Manager, f.Confirmation, f.State, config); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && f.P.S("stage") == "STOPPED" && f.P.L("sale_proceeds_gp") == 868, kind + " preserves journal");
        }
        return J.O(("passed", checks.Count), ("checks", checks));
    }
}
