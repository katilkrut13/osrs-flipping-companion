using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public interface IReceiptExchange
{
    IEnumerable<JsonObject> CollectionReceipts(bool includePending = false);
    void ReconcilePendingCollections(JsonObject state);
}

public static class Lifecycle
{
    public static long CountItem(JsonObject state, int itemId) => state.Objects("inventory")
        .Where(i => i["unnoted_id"].L(i["id"].L()) == itemId).Sum(i => i["quantity"].L());

    public static bool MatchingOffer(JsonObject? offer, int? slot, int itemId, long quantity, long price, bool buying, bool settled = false)
    {
        if (offer == null) return false;
        var states = buying ? new[] { "BOUGHT", "CANCELLED_BUY" } : new[] { "SOLD", "CANCELLED_SELL" };
        var state = offer["state"].S();
        if (!states.Contains(state) && (settled || state != (buying ? "BUYING" : "SELLING"))) return false;
        return slot.HasValue && offer["slot"].L(-1) == slot && offer["item_id"].L() == itemId
            && offer["quantity"].L(-1) == quantity && offer["price"].L(-1) == price
            && offer["filled"].L(-1) >= 0 && offer["filled"].L(-1) <= quantity && offer["spent"].L(-1) >= 0
            && (!buying || offer["spent"].L() <= offer["filled"].L() * price);
    }

    public static bool ValidCollectionReceipt(JsonObject? receipt, string account, int? slot, int itemId, long quantity, long price, bool buying, long after = 0)
    {
        if (receipt == null || receipt["account_key"].S() != account) return false;
        var offer = receipt["offer"] as JsonObject;
        if (!MatchingOffer(offer, slot, itemId, quantity, price, buying, true)) return false;
        if (!new[] { "COMPLETED", "completed" }.Contains(receipt["status"].StringValue("COMPLETED")) || receipt["observed_at"].L() < after) return false;
        if (!IsInteger(receipt["cash_delta"]) || !IsInteger(receipt["item_delta"])) return false;
        var cash = receipt["cash_delta"].L(); var items = receipt["item_delta"].L();
        var expectedItems = buying ? offer!["filled"].L() : quantity - offer!["filled"].L();
        var priorPartialAllowed = offer.S("state") == "CANCELLED_BUY";
        if (cash < 0 || priorPartialAllowed && (items < 0 || items > expectedItems)
            || buying && !priorPartialAllowed && items != expectedItems || !buying && items != expectedItems
            || buying && cash != quantity * price - offer["spent"].L()) return false;
        if (receipt.S("source") == "OBSERVED_EXTERNAL_COLLECTION") return ManualCollectionObserver.ValidReceipt(receipt);
        return receipt["cash_after_gp"].L(-1) == receipt["cash_before_gp"].L(-1) + cash
            && receipt["item_after"].L(-1) == receipt["item_before"].L(-1) + items;
    }

    public static bool IsInteger(JsonNode? value) => value.IsInteger();

    public static JsonObject CollectionReceipt(IExchange backend, JsonObject before, JsonObject offer, JsonObject amounts, JsonObject? returned = null)
    {
        var buying = new[] { "BOUGHT", "CANCELLED_BUY" }.Contains(offer["state"].S());
        var slot = (int)offer["slot"].L(); var id = (int)offer["item_id"].L();
        if (returned != null)
        {
            if (!ValidCollectionReceipt(returned, before["account_key"].S(), slot, id, offer["quantity"].L(), offer["price"].L(), buying, before["observed_at"].L()))
                throw new InvalidOperationException("Collection receipt does not match the pending offer");
            if (returned["cash_delta"].L() != amounts["cash_delta"].L() || returned["item_delta"].L() != amounts["item_delta"].L())
                throw new InvalidOperationException("Collection receipt amounts changed");
            return returned.Copy();
        }
        var after = backend.Observe();
        var raw = after.Objects("offers").FirstOrDefault(o => o["slot"].L() == slot);
        var receipt = J.O(("account_key", before["account_key"]), ("offer", offer), ("cash_delta", amounts["cash_delta"]),
            ("item_delta", amounts["item_delta"]), ("cash_before_gp", before["cash_inventory_gp"]), ("cash_after_gp", after["cash_inventory_gp"]),
            ("item_before", CountItem(before, id)), ("item_after", CountItem(after, id)), ("observed_at", after["observed_at"]), ("status", "COMPLETED"));
        if (after["account_key"].S() != before["account_key"].S() || raw != null && raw["state"].S() != "EMPTY"
            || !ValidCollectionReceipt(receipt, before["account_key"].S(), slot, id, offer["quantity"].L(), offer["price"].L(), buying, before["observed_at"].L()))
            throw new InvalidOperationException("Collection did not prove exact cash and item changes");
        return receipt;
    }

    public static JsonObject? HistoricalCollection(IExchange backend, string account, int? slot, int itemId, long quantity, long price, bool buying, long after)
    {
        if (backend is not IReceiptExchange source) return null;
        var matches = source.CollectionReceipts().Where(r => ValidCollectionReceipt(r, account, slot, itemId, quantity, price, buying, after)).ToList();
        return matches.Count == 1 ? matches[0].Copy() : null;
    }

    internal static int? Slot(JsonObject value) => value["slot"] == null ? null : (int)value["slot"].L(-1);
    internal static bool SameFields(JsonObject first, JsonObject second, params string[] keys) => keys.All(k => JsonNode.DeepEquals(first[k], second[k]));
    internal static bool HasOffer(JsonObject state, long id) => state.Objects("offers").Any(o => o["item_id"].L() == id && o["state"].S() != "EMPTY");
    internal static void AddShutdownEntry(JsonObject value, JsonObject entry)
    {
        var entries = value["shutdown_entries"] as JsonArray ?? new JsonArray();
        if (value["shutdown_entries"] == null) value["shutdown_entries"] = entries;
        entries.Add(entry["id"]?.DeepClone());
    }
}

public sealed class MarginRunner
{
    readonly IExchange backend; readonly JsonObject config; readonly string? journal;
    public JsonObject? Test { get; set; }
    public string Reason { get; set; } = "";
    public MarginRunner(IExchange backend, JsonObject config, string? journal = null) { this.backend = backend; this.config = config; this.journal = journal; }
    public void Save()
    {
        if (Test == null) return;
        if (!new[] { "STOPPED", "PAUSED_HELD", "DONE" }.Contains(Test["stage"].S())) Test["recovery_stage"] = Test["stage"]?.DeepClone();
        if (journal != null) Storage.WriteJson(journal, Test);
    }
    public void Start(JsonObject candidate, Portfolio portfolio, JsonObject state, IEnumerable<int>? allowedOfferSlots = null)
    {
        if (Test != null && Test["stage"].S() != "DONE") throw new InvalidOperationException("Margin test already active or requires reconciliation");
        var allowed = (allowedOfferSlots ?? []).ToHashSet();
        if (!portfolio.Verified || !portfolio.BuyLimitsVerified || portfolio.Offers.Count >= LimitRules.SlotCapacity(config, portfolio)
            || portfolio.Offers.Any(o => !allowed.Contains(o.Slot)) || state.Objects("offers").Any(o => !new[] { "EMPTY", "BUYING", "SELLING" }.Contains(o["state"].S())))
            throw new InvalidOperationException("Verified limits, a free slot, and only known active offers required");
        var buy = candidate["indicative_sell_gp"].L(); var sell = candidate["indicative_buy_gp"].L();
        if (buy > Math.Min(config["max_slot_allocation_gp"].L(), portfolio.FreeCashGp)) throw new InvalidOperationException("Test exceeds slot budget");
        if (config["monetary_limits_enabled"].B(true) && candidate["estimated_test_loss_gp"].L() > config["max_test_loss_gp"].L()) throw new InvalidOperationException("Estimated test loss exceeds cap");
        if (portfolio.Members != true && candidate["members"].B()) throw new InvalidOperationException("Members item unavailable");
        if (LimitRules.UsedLimit(candidate, portfolio) >= candidate["buy_limit"].L()) throw new InvalidOperationException("Buy limit exhausted");
        Test = J.O(("item_id", candidate["id"]), ("item_name", candidate["name"]), ("buy_price", buy), ("sell_price", sell),
            ("started_at", state["observed_at"]), ("initial_cash", state["cash_inventory_gp"]), ("stage", "NEW"), ("slot", null),
            ("buy_spent", 0), ("sale_cash_before", 0), ("cash_before_collect", 0), ("inventory_before", Lifecycle.CountItem(state, (int)candidate["id"].L())),
            ("result", null), ("account_key", state["account_key"]), ("recovery_stage", ""), ("submission_cash_gp", null), ("expected_sale_gp", null),
            ("shutdown_entries", null), ("storage_location", "INVENTORY"), ("collection_offer", null), ("collection_inventory_before", null),
            ("collection_expected_cash_gp", null), ("collection_expected_item_quantity", null), ("collection_receipt", null), ("wait_started_at", null));
        Reason = ""; Save();
    }
    void Collect(JsonObject raw, string stage)
    {
        var t = Test!; var slot = Lifecycle.Slot(t)!.Value;
        if (backend is IOverviewCollector overview)
        {
            var actual = overview.CollectOverview(slot);
            t["collection_offer"] = raw.Copy(); t["stage"] = stage; t["recovery_stage"] = stage;
            t["cash_before_collect"] = actual["cash_before_gp"]?.DeepClone(); t["collection_inventory_before"] = actual["item_before"]?.DeepClone();
            t["collection_expected_cash_gp"] = actual["cash_delta"]?.DeepClone(); t["collection_expected_item_quantity"] = actual["item_delta"]?.DeepClone();
            if (stage == "SELL_COLLECTING") t["expected_sale_gp"] = actual["cash_delta"]?.DeepClone();
            t["collection_receipt"] = actual.Copy(); Save(); return;
        }
        var amounts = backend.CollectionAmounts(slot); var before = backend.Observe();
        var check = before.Objects("offers").FirstOrDefault(o => o["slot"].L() == slot);
        if (check == null || !Lifecycle.SameFields(check, raw, "state", "item_id", "quantity", "filled", "price", "spent")) throw new InvalidOperationException("Margin offer changed while reading collection amounts");
        t["cash_before_collect"] = before["cash_inventory_gp"]?.DeepClone(); t["collection_inventory_before"] = Lifecycle.CountItem(before, (int)t["item_id"].L());
        t["collection_offer"] = raw.Copy(); t["collection_expected_cash_gp"] = amounts["cash_delta"]?.DeepClone();
        t["collection_expected_item_quantity"] = amounts["item_delta"]?.DeepClone(); t["collection_receipt"] = null;
        if (stage == "SELL_COLLECTING") t["expected_sale_gp"] = amounts["cash_delta"]?.DeepClone();
        t["stage"] = stage; t["recovery_stage"] = stage; Save();
        t["collection_receipt"] = Lifecycle.CollectionReceipt(backend, before, raw, amounts, backend.Collect(slot)); Save();
    }
    bool FinishSale(JsonObject state)
    {
        var t = Test!; if (t["expected_sale_gp"] == null) return false;
        var proceeds = t["expected_sale_gp"].L();
        t["result"] = J.O(("item_id", t["item_id"]), ("actual_buy_gp", t["buy_spent"]), ("actual_net_sale_gp", proceeds),
            ("actual_test_loss_gp", t["buy_spent"].L() - proceeds), ("completed_at", state["observed_at"]), ("verified", true));
        t["stage"] = "DONE"; ClearManualReview(); Reason = ""; Save(); return true;
    }
    void Abandon(long now)
    {
        var t = Test!; t["result"] = J.O(("item_id", t["item_id"]), ("actual_buy_gp", 0), ("actual_net_sale_gp", 0),
            ("actual_test_loss_gp", 0), ("completed_at", now), ("verified", false), ("abandoned", true)); t["stage"] = "DONE"; ClearManualReview();
    }
    void QuarantineManualChange(JsonObject state, string reason, JsonObject? offer = null)
    {
        var t = Test!;
        if (t.S("stage") == "STOPPED" && t.Obj("manual_review").S("reason") == reason) return;
        if (t.S("stage") != "STOPPED") t["recovery_stage"] = t["stage"]?.DeepClone();
        t["stage"] = "STOPPED";
        t["manual_review"] = J.O(("reason", reason), ("observed_at", state.L("observed_at")),
            ("account_key", state.S("account_key")), ("previous_stage", t.S("recovery_stage")),
            ("observed_inventory_quantity", Lifecycle.CountItem(state, t.I("item_id"))),
            ("observed_offer", offer));
        Reason = reason; Save();
    }
    void ClearManualReview() { Test?.Remove("manual_review"); Test?.Remove("buy_limit_review_recorded"); }
    public void ReviewManualChanges(JsonObject state)
    {
        var t = Test;
        if (t is not null && t.S("stage") == "DONE" && t.Obj("result").B("abandoned") && t.S("recovery_stage") == "BUY_SUBMITTING"
            && !state.B("input_busy") && state.B("logged_in") && state.B("ge_open") && t.S("account_key") == state.S("account_key")
            && Lifecycle.CountItem(state, t.I("item_id")) == t.L("inventory_before") + 1)
        {
            var late = Lifecycle.HistoricalCollection(backend, t.S("account_key"), Lifecycle.Slot(t), t.I("item_id"), 1, t.L("buy_price"), true, t.L("started_at"));
            if (late is not null && late.Obj("offer").L("filled") == 1)
            {
                t["buy_spent"] = late.Obj("offer")["spent"]?.DeepClone(); t["collection_receipt"] = late.Copy();
                t["stage"] = "BUY_COLLECTING"; t["result"] = null; t["late_submission_recovered_at"] = state["observed_at"]?.DeepClone();
                ClearManualReview(); Reason = ""; Save();
            }
        }
        if (t is not null && t.S("stage") == "DONE" && t.Obj("result").B("abandoned") && t.S("recovery_stage") == "BUY_SUBMITTING"
            && !state.B("input_busy") && state.B("logged_in") && state.B("ge_open") && t.S("account_key") == state.S("account_key")
            && t["submission_cash_gp"].IsInteger() && state.L("cash_inventory_gp") == t.L("submission_cash_gp") - t.L("buy_price")
            && Lifecycle.CountItem(state, t.I("item_id")) == t.L("inventory_before")
            && state.Objects("offers").Any(o => Lifecycle.MatchingOffer(o, Lifecycle.Slot(t), t.I("item_id"), 1, t.L("buy_price"), true)))
        {
            t["stage"] = "WAIT_BUY"; t["result"] = null; t["wait_started_at"] = state["observed_at"]?.DeepClone();
            t["late_submission_recovered_at"] = state["observed_at"]?.DeepClone(); ClearManualReview(); Reason = ""; Save();
        }
        if (t == null || t.S("stage") == "DONE" || state.B("input_busy") || !state.B("logged_in") || !state.B("ge_open")
            || t.S("account_key") == "" || t.S("account_key") != state.S("account_key")) return;
        var stage = t.S("stage") == "STOPPED" ? t.S("recovery_stage") : t.S("stage");
        var slot = Lifecycle.Slot(t); var id = t.I("item_id");
        var raw = state.Objects("offers").FirstOrDefault(o => o.L("slot", -1) == slot && o.S("state") != "EMPTY");
        if (stage is "WAIT_BUY" or "WAIT_SELL")
        {
            var buying = stage == "WAIT_BUY";
            if (Lifecycle.MatchingOffer(raw, slot, id, 1, t.L(buying ? "buy_price" : "sell_price"), buying))
            {
                if (Lifecycle.CountItem(state, id) != t.L("inventory_before"))
                { QuarantineManualChange(state, "MANUAL_MARGIN_STOCK_CHANGED", raw); return; }
                if (t.S("stage") == "STOPPED" && t["manual_review"] is not null)
                { t["stage"] = stage; ClearManualReview(); Reason = ""; Save(); }
                return;
            }
            if (raw == null && Reconcile(state)) { ClearManualReview(); Save(); return; }
            QuarantineManualChange(state, raw == null ? "MANUAL_MARGIN_OFFER_MISSING" : "MANUAL_MARGIN_OFFER_CHANGED", raw);
            return;
        }
        if (stage is "BUY_COLLECTING" or "PAUSED_HELD" && t.S("storage_location", "INVENTORY") == "INVENTORY")
        {
            if (Lifecycle.HasOffer(state, id))
            {
                // A failed collection may leave the exact settled buy in place. A new
                // manual offer for held stock has no ownership proof and is left alone.
                if (stage == "BUY_COLLECTING" && Lifecycle.MatchingOffer(raw, slot, id, 1, t.L("buy_price"), true, true)
                    && Lifecycle.CountItem(state, id) == t.L("inventory_before") && Reconcile(state)) return;
                QuarantineManualChange(state, "MANUAL_MARGIN_OFFER_CHANGED", raw); return;
            }
            var expected = t.L("inventory_before") + (t.L("buy_spent") > 0 ? 1 : 0);
            if (Lifecycle.CountItem(state, id) != expected)
                QuarantineManualChange(state, "MANUAL_MARGIN_STOCK_CHANGED");
            else if (t.S("stage") == "STOPPED" && t["manual_review"] is not null && Reconcile(state))
            { ClearManualReview(); Save(); }
            return;
        }
        if (stage == "SELL_COLLECTING")
        {
            if (Reconcile(state)) return;
            QuarantineManualChange(state, Lifecycle.HasOffer(state, id) ? "MANUAL_MARGIN_OFFER_CHANGED"
                : Lifecycle.CountItem(state, id) != t.L("inventory_before") ? "MANUAL_MARGIN_STOCK_CHANGED"
                : "MANUAL_MARGIN_COLLECTION_UNPROVED", raw);
        }
    }
    public bool Step(JsonObject state)
    {
        ReviewManualChanges(state);
        var t = Test;
        if (t == null || new[] { "DONE", "STOPPED", "PAUSED_HELD" }.Contains(t["stage"].S())) return false;
        if (t["account_key"].S() != state["account_key"].S()) throw new InvalidOperationException("Margin account changed");
        if (state["input_busy"].B()) return false;
        var slot = Lifecycle.Slot(t); var id = (int)t["item_id"].L(); var stage = t["stage"].S();
        var raw = state.Objects("offers").FirstOrDefault(o => o["slot"].L(-1) == slot && o["state"].S() != "EMPTY");
        // Once a collection is complete the GE slot may immediately be reused by an
        // independent position. That unrelated offer must not masquerade as the old
        // margin offer and prevent the collected stock from advancing to its sale.
        if (stage is "BUY_COLLECTING" or "SELL_COLLECTING" && raw != null && raw.I("item_id") != id) raw = null;
        var buying = stage == "WAIT_BUY";
        if (new[] { "WAIT_BUY", "WAIT_SELL" }.Contains(stage) && raw != null && !Lifecycle.MatchingOffer(raw, slot, id, 1, t[buying ? "buy_price" : "sell_price"].L(), buying))
        { QuarantineManualChange(state, "MANUAL_MARGIN_OFFER_CHANGED", raw); return false; }
        var now = state["observed_at"].L();
        // A one-item reverse margin test should fill almost immediately. Reusing the
        // full-position stagnation window made one stale test occupy the single test
        // runner for twelve minutes while the GE had empty slots.
        var timeout = config.L("margin_test_timeout_seconds", 120);
        if (new[] { "WAIT_BUY", "WAIT_SELL" }.Contains(stage) && raw != null && new[] { "BUYING", "SELLING" }.Contains(raw["state"].S())
            && now - t["wait_started_at"].L(t["started_at"].L()) >= timeout)
        { backend.Cancel(slot!.Value); Reason = "Margin offer timed out; collecting its exact filled portion"; return true; }
        if (stage == "NEW")
        {
            var slots = LimitRules.EmptySlots(state, config).ToList(); if (slots.Count == 0) return false;
            t["slot"] = slots.Min(); t["submission_cash_gp"] = state["cash_inventory_gp"]?.DeepClone(); t["stage"] = "BUY_SUBMITTING"; t["recovery_stage"] = "BUY_SUBMITTING"; Save();
            try { t["slot"] = backend.Submit("BUY", id, t["item_name"].S(), 1, t["buy_price"].L()); }
            catch (OfferNotSubmitted ex) when (ex.ItemId == id && ex.Side == "BUY")
            {
                t["no_submission_evidence"] = ex.Details.Copy();
                Abandon(now); Save(); throw;
            }
            t["stage"] = "WAIT_BUY"; t["wait_started_at"] = now;
        }
        else if (stage == "WAIT_BUY" && raw != null && new[] { "BOUGHT", "CANCELLED_BUY" }.Contains(raw["state"].S()))
        { t["buy_spent"] = raw["spent"]?.DeepClone(); Collect(raw, "BUY_COLLECTING"); }
        else if (stage == "BUY_COLLECTING" && raw == null)
        {
            if (Lifecycle.CountItem(state, id) == t["inventory_before"].L() && t["buy_spent"].L() == 0) Abandon(now);
            else if (Lifecycle.CountItem(state, id) == t["inventory_before"].L() + 1)
            {
                var slots = LimitRules.EmptySlots(state, config).ToList(); if (slots.Count == 0) return false;
                t["slot"] = slots.Min(); t["submission_cash_gp"] = state["cash_inventory_gp"]?.DeepClone(); t["stage"] = "SELL_SUBMITTING"; t["recovery_stage"] = "SELL_SUBMITTING"; Save();
                try { t["slot"] = backend.Submit("SELL", id, t["item_name"].S(), 1, t["sell_price"].L()); }
                catch (OfferNotSubmitted ex) when (ex.ItemId == id && ex.Side == "SELL")
                {
                    t["no_submission_evidence"] = ex.Details.Copy();
                    t["stage"] = "BUY_COLLECTING"; Save(); throw;
                }
                t["stage"] = "WAIT_SELL"; t["wait_started_at"] = now;
            }
            else { QuarantineManualChange(state, "MANUAL_MARGIN_STOCK_CHANGED"); return false; }
        }
        else if (stage == "WAIT_SELL" && raw != null && new[] { "SOLD", "CANCELLED_SELL" }.Contains(raw["state"].S()))
        {
            Collect(raw, "SELL_COLLECTING");
            if (raw["filled"].L() == 0) { t["stage"] = "PAUSED_HELD"; t["recovery_stage"] = "BUY_COLLECTING"; Reason = "Margin sale did not fill; stock retained"; }
        }
        else if (stage == "SELL_COLLECTING" && raw == null && Lifecycle.CountItem(state, id) == t["inventory_before"].L())
        {
            var receiptOk = Lifecycle.ValidCollectionReceipt(t["collection_receipt"] as JsonObject, t["account_key"].S(), slot, id, 1, t["sell_price"].L(), false, t["started_at"].L())
                && t["collection_receipt"]?["cash_delta"].L() == t["expected_sale_gp"].L();
            if (!receiptOk && state["cash_inventory_gp"].L() != t["cash_before_collect"].L() + t["expected_sale_gp"].L())
            { QuarantineManualChange(state, "MANUAL_MARGIN_COLLECTION_UNPROVED"); return false; }
            FinishSale(state);
        }
        else return false;
        Save(); return true;
    }
    public bool ResolveSubmissionAfterCompletedStop(JsonObject state)
    {
        var t = Test;
        if (t == null) return false;
        var submissionStage = t.S("stage") == "STOPPED" ? t.S("recovery_stage") : t.S("stage");
        if (submissionStage is not ("BUY_SUBMITTING" or "SELL_SUBMITTING")
            || !state.B("logged_in") || !state.B("ge_open") || state.B("input_busy")
            || t.S("account_key") == "" || t.S("account_key") != state.S("account_key")
            || Lifecycle.HasOffer(state, t.I("item_id"))) return false;
        var buying = submissionStage == "BUY_SUBMITTING";
        var expected = t.L("inventory_before") + (buying ? 0 : 1);
        if (Lifecycle.CountItem(state, t.I("item_id")) != expected) return false;
        t["no_submission_evidence"] = J.O(("status", "NOT_PRESENT_AFTER_COMPLETED_STOP"),
            ("account_key", t.S("account_key")), ("item_id", t.I("item_id")),
            ("side", buying ? "BUY" : "SELL"), ("observed_at", state.L("observed_at")));
        t.Remove("user_interrupted_submission"); ClearManualReview();
        if (buying) Abandon(state.L("observed_at"));
        else { t["stage"] = "BUY_COLLECTING"; t["recovery_stage"] = "BUY_COLLECTING"; Reason = ""; }
        Save(); return true;
    }
    public bool Reconcile(JsonObject state)
    {
        var t = Test; if (t == null || t["stage"].S() == "DONE") return true;
        if (state["input_busy"].B() || t["account_key"].S() == "" || t["account_key"].S() != state["account_key"].S()) return false;
        if (backend is IReceiptExchange receipts) receipts.ReconcilePendingCollections(state);
        var stage = t["stage"].S() == "STOPPED" ? t["recovery_stage"].S() : t["stage"].S(); var id = (int)t["item_id"].L(); var slot = Lifecycle.Slot(t);
        if (stage == "NEW") { t["stage"] = "NEW"; ClearManualReview(); Reason = ""; Save(); return true; }
        var hasOffer = Lifecycle.HasOffer(state, id); var count = Lifecycle.CountItem(state, id); var before = t["inventory_before"].L();
        var interruption = t["user_interrupted_submission"];
        var absenceProved = interruption is null && !backend.IsLive || interruption is JsonObject interrupted
            && interrupted.B("before_confirm") && interrupted.S("account_key") == t.S("account_key")
            && interrupted.S("session") != "" && interrupted.S("session") == state.S("session");
        if (stage == "PAUSED_HELD" && t["storage_location"].StringValue("INVENTORY") == "INVENTORY" && count == before + 1) { t["stage"] = "BUY_COLLECTING"; ClearManualReview(); Reason = ""; Save(); return true; }
        var buying = new[] { "WAIT_BUY", "BUY_SUBMITTING", "BUY_COLLECTING" }.Contains(stage);
        if (state.Objects("offers").Any(o => Lifecycle.MatchingOffer(o, slot, id, 1, t[buying ? "buy_price" : "sell_price"].L(), buying)))
        {
            if (count != before) return false;
            t["stage"] = buying ? "WAIT_BUY" : "WAIT_SELL"; t.Remove("user_interrupted_submission"); ClearManualReview(); Reason = ""; Save(); return true;
        }
        var receipt = Lifecycle.HistoricalCollection(backend, t["account_key"].S(), slot, id, 1, t[buying ? "buy_price" : "sell_price"].L(), buying, t["started_at"].L());
        if (!hasOffer && receipt != null)
        {
            t["collection_receipt"] = receipt.Copy();
            if (buying)
            {
                t["buy_spent"] = receipt["offer"]?["spent"]?.DeepClone();
                if (receipt["offer"]?["filled"].L() == 0) Abandon(receipt["observed_at"].L());
                else if (count == before + 1) t["stage"] = "BUY_COLLECTING"; else return false;
            }
            else if (receipt["offer"]?["filled"].L() == 1 && count == before) { t["expected_sale_gp"] = receipt["cash_delta"]?.DeepClone(); FinishSale(state); }
            else if (receipt["offer"]?["filled"].L() == 0 && count == before + 1) t["stage"] = "PAUSED_HELD"; else return false;
            t.Remove("user_interrupted_submission"); ClearManualReview(); Reason = ""; Save(); return true;
        }
        if (stage is "WAIT_BUY" or "WAIT_SELL" && !hasOffer && count == before + 1 && t.L("buy_spent") > 0)
        {
            // No exact receipt exists, but the one-item probe is still fully owned.
            // Exact stock plus its recorded cost proves the buy; a missing sale with
            // the same item proves that no proceeds should be claimed.
            t["automatic_recovery_evidence"] = J.O(("status", stage == "WAIT_BUY"
                    ? "MARGIN_BUY_EXACT_STOCK_AND_COST" : "MARGIN_SALE_RETURNED_EXACT_STOCK"),
                ("item_id", id), ("quantity", 1), ("buy_spent_gp", t.L("buy_spent")),
                ("observed_at", state.L("observed_at")));
            t["slot"] = null; t["stage"] = "BUY_COLLECTING"; t["recovery_stage"] = "BUY_COLLECTING";
            t.Remove("user_interrupted_submission"); ClearManualReview(); Reason = ""; Save(); return true;
        }
        if (stage == "BUY_COLLECTING" && !hasOffer && count == before + 1 && t["buy_spent"].L() > 0) { t["stage"] = "BUY_COLLECTING"; ClearManualReview(); Reason = ""; Save(); return true; }
        if (stage == "SELL_COLLECTING" && !hasOffer && t["expected_sale_gp"] != null && count == before)
        {
            var receiptOk = Lifecycle.ValidCollectionReceipt(t["collection_receipt"] as JsonObject, t["account_key"].S(), slot, id, 1, t["sell_price"].L(), false, t["started_at"].L()) && t["collection_receipt"]?["cash_delta"].L() == t["expected_sale_gp"].L();
            if (receiptOk || state["cash_inventory_gp"].L() == t["cash_before_collect"].L() + t["expected_sale_gp"].L()) return FinishSale(state);
        }
        if (stage == "BUY_SUBMITTING" && (interruption is null || interruption is JsonObject && absenceProved)
            && !hasOffer && t["submission_cash_gp"] != null
            && state["cash_inventory_gp"].L() == t["submission_cash_gp"].L() && count == before)
        {
            // A rejected/failed search can leave a live margin test at
            // BUY_SUBMITTING without an interruption marker. Exact unchanged
            // cash and inventory, plus the absence of an offer and receipt,
            // prove that this one-item probe never entered the exchange.
            t["no_submission_evidence"] = J.O(("status", "NOT_SUBMITTED_EXACT_BALANCES"),
                ("account_key", t.S("account_key")), ("item_id", id), ("side", "BUY"),
                ("observed_at", state.L("observed_at")));
            Abandon(state["observed_at"].L()); t.Remove("user_interrupted_submission"); ClearManualReview(); Reason = ""; Save(); return true;
        }
        if (stage == "SELL_SUBMITTING" && (interruption is null || interruption is JsonObject && absenceProved)
            && !hasOffer && t["submission_cash_gp"] != null
            && state["cash_inventory_gp"].L() == t["submission_cash_gp"].L() && count == before + 1 && t.L("buy_spent") > 0)
        {
            // The exact purchased unit is still present, cash is unchanged and no
            // offer or collection receipt exists. The sell did not take ownership
            // of the item, so retry from the held-stock stage instead of freezing
            // every new entry behind a global recovery review.
            t["no_submission_evidence"] = J.O(("status", "NOT_SUBMITTED_EXACT_BALANCES"),
                ("account_key", t.S("account_key")), ("item_id", id), ("side", "SELL"),
                ("observed_at", state.L("observed_at")));
            t["stage"] = "BUY_COLLECTING"; t["recovery_stage"] = "BUY_COLLECTING";
            t.Remove("user_interrupted_submission"); ClearManualReview(); Reason = ""; Save(); return true;
        }
        return false;
    }
    public void ApplyShutdownCollection(JsonObject entry, JsonObject state)
    {
        var t = Test; var o = entry["offer"] as JsonObject; if (t == null || o == null) return;
        if ((t["shutdown_entries"] as JsonArray)?.Any(v => JsonNode.DeepEquals(v, entry["id"])) == true) return;
        if (t["stage"].S() == "DONE" || t["account_key"].S() != state["account_key"].S() || Lifecycle.Slot(t) != Lifecycle.Slot(o) || t["item_id"].L() != o["item_id"].L()) return;
        var buying = new[] { "BOUGHT", "CANCELLED_BUY" }.Contains(o["state"].S());
        if (!Lifecycle.MatchingOffer(o, Lifecycle.Slot(t), (int)t["item_id"].L(), 1, t[buying ? "buy_price" : "sell_price"].L(), buying, true)) throw new InvalidOperationException("Margin shutdown offer mismatch");
        if (buying) t["buy_spent"] = o["spent"]?.DeepClone();
        var held = buying ? o["filled"].L() : 1 - o["filled"].L();
        if (held > 0) { t["stage"] = "PAUSED_HELD"; t["recovery_stage"] = "BUY_COLLECTING"; }
        else
        {
            t["result"] = J.O(("item_id", t["item_id"]), ("actual_buy_gp", t["buy_spent"]), ("actual_net_sale_gp", buying ? 0 : entry["cash_delta"].L()),
                ("actual_test_loss_gp", buying ? 0 : t["buy_spent"].L() - entry["cash_delta"].L()), ("completed_at", state["observed_at"]), ("verified", !buying)); t["stage"] = "DONE";
        }
        Lifecycle.AddShutdownEntry(t, entry); Save();
    }
    public void Recover(JsonObject? observedState = null)
    {
        if (journal == null || !File.Exists(journal)) return;
        Test = Storage.ReadJson(journal) as JsonObject;
        if (Test != null && !new[] { "DONE", "STOPPED", "PAUSED_HELD" }.Contains(Test["stage"].S()))
        { Test["recovery_stage"] = Test["stage"]?.DeepClone(); Test["stage"] = "STOPPED"; Reason = "Recovered unfinished test; matching offer or receipt required"; Save(); }
        if (observedState != null) Reconcile(observedState);
    }
}
