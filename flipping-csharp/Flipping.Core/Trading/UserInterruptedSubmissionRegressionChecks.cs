using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public static class UserInterruptedSubmissionRegressionChecks
{
    public static JsonObject Run()
    {
        var config = J.O(("tax_percent", 2), ("tax_cap_gp", 5_000_000), ("tax_exempt_ids", new JsonArray()),
            ("simulation_slot_capacity", 8), ("target_slots", 8), ("max_slot_allocation_gp", 500_000),
            ("max_test_loss_gp", 10_000));
        var candidate = J.O(("id", 1), ("name", "Interrupted fixture"), ("indicative_buy_gp", 100),
            ("indicative_sell_gp", 120), ("estimated_test_loss_gp", 22), ("buy_limit", 100));
        var checks = new JsonArray();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidDataException("Interrupted submission regression: " + name);
            checks.Add(name);
        }
        JsonObject Marker(SimExchange e, bool beforeConfirm) => J.O(("before_confirm", beforeConfirm),
            ("at", e.Now), ("phase", beforeConfirm ? "PRICE" : "VERIFY_SETUP"),
            ("account_key", e.Account), ("session", "simulation"));
        (SimExchange Exchange, OrderManager Manager, JsonObject Position) Order(bool selling, bool beforeConfirm)
        {
            var e = new SimExchange(config); if (selling) e.Inventory[1] = 10;
            var p = OrderManager.NewPosition(1, "Interrupted fixture", 10, 100, 120, e.Now, e.Account!);
            p["slot"] = 0; p["stage"] = "STOPPED"; p["recovery_stage"] = selling ? "SELL_SUBMITTING" : "BUY_SUBMITTING";
            p["submission_cash_gp"] = e.Cash; p["user_interrupted_submission"] = Marker(e, beforeConfirm);
            if (selling) { p["owned_quantity"] = 10; p["acquired_quantity"] = 10; p["buy_spent"] = 1000; }
            var m = new OrderManager(e, config); m.Positions.Add(p); return (e, m, p);
        }
        (SimExchange Exchange, MarginRunner Runner, JsonObject Test) Margin(bool selling, bool beforeConfirm)
        {
            var e = new SimExchange(config); var r = new MarginRunner(e, config);
            var portfolio = new Portfolio { Verified = true, BuyLimitsVerified = true, ObservedAt = e.Now,
                FreeCashGp = e.Cash, Members = true, SlotCapacity = 8 };
            r.Start(candidate, portfolio, e.Observe()); var t = r.Test!;
            t["slot"] = 0; t["stage"] = "STOPPED"; t["recovery_stage"] = selling ? "SELL_SUBMITTING" : "BUY_SUBMITTING";
            t["submission_cash_gp"] = e.Cash; t["user_interrupted_submission"] = Marker(e, beforeConfirm);
            if (selling) { e.Inventory[1] = 1; t["buy_spent"] = 120; }
            return (e, r, t);
        }
        foreach (var selling in new[] { false, true })
        {
            var side = selling ? "sell" : "buy";
            var (preE, preM, preP) = Order(selling, true);
            Check(preM.Reconcile(preE.Observe()) && preP.S("stage") == (selling ? "BUY_COLLECTING" : "VOID")
                && preP["user_interrupted_submission"] is null, "order pre-confirm absence resolves and clears marker: " + side);
            Check(preE.Offers.Count == 0 && preE.Cash == 2_000_000 && preE.Inventory.GetValueOrDefault(1) == (selling ? 10 : 0)
                && preP["realized_profit_gp"] is null, "order pre-confirm reconciliation sends no input and invents no profit: " + side);
            var (postE, postM, postP) = Order(selling, false);
            Check(!postM.Reconcile(postE.Observe()) && postP.S("stage") == "STOPPED" && postP["user_interrupted_submission"] is JsonObject
                && postP["realized_profit_gp"] is null && postE.Offers.Count == 0,
                "order post-confirm unchanged balances cannot authorize replay: " + side);
            var (liveE, liveM, liveP) = Order(selling, false);
            liveE.Submit(selling ? "SELL" : "BUY", 1, "Interrupted fixture", 10, selling ? 120 : 100);
            Check(liveM.Reconcile(liveE.Observe()) && liveP.S("stage") == (selling ? "WAIT_SELL" : "WAIT_BUY")
                && liveP["user_interrupted_submission"] is null && liveE.Offers.Count == 1,
                "order exact live offer proves interrupted submission without duplicate: " + side);
            var (receiptE, receiptM, receiptP) = Order(selling, false);
            var slot = receiptE.Submit(selling ? "SELL" : "BUY", 1, "Interrupted fixture", 10, selling ? 120 : 100);
            receiptE.Fill(slot); receiptE.Collect(slot);
            Check(receiptM.Reconcile(receiptE.Observe()) && receiptP.S("stage") == (selling ? "DONE" : "BUY_COLLECTING")
                && receiptP["user_interrupted_submission"] is null && receiptE.Offers.Count == 0
                && (selling ? receiptP.L("realized_profit_gp") == 180 : receiptP.L("buy_spent") == 1000 && receiptP.L("owned_quantity") == 10),
                "order exact receipt proves already collected interrupted submission: " + side);
            var (legacyE, legacyM, legacyP) = Order(selling, false); legacyP.Remove("user_interrupted_submission");
            Check(legacyM.Reconcile(legacyE.Observe()) && legacyP.S("stage") == (selling ? "BUY_COLLECTING" : "VOID"),
                "legacy unmarked order absence behavior preserved: " + side);
            var (wrongE, wrongM, wrongP) = Order(selling, true); wrongP.Obj("user_interrupted_submission")["session"] = "other-session";
            Check(!wrongM.Reconcile(wrongE.Observe()) && wrongP.S("stage") == "STOPPED",
                "pre-confirm order evidence cannot cross session: " + side);
            var (unknownE, unknownM, unknownP) = Order(selling, true); unknownP.Obj("user_interrupted_submission").Remove("before_confirm");
            Check(!unknownM.Reconcile(unknownE.Observe()) && unknownP["user_interrupted_submission"] is JsonObject,
                "unknown order confirmation phase remains quarantined: " + side);

            var (mPreE, mPreR, mPreT) = Margin(selling, true);
            Check(mPreR.Reconcile(mPreE.Observe()) && mPreT.S("stage") == (selling ? "BUY_COLLECTING" : "DONE")
                && mPreT["user_interrupted_submission"] is null && mPreE.Offers.Count == 0
                && (selling ? mPreT["result"] is null && mPreT.L("buy_spent") == 120 : mPreT.Obj("result").B("abandoned") && !mPreT.Obj("result").B("verified")),
                "margin pre-confirm absence resolves without input or fabricated result: " + side);
            var (mPostE, mPostR, mPostT) = Margin(selling, false);
            Check(!mPostR.Reconcile(mPostE.Observe()) && mPostT.S("stage") == "STOPPED" && mPostT["user_interrupted_submission"] is JsonObject
                && mPostT["result"] is null && mPostE.Offers.Count == 0,
                "margin post-confirm unchanged balances cannot authorize replay: " + side);
            var (mLiveE, mLiveR, mLiveT) = Margin(selling, false);
            mLiveE.Submit(selling ? "SELL" : "BUY", 1, "Interrupted fixture", 1, selling ? 100 : 120);
            Check(mLiveR.Reconcile(mLiveE.Observe()) && mLiveT.S("stage") == (selling ? "WAIT_SELL" : "WAIT_BUY")
                && mLiveT["user_interrupted_submission"] is null && mLiveE.Offers.Count == 1,
                "margin exact live offer restores interrupted wait without duplicate: " + side);
            var (mReceiptE, mReceiptR, mReceiptT) = Margin(selling, false);
            var marginSlot = mReceiptE.Submit(selling ? "SELL" : "BUY", 1, "Interrupted fixture", 1, selling ? 100 : 120);
            mReceiptE.Fill(marginSlot); mReceiptE.Collect(marginSlot);
            Check(mReceiptR.Reconcile(mReceiptE.Observe()) && mReceiptT.S("stage") == (selling ? "DONE" : "BUY_COLLECTING")
                && mReceiptT["user_interrupted_submission"] is null
                && (selling ? mReceiptT.Obj("result").L("actual_test_loss_gp") == 22 : mReceiptT.L("buy_spent") == 120),
                "margin exact receipt resolves interrupted submission: " + side);
            var (mWrongE, mWrongR, mWrongT) = Margin(selling, true); mWrongT.Obj("user_interrupted_submission")["session"] = "other-session";
            Check(!mWrongR.Reconcile(mWrongE.Observe()) && mWrongT.S("stage") == "STOPPED",
                "pre-confirm margin evidence cannot cross session: " + side);
            var (mUnknownE, mUnknownR, mUnknownT) = Margin(selling, true); mUnknownT.Obj("user_interrupted_submission").Remove("before_confirm");
            Check(!mUnknownR.Reconcile(mUnknownE.Observe()) && mUnknownT["user_interrupted_submission"] is JsonObject,
                "unknown margin confirmation phase remains quarantined: " + side);
        }
        {
            var live = new RejectedExchange(config); var runner = new MarginRunner(live, config);
            runner.Start(candidate, new Portfolio { Verified = true, BuyLimitsVerified = true,
                FreeCashGp = live.Inner.Cash, Members = true, SlotCapacity = 8 }, live.Observe());
            var test = runner.Test!; test["slot"] = 0; test["stage"] = "BUY_SUBMITTING";
            test["recovery_stage"] = "BUY_SUBMITTING"; test["submission_cash_gp"] = live.Inner.Cash;
            Check(runner.Reconcile(live.Observe()) && test.S("stage") == "DONE"
                && test.Obj("result").B("abandoned") && test["no_submission_evidence"] is JsonObject,
                "live unmarked rejected margin buy releases exact unchanged balances");
        }
        foreach (var selling in new[] { false, true })
        {
            var (stopE, stopR, stopT) = Margin(selling, false);
            stopT["recovery_stage"] = selling ? "SELL_SUBMITTING" : "BUY_SUBMITTING";
            stopT["stage"] = "STOPPED";
            Check(stopR.ResolveSubmissionAfterCompletedStop(stopE.Observe())
                && stopT.S("stage") == (selling ? "BUY_COLLECTING" : "DONE")
                && stopT["no_submission_evidence"] is JsonObject,
                "completed stop releases absent margin submission: " + (selling ? "sell" : "buy"));
        }
        foreach (var selling in new[] { false, true })
        {
            var rejected = new RejectedExchange(config); var r = new MarginRunner(rejected, config);
            r.Start(candidate, new Portfolio { Verified = true, BuyLimitsVerified = true, FreeCashGp = rejected.Inner.Cash, Members = true, SlotCapacity = 8 }, rejected.Observe());
            if (selling) { rejected.Inner.Inventory[1] = 1; r.Test!["stage"] = "BUY_COLLECTING"; r.Test["buy_spent"] = 120; }
            try { r.Step(rejected.Observe()); throw new InvalidDataException("Expected no-submission failure"); }
            catch (OfferNotSubmitted) { }
            Check(r.Test!.S("stage") == (selling ? "BUY_COLLECTING" : "DONE") && r.Test["no_submission_evidence"] is JsonObject,
                "verified search rejection releases margin submission: " + selling);
            Check(rejected.Inner.Offers.Count == 0 && rejected.Inner.Cash == 2_000_000,
                "rejected margin creates no offer or cash movement: " + selling);
        }
        return J.O(("passed", checks.Count), ("checks", checks));
    }
    sealed class RejectedExchange(JsonObject config) : IExchange
    {
        public SimExchange Inner { get; } = new(config);
        public string? Account => Inner.Account;
        public bool IsLive => true;
        public JsonObject Observe() => Inner.Observe();
        public JsonObject Connection() => Inner.Connection();
        public int Submit(string side, int id, string name, long quantity, long price) => throw new OfferNotSubmitted(id, side, "SEARCH", J.O(("cause", "Unsupported pointer timing")));
        public void Cancel(int slot) => Inner.Cancel(slot);
        public JsonObject? Collect(int slot) => Inner.Collect(slot);
        public JsonObject CollectionAmounts(int slot) => Inner.CollectionAmounts(slot);
        public void Overview() => Inner.Overview();
        public void RequestStop() => Inner.RequestStop();
        public void Resume() => Inner.Resume();
    }
}
