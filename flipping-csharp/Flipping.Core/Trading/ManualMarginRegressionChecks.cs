using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public static class ManualMarginRegressionChecks
{
    public static JsonObject Run()
    {
        var config = J.O(("tax_percent", 2), ("tax_cap_gp", 5_000_000), ("tax_exempt_ids", new JsonArray()),
            ("simulation_slot_capacity", 8), ("target_slots", 8), ("max_slot_allocation_gp", 500_000),
            ("max_test_loss_gp", 10_000), ("stagnation_timeout_minutes", 12));
        var candidate = J.O(("id", 1), ("name", "Margin fixture"), ("indicative_buy_gp", 100),
            ("indicative_sell_gp", 120), ("estimated_test_loss_gp", 22), ("buy_limit", 100));
        var checks = new JsonArray();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidDataException("Manual margin regression: " + name);
            checks.Add(name);
        }
        Portfolio Portfolio(SimExchange e) => new() { Verified = true, BuyLimitsVerified = true,
            ObservedAt = e.Now, FreeCashGp = e.Cash, Members = true, SlotCapacity = 8 };
        (SimExchange Exchange, MarginRunner Runner, int Slot) Buy()
        {
            var e = new SimExchange(config); var r = new MarginRunner(e, config);
            r.Start(candidate, Portfolio(e), e.Observe()); r.Step(e.Observe()); return (e, r, r.Test!.I("slot"));
        }
        (SimExchange Exchange, MarginRunner Runner, int Slot) Sell()
        {
            var (e, r, s) = Buy(); e.Fill(s); e.Collect(s); r.ReviewManualChanges(e.Observe()); r.Step(e.Observe());
            return (e, r, r.Test!.I("slot"));
        }

        var (cancelE, cancelR, cancelS) = Buy(); cancelE.Cancel(cancelS); cancelE.Collect(cancelS);
        cancelR.ReviewManualChanges(cancelE.Observe());
        Check(cancelR.Test!.S("stage") == "DONE" && cancelR.Test.Obj("result").B("abandoned")
            && !cancelR.Test.Obj("result").B("verified"), "manually collected empty buy cancellation safely abandons margin test");
        Check(cancelE.Cash == 2_000_000 && cancelR.Test.L("buy_spent") == 0
            && cancelR.Test.Obj("result").L("actual_test_loss_gp") == 0, "empty cancellation retains exact zero cost");

        var (buyE, buyR, buyS) = Buy(); buyE.Fill(buyS); buyE.Collect(buyS);
        buyR.ReviewManualChanges(buyE.Observe());
        Check(buyR.Test!.S("stage") == "BUY_COLLECTING" && buyR.Test.L("buy_spent") == 120
            && buyE.Inventory[1] == 1, "exact external buy receipt restores purchased stock and cost");
        Check(buyE.Offers.Count == 0 && buyR.Test!["result"] is null, "passive review does not place a sale or invent a margin result");
        buyR.Step(buyE.Observe()); buyS = buyR.Test.I("slot"); buyE.Fill(buyS); buyE.Collect(buyS);
        buyR.ReviewManualChanges(buyE.Observe());
        Check(buyR.Test.S("stage") == "DONE" && buyR.Test.Obj("result").B("verified")
            && buyR.Test.Obj("result").L("actual_net_sale_gp") == 98
            && buyR.Test.Obj("result").L("actual_test_loss_gp") == 22, "manual sale collection uses exact after-tax receipt");
        var completed = buyR.Test!.ToJsonString(); buyR.ReviewManualChanges(buyE.Observe());
        Check(completed == buyR.Test.ToJsonString(), "repeated manual sale review cannot count proceeds twice");

        var (sellCancelE, sellCancelR, sellCancelS) = Sell(); sellCancelE.Cancel(sellCancelS); sellCancelE.Collect(sellCancelS);
        sellCancelR.ReviewManualChanges(sellCancelE.Observe());
        Check(sellCancelR.Test!.S("stage") == "PAUSED_HELD" && sellCancelE.Inventory[1] == 1
            && sellCancelR.Test.L("buy_spent") == 120 && sellCancelR.Test!["result"] is null,
            "manual sale cancellation preserves held item without fabricated sale");

        var (missingE, missingR, missingS) = Buy(); var missingOffer = missingE.Offers[missingS].Copy();
        missingE.Offers.Remove(missingS); missingR.ReviewManualChanges(missingE.Observe());
        Check(missingR.Test!.S("stage") == "STOPPED" && missingR.Test.S("recovery_stage") == "WAIT_BUY"
            && missingR.Test.Obj("manual_review").S("reason") == "MANUAL_MARGIN_OFFER_MISSING",
            "missing buy without receipt is quarantined instead of waiting forever");
        var missingCash = missingE.Cash;
        Check(!missingR.Step(missingE.Observe()) && missingE.Offers.Count == 0 && missingE.Cash == missingCash
            && missingR.Test!["result"] is null, "unproved missing offer cannot submit replacement or claim outcome");
        missingR.Test!["buy_limit_review_recorded"] = true;
        missingE.Offers[missingS] = missingOffer; missingR.ReviewManualChanges(missingE.Observe());
        Check(missingR.Test.S("stage") == "WAIT_BUY" && missingR.Test!["manual_review"] is null,
            "return of exact known offer resumes margin wait");
        Check(missingR.Test["buy_limit_review_recorded"] is null,
            "restoring exact offer clears old buy-limit invalidation marker for future review");

        var (changedE, changedR, changedS) = Buy(); changedE.Offers[changedS]["price"] = 121;
        Check(!changedR.Step(changedE.Observe()) && changedR.Test!.S("stage") == "STOPPED"
            && changedR.Test.Obj("manual_review").S("reason") == "MANUAL_MARGIN_OFFER_CHANGED",
            "manual reprice quarantines affected margin test without exception");
        Check(changedE.Offers[changedS].L("price") == 121 && changedE.Offers[changedS].S("state") == "BUYING",
            "manual replacement offer remains untouched");
        var preserved = changedR.Test!.ToJsonString(); var replacementRejected = false;
        try { changedR.Start(candidate, Portfolio(changedE), changedE.Observe()); }
        catch (InvalidOperationException) { replacementRejected = true; }
        Check(replacementRejected && preserved == changedR.Test.ToJsonString(), "unresolved test cannot be overwritten by a new test");

        var (stockE, stockR, stockS) = Buy(); stockE.Fill(stockS); stockE.Collect(stockS); stockR.ReviewManualChanges(stockE.Observe());
        stockE.Inventory.Remove(1);
        Check(!stockR.Step(stockE.Observe()) && stockR.Test!.S("stage") == "STOPPED"
            && stockR.Test.S("recovery_stage") == "BUY_COLLECTING" && stockR.Test.L("buy_spent") == 120,
            "manual held stock removal quarantines while preserving cost");
        stockE.Inventory[1] = 1; stockR.ReviewManualChanges(stockE.Observe());
        Check(stockR.Test!.S("stage") == "BUY_COLLECTING" && stockR.Test!["manual_review"] is null && stockE.Offers.Count == 0,
            "restored exact held stock clears manual review without placing input");

        var (partialE, partialR, partialS) = Buy(); partialE.Fill(partialS); partialE.Inventory[1] = 1;
        partialR.ReviewManualChanges(partialE.Observe());
        Check(partialR.Test!.S("stage") == "STOPPED" && !partialR.Reconcile(partialE.Observe())
            && partialE.Offers[partialS].S("state") == "BOUGHT", "stock change while known offer still exists cannot bypass quarantine");

        var (heldOfferE, heldOfferR, heldOfferS) = Buy(); heldOfferE.Fill(heldOfferS); heldOfferE.Collect(heldOfferS);
        heldOfferR.ReviewManualChanges(heldOfferE.Observe());
        var manualSlot = heldOfferE.Submit("SELL", 1, "Margin fixture", 1, 130); var manualOffer = heldOfferE.Offers[manualSlot].ToJsonString();
        Check(!heldOfferR.Step(heldOfferE.Observe()) && heldOfferR.Test!.S("stage") == "STOPPED"
            && heldOfferR.Test.S("recovery_stage") == "BUY_COLLECTING"
            && heldOfferR.Test.Obj("manual_review").S("reason") == "MANUAL_MARGIN_OFFER_CHANGED"
            && heldOfferR.Test.L("buy_spent") == 120 && heldOfferR.Test!["result"] is null
            && manualOffer == heldOfferE.Offers[manualSlot].ToJsonString(),
            "manual sale of held margin stock is quarantined without adoption cancellation or fabricated result");

        var (collectE, collectR, collectS) = Sell(); collectE.Fill(collectS); collectR.Step(collectE.Observe());
        collectE.Inventory[1] = 1;
        Check(!collectR.Step(collectE.Observe()) && collectR.Test!.S("stage") == "STOPPED"
            && collectR.Test.S("recovery_stage") == "SELL_COLLECTING"
            && collectR.Test.Obj("manual_review").S("reason") == "MANUAL_MARGIN_STOCK_CHANGED"
            && collectR.Test.L("buy_spent") == 120 && collectR.Test!["result"] is null && collectE.Offers.Count == 0,
            "changed stock during sale collection quarantines without a global exception or invented margin result");

        foreach (var kind in new[] { "account", "busy", "closed", "logged_out" })
        {
            var (ignoreE, ignoreR, ignoreS) = Buy(); ignoreE.Offers.Remove(ignoreS); var state = ignoreE.Observe();
            if (kind == "account") state["account_key"] = "other-account";
            if (kind == "busy") state["input_busy"] = true;
            if (kind == "closed") state["ge_open"] = false;
            if (kind == "logged_out") state["logged_in"] = false;
            var before = ignoreR.Test!.ToJsonString(); ignoreR.ReviewManualChanges(state);
            Check(before == ignoreR.Test.ToJsonString(), "passive review ignores unready snapshot: " + kind);
        }

        var (unprovedE, unprovedR, unprovedS) = Sell(); unprovedE.Offers.Remove(unprovedS); unprovedE.Cash += 98;
        unprovedR.ReviewManualChanges(unprovedE.Observe());
        Check(unprovedR.Test!.S("stage") == "STOPPED" && unprovedR.Test!["result"] is null
            && unprovedR.Test.L("buy_spent") == 120, "sale cash coincidence without receipt does not invent result");
        foreach (var mismatch in new[] { "none", "cash", "price" })
        {
            var (e, r, slot) = Buy(); var t = r.Test!;
            t["stage"] = "DONE"; t["recovery_stage"] = "BUY_SUBMITTING";
            t["result"] = J.O(("abandoned", true), ("verified", false));
            var state = e.Observe();
            if (mismatch == "cash") state["cash_inventory_gp"] = state.L("cash_inventory_gp") + 1;
            if (mismatch == "price") state.Objects("offers").Single(o => o.I("slot") == slot)["price"] = 121;
            r.ReviewManualChanges(state);
            Check(mismatch == "none" ? t.S("stage") == "WAIT_BUY" && t["result"] is null : t.S("stage") == "DONE",
                "late abandoned submission recovery requires exact offer and reserved cash: " + mismatch);
        }
        {
            var (e, r, slot) = Buy(); var t = r.Test!;
            e.Fill(slot); e.Collect(slot); t["stage"] = "DONE"; t["recovery_stage"] = "BUY_SUBMITTING";
            t["result"] = J.O(("abandoned", true), ("verified", false)); r.ReviewManualChanges(e.Observe());
            Check(t.S("stage") == "BUY_COLLECTING" && t["result"] is null && t.L("buy_spent") == 120,
                "late abandoned buy already collected recovers only through exact historical receipt");
        }
        return J.O(("passed", checks.Count), ("checks", checks));
    }
}
