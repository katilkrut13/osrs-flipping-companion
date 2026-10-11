using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public static class ManualOrderRegressionChecks
{
    public static JsonObject Run(JsonObject originalConfig)
    {
        var config = originalConfig.Copy(); config["target_fill_slots"] = false;
        var checks = new JsonArray();
        void Check(bool value, string name) { if (!value) throw new InvalidDataException("Manual orders: " + name); checks.Add(name); }
        (SimExchange Exchange, OrderManager Manager, JsonObject P) Buying()
        {
            var exchange = new SimExchange(config); var manager = new OrderManager(exchange, config);
            var state = exchange.Observe();
            var candidate = J.O(("id", 2349), ("name", "Bronze bar"), ("indicative_buy_gp", 100), ("indicative_sell_gp", 120), ("buy_limit", 10000));
            var portfolio = new Portfolio { Verified = true, BuyLimitsVerified = true, ObservedAt = exchange.Now, FreeCashGp = exchange.Cash, SlotCapacity = 8, Members = true };
            return (exchange, manager, manager.Add(candidate, 10, portfolio, state, true));
        }
        var zero = Buying(); zero.Exchange.Cancel(zero.P.I("slot")); zero.Exchange.Collect(zero.P.I("slot"));
        zero.Manager.ReviewManualChanges(zero.Exchange.Observe());
        Check(zero.Manager.Reconcile(zero.Exchange.Observe()) && zero.P.S("stage") == "BUY_COLLECTING", "manual zero-fill cancel and collect reconciles from exact receipt");
        zero.Manager.Step(zero.Exchange.Observe());
        Check(zero.P.S("stage") == "DONE" && zero.P.L("realized_profit_gp") == 0 && zero.Exchange.Offers.Count == 0, "zero-fill manual cancellation never submits a sale");
        var partial = Buying(); partial.Exchange.Fill(partial.P.I("slot"), 4, 99); partial.Exchange.Cancel(partial.P.I("slot")); partial.Exchange.Collect(partial.P.I("slot"));
        partial.Manager.ReviewManualChanges(partial.Exchange.Observe());
        Check(partial.Manager.Reconcile(partial.Exchange.Observe()) && partial.P.L("owned_quantity") == 4 && partial.P.L("buy_spent") == 396, "partial manual purchase retains actual filled quantity and cost");
        partial.Manager.Step(partial.Exchange.Observe());
        Check(partial.Exchange.Offers.Single().Value.L("quantity") == 4, "resumed sale uses actual four items rather than original ten");
        partial.Exchange.Fill(partial.P.I("slot")); partial.Exchange.Collect(partial.P.I("slot"));
        partial.Manager.ReviewManualChanges(partial.Exchange.Observe());
        Check(partial.Manager.Reconcile(partial.Exchange.Observe()) && partial.P.S("stage") == "DONE" && partial.P.L("realized_profit_gp") == 76, "manual sale collection closes with actual net proceeds");
        Check(partial.Manager.Reconcile(partial.Exchange.Observe()) && partial.P.L("sale_proceeds_gp") == 472, "repeated reconciliation cannot duplicate manual sale proceeds");
        var missing = Buying(); missing.Exchange.Offers.Clear();
        missing.Manager.ReviewManualChanges(missing.Exchange.Observe());
        Check(missing.P.S("stage") == "STOPPED" && !missing.Manager.Reconcile(missing.Exchange.Observe()), "missing offer without evidence becomes explicit review rather than an infinite wait");
        Check(!missing.Manager.Step(missing.Exchange.Observe()) && missing.P["realized_profit_gp"] is null && missing.P.L("buy_spent") == 0, "unknown disappearance never invents a fill or profit");
        var fullStock = Buying(); fullStock.Exchange.Offers.Clear(); fullStock.Exchange.Inventory[2349] = 10;
        fullStock.P["last_filled_quantity"] = 4; fullStock.P["buy_precollected_quantity"] = 4;
        fullStock.Manager.ReviewManualChanges(fullStock.Exchange.Observe());
        Check(fullStock.Manager.Reconcile(fullStock.Exchange.Observe()) && fullStock.P.S("stage") == "BUY_COLLECTING"
            && fullStock.P.L("owned_quantity") == 10 && fullStock.P.L("buy_spent") == 1000
            && fullStock.P.Obj("automatic_recovery_evidence").S("status") == "FULL_BUY_EXACT_STOCK_CONSERVATIVE_COST",
            "full exact stock after observed partial buy resumes with conservative maximum cost");
        var changed = Buying(); changed.Exchange.Offers[changed.P.I("slot")]["price"] = 101;
        changed.Manager.ReviewManualChanges(changed.Exchange.Observe());
        Check(changed.P.Obj("manual_review").S("reason") == "OFFER_CHANGED" && !changed.Manager.Step(changed.Exchange.Observe()) && changed.Exchange.Offers.Count == 1, "manually changed offer is quarantined without cancel or overwrite");
        changed.Exchange.Offers[changed.P.I("slot")]["price"] = 100;
        changed.P["buy_limit_review_recorded"] = true;
        Check(changed.Manager.Reconcile(changed.Exchange.Observe()) && changed.P.S("stage") == "WAIT_BUY" && changed.P["manual_review"] is null && changed.P["buy_limit_review_recorded"] is null, "exact original offer restoration clears review and permits fresh future limit invalidation");
        var displacedSale = Buying(); displacedSale.Exchange.Fill(displacedSale.P.I("slot")); displacedSale.Exchange.Collect(displacedSale.P.I("slot"));
        displacedSale.Manager.Reconcile(displacedSale.Exchange.Observe()); displacedSale.Manager.Step(displacedSale.Exchange.Observe());
        var oldSlot = displacedSale.P.I("slot"); displacedSale.Exchange.Offers.Remove(oldSlot);
        displacedSale.Exchange.Inventory[2349] = 10;
        displacedSale.Exchange.Inventory[2] = 1;
        displacedSale.Exchange.Submit("SELL", 2, "Cannonball", 1, 200);
        displacedSale.Manager.ReviewManualChanges(displacedSale.Exchange.Observe());
        Check(displacedSale.Manager.Reconcile(displacedSale.Exchange.Observe()) && displacedSale.P.S("stage") == "HELD"
            && displacedSale.P.L("owned_quantity") == 10 && displacedSale.P["manual_review"] is null,
            "displaced sale with exact owned stock returns to held state for a new free slot");
        var activePartial = Buying(); activePartial.Exchange.Fill(activePartial.P.I("slot"), 4); activePartial.Exchange.Inventory[2349] = 4;
        activePartial.Manager.ReviewManualChanges(activePartial.Exchange.Observe());
        Check(activePartial.P.S("stage") == "WAIT_BUY" && activePartial.Manager.Reconcile(activePartial.Exchange.Observe())
            && activePartial.P.L("buy_precollected_quantity") == 4,
            "partial collection from a still-active buy is tracked without quarantining the trade");
        var held = Buying(); held.Exchange.Fill(held.P.I("slot")); held.Exchange.Collect(held.P.I("slot")); held.Manager.Reconcile(held.Exchange.Observe()); held.P["stage"] = "HELD";
        held.Exchange.Inventory[2349] = 0; held.Manager.ReviewManualChanges(held.Exchange.Observe());
        Check(held.P.S("stage") == "STOPPED" && held.P.L("owned_quantity") == 10 && held.P["realized_profit_gp"] is null, "manual stock movement preserves ownership and unknown result");
        held.Exchange.Inventory[2349] = 10;
        Check(held.Manager.Reconcile(held.Exchange.Observe()) && held.P.S("stage") == "HELD", "restored held stock resumes its previous state");
        held.Exchange.Submit("SELL", 2349, "Bronze bar", 10, 130); held.Manager.ReviewManualChanges(held.Exchange.Observe());
        Check(held.P.S("stage") == "STOPPED" && held.P.Obj("manual_review").S("reason") == "UNEXPECTED_ITEM_OFFER" && !held.Manager.Reconcile(held.Exchange.Observe()), "manual sale of held stock is not adopted or cancelled without attribution");
        Check(!EngineRules.TrackedOfferState(held.Manager, new MarginRunner(held.Exchange, config), held.Exchange.Observe()).Objects("offers").Any(o => o.S("state") != "EMPTY"), "quarantined stock offer stays outside stop cleanup");
        var collecting = Buying(); collecting.Exchange.Offers.Clear(); collecting.P["stage"] = "SELL_COLLECTING";
        collecting.P["owned_quantity"] = 10; collecting.P["acquired_quantity"] = 10; collecting.P["buy_spent"] = 1000;
        collecting.P["collection_remaining_quantity"] = 0; collecting.P["cash_before_collect"] = collecting.Exchange.Cash;
        collecting.P["collection_expected_gp"] = 1180; collecting.Exchange.Inventory[2349] = 1;
        collecting.Manager.ReviewManualChanges(collecting.Exchange.Observe());
        Check(collecting.P.S("stage") == "STOPPED" && collecting.P.Obj("manual_review").S("reason") == "COLLECTION_CHANGED"
            && !collecting.Manager.Step(collecting.Exchange.Observe()), "collection inconsistency quarantines without a global exception or invented receipt");
        var independent = Buying(); independent.Exchange.Offers.Clear(); independent.Manager.ReviewManualChanges(independent.Exchange.Observe());
        independent.Exchange.Inventory[2] = 5;
        var healthy = OrderManager.NewPosition(2, "Cannonball", 5, 100, 120, independent.Exchange.Now, independent.Exchange.Account!);
        healthy["buy_spent"] = 500; healthy["acquired_quantity"] = 5; healthy["owned_quantity"] = 5;
        healthy["stage"] = "WAIT_SELL"; healthy["slot"] = independent.Exchange.Submit("SELL", 2, "Cannonball", 5, 120); independent.Manager.Positions.Add(healthy);
        independent.Exchange.Fill(healthy.I("slot")); independent.Manager.Step(independent.Exchange.Observe()); independent.Manager.Step(independent.Exchange.Observe());
        Check(independent.P.S("stage") == "STOPPED" && healthy.S("stage") == "DONE" && healthy.L("realized_profit_gp") == 90, "one uncertain trade does not block an independent healthy sale");
        var manualSlot = independent.Exchange.Submit("BUY", 453, "Coal", 3, 100);
        var runner = new MarginRunner(independent.Exchange, config);
        Check(!EngineRules.TrackedOfferState(independent.Manager, runner, independent.Exchange.Observe()).Objects("offers").Any(o => o.S("state") != "EMPTY"), "stop cleanup excludes unrelated manually opened offers");
        Check(independent.Exchange.Offers.ContainsKey(manualSlot), "filtering stop plan does not mutate the real manual offer");
        var tracked = Buying(); var trackedRunner = new MarginRunner(tracked.Exchange, config);
        Check(EngineRules.TrackedOfferState(tracked.Manager, trackedRunner, tracked.Exchange.Observe()).Objects("offers").Count(o => o.S("state") != "EMPTY") == 1, "stop cleanup retains exact known bot offers");
        var ledgerFolder = Path.Combine(Path.GetTempPath(), "ManualLedger-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(ledgerFolder);
        using var ledger = new Ledger(Path.Combine(ledgerFolder, "trades.sqlite3"));
        ledger.Ingest(tracked.Exchange.Observe()); ledger.ConfirmNoUntrackedBuys(tracked.Exchange.Account!, tracked.Exchange.Now);
        Check(ledger.Ingest(tracked.Exchange.Observe()).BuyLimitsVerified, "manual observation keeps a confirmed continuous account baseline");
        ledger.InvalidateBuyHistory(tracked.Exchange.Account!, tracked.Exchange.Now, J.O(("reason", "OFFER_MISSING")));
        Check(!ledger.Ingest(tracked.Exchange.Observe()).BuyLimitsVerified, "unobserved manual buy outcome invalidates rather than fabricates remaining limits");
        var bankState = tracked.Exchange.Observe(); bankState["bank_open"] = true; bankState["observed_at"] = 100100;
        Check(EngineRules.UnexpectedManualBank(bankState, null, 100000), "unexpected manual bank opening requests input pause");
        Check(!EngineRules.UnexpectedManualBank(bankState, J.O(("stage", "VERIFY")), 100000), "active automated bank transfer is not mistaken for manual control");
        Check(!EngineRules.UnexpectedManualBank(bankState, J.O(("stage", "DONE"), ("completed_at", 100099)), 100000)
            && !EngineRules.UnexpectedManualBank(bankState, null, 100099), "recent automated transfer or explicit start can finish bank navigation");
        return J.O(("passed", checks.Count), ("checks", checks), ("live_game_inputs", false));
    }
}
