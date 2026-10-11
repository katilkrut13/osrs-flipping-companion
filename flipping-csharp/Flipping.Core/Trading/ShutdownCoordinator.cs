using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public sealed class ShutdownCoordinator
{
    readonly IExchange backend; readonly string? journal;
    public JsonObject? Plan { get; private set; }
    public bool Pending => Plan != null && Plan["stage"].S() is not ("DONE" or "REVIEW");
    public ShutdownCoordinator(IExchange backend, string? journal = null) { this.backend = backend; this.journal = journal; }
    public void Save() { if (journal != null && Plan != null) Storage.WriteJson(journal, Plan); }
    public void Recover() { if (journal != null && File.Exists(journal)) Plan = Storage.ReadJson(journal) as JsonObject; }
    public void Start(JsonObject state)
    {
        if (Pending) return;
        Plan = J.O(("account_key", state["account_key"]), ("started_at", state["observed_at"]), ("stage", "CANCELLING"),
            ("entries", J.A(state.Objects("offers").Where(o => o["state"].S() != "EMPTY").Select(o => J.O(("id", Guid.NewGuid().ToString()), ("offer", o), ("stage", "PENDING")))))); Save();
    }
    void Review(string message) { Plan!["stage"] = "REVIEW"; Plan["review_reason"] = message; Save(); throw new InvalidOperationException(message); }
    JsonObject? CompletedReceipt(JsonObject entry)
    {
        if (backend is not IReceiptExchange source || entry["offer"] is not JsonObject o) return null;
        var buying = o.S("state") is "BUYING" or "BOUGHT" or "CANCELLED_BUY";
        var matches = source.CollectionReceipts().Where(r => Lifecycle.ValidCollectionReceipt(r, Plan!.S("account_key"), Lifecycle.Slot(o),
            o.I("item_id"), o.L("quantity"), o.L("price"), buying, Plan.L("started_at"))
            && r["offer"] is JsonObject final && final.L("filled") >= o.L("filled") && final.L("spent") >= o.L("spent")).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    public bool ResumeReview(JsonObject state)
    {
        if (Plan?.S("stage") != "REVIEW") return true;
        if (state.B("input_busy") || state.S("account_key") != Plan.S("account_key")) return false;
        if (backend is IReceiptExchange receipts) receipts.ReconcilePendingCollections(state);
        foreach (var e in Plan.Objects("entries").Where(e => e.S("stage") != "DONE"))
        {
            var expected = e.Obj("offer"); var raw = state.Objects("offers").FirstOrDefault(o => Lifecycle.Slot(o) == Lifecycle.Slot(expected));
            if (raw?.S("state") == "EMPTY" && CompletedReceipt(e) != null) continue;
            // A fully filled sale owns no remaining items. Preserve its unknown
            // proceeds for accounting review, without freezing unrelated offers.
            if (raw?.S("state") == "EMPTY" && expected.S("state") == "SOLD"
                && expected.L("filled") == expected.L("quantity")
                && !Lifecycle.HasOffer(state, expected.L("item_id")))
            {
                e["stage"] = "UNRESOLVED"; e["applied"] = true;
                e["review_reason"] = "Completed sale disappeared without a collection receipt; proceeds unknown";
                e["reviewed_at"] = state["observed_at"]?.DeepClone(); continue;
            }
            var buying = expected.S("state") is "BUYING" or "BOUGHT" or "CANCELLED_BUY";
            if (raw == null || !Lifecycle.MatchingOffer(raw, Lifecycle.Slot(expected), expected.I("item_id"), expected.L("quantity"), expected.L("price"), buying)
                || raw.L("filled") < expected.L("filled"))
            {
                Plan["review_reason"] = "Missing or changed shutdown offer without exact receipt: " + expected.S("item_name") + " (slot " + expected.L("slot") + ")";
                Save(); return false;
            }
        }
        Plan["stage"] = "CANCELLING"; Plan.Remove("review_reason"); Save(); return true;
    }
    JsonObject? PendingReceipt(JsonObject state, JsonObject offer)
    {
        if (backend is not IReceiptExchange source) return null;
        var matches = source.CollectionReceipts(true).Where(r => r["status"].S() == "PREPARED" && r["account_key"].S() == state["account_key"].S()
            && JsonNode.DeepEquals(r["session"], state["session"]) && r["offer"] is JsonObject ro && Lifecycle.SameFields(ro, offer, "slot", "state", "item_id", "quantity", "price", "filled", "spent")).ToList();
        if (matches.Count > 1) Review("Ambiguous shutdown collection receipts"); if (matches.Count == 0) return null;
        var r = matches[0]; var buying = offer["state"].S() is "BOUGHT" or "CANCELLED_BUY";
        var cash = state["cash_inventory_gp"].L() - r["cash_before_gp"].L(); var items = Lifecycle.CountItem(state, (int)offer["item_id"].L()) - r["item_before"].L();
        if (!Lifecycle.MatchingOffer(r["offer"] as JsonObject, Lifecycle.Slot(offer), (int)offer["item_id"].L(), offer["quantity"].L(), offer["price"].L(), buying, true)
            || !r["collect_started_at"].IsInteger() || r["collect_started_at"].L() > state["observed_at"].L()
            || new[] { "cash_before_gp", "item_before", "expected_cash_delta", "expected_item_delta" }.Any(k => !r[k].IsInteger() || r[k].L() < 0)
            || offer.S("state") == "CANCELLED_BUY" && (r["expected_item_delta"].L() < 0 || r["expected_item_delta"].L() > offer["filled"].L())
            || buying && offer.S("state") != "CANCELLED_BUY" && r["expected_item_delta"].L() != offer["filled"].L()
            || !buying && r["expected_item_delta"].L() != offer["quantity"].L() - offer["filled"].L()
            || buying && r["expected_cash_delta"].L() != offer["quantity"].L() * offer["price"].L() - offer["spent"].L()
            || cash < 0 || cash > r["expected_cash_delta"].L() || items < 0 || items > r["expected_item_delta"].L()) Review("Invalid shutdown partial collection receipt");
        return r;
    }
    bool ReceiptMatchesEntry(JsonObject? receipt, JsonObject entry)
    {
        var offer = entry["offer"] as JsonObject; if (offer == null || receipt == null) return false;
        var buying = offer["state"].S() is "BOUGHT" or "CANCELLED_BUY";
        return Lifecycle.ValidCollectionReceipt(receipt, Plan!["account_key"].S(), Lifecycle.Slot(offer), (int)offer["item_id"].L(), offer["quantity"].L(), offer["price"].L(), buying, Plan["started_at"].L())
            && receipt["offer"] is JsonObject ro && Lifecycle.SameFields(ro, offer, "state", "filled", "spent")
            && receipt["cash_before_gp"].L() == entry["cash_before"].L() && receipt["item_before"].L() == entry["inventory_before"].L()
            && receipt["cash_delta"].L() == entry["cash_delta"].L() && receipt["item_delta"].L() == entry["item_delta"].L();
    }
    public JsonObject? Step(JsonObject state)
    {
        if (!Pending || state["input_busy"].B()) return null;
        if (state["account_key"].S() != Plan!["account_key"].S()) Review("Shutdown account changed");
        foreach (var entry in Plan!.Objects("entries"))
        {
            if (entry["stage"].S() == "UNRESOLVED") continue;
            if (entry["stage"].S() == "DONE") { if (!entry["applied"].B()) return entry; continue; }
            var expected = entry["offer"] as JsonObject ?? throw new InvalidOperationException("Missing shutdown offer");
            var raw = state.Objects("offers").FirstOrDefault(o => Lifecycle.Slot(o) == Lifecycle.Slot(expected));
            if (entry.S("stage") == "PENDING" && raw?.S("state") == "EMPTY" && CompletedReceipt(entry) is JsonObject recovered)
            {
                entry["offer"] = recovered["offer"]?.DeepClone(); entry["collection_receipt"] = recovered.Copy();
                entry["cash_before"] = recovered["cash_before_gp"]?.DeepClone(); entry["inventory_before"] = recovered["item_before"]?.DeepClone();
                entry["cash_delta"] = recovered["cash_delta"]?.DeepClone(); entry["item_delta"] = recovered["item_delta"]?.DeepClone();
                entry["stage"] = "DONE"; Save(); return entry;
            }
            if (entry["stage"].S() == "COLLECTING" && raw?["state"].S() == "EMPTY")
            {
                var receipt = entry["collection_receipt"] as JsonObject ?? Lifecycle.HistoricalCollection(backend, Plan["account_key"].S(), Lifecycle.Slot(expected),
                    (int)expected["item_id"].L(), expected["quantity"].L(), expected["price"].L(), expected["state"].S() is "BOUGHT" or "CANCELLED_BUY", Plan["started_at"].L());
                var ok = ReceiptMatchesEntry(receipt, entry); if (receipt != null && !ok) Review("Shutdown completed collection receipt changed");
                if (!ok && state["cash_inventory_gp"].L() != entry["cash_before"].L() + entry["cash_delta"].L()
                    || Lifecycle.CountItem(state, (int)expected["item_id"].L()) != entry["inventory_before"].L() + entry["item_delta"].L()) Review("Shutdown collection balance mismatch");
                entry["stage"] = "DONE"; Save(); return entry;
            }
            if (raw == null || raw["state"].S() == "EMPTY" || !Lifecycle.SameFields(raw, expected, "slot", "item_id", "quantity", "price")
                || raw["filled"].L() < expected["filled"].L()
                || new[] { "BUYING", "BOUGHT", "CANCELLED_BUY" }.Contains(raw["state"].S()) != new[] { "BUYING", "BOUGHT", "CANCELLED_BUY" }.Contains(expected["state"].S())) Review("Shutdown offer changed; no cancellation sent");
            if (raw!["state"].S() is "BUYING" or "SELLING") { backend.Cancel((int)raw["slot"].L()); return null; }
            if (raw["state"].S() is not ("BOUGHT" or "SOLD" or "CANCELLED_BUY" or "CANCELLED_SELL")) throw new InvalidOperationException("Unsupported shutdown offer state");
            if (entry["stage"].S() == "COLLECTING")
            {
                if (!Lifecycle.SameFields(raw, expected, "state", "filled", "spent")) Review("Shutdown settled collection offer changed");
                if (state["cash_inventory_gp"].L() != entry["cash_before"].L() || Lifecycle.CountItem(state, (int)raw["item_id"].L()) != entry["inventory_before"].L())
                {
                    var pending = PendingReceipt(state, raw);
                    if (pending == null || pending["cash_before_gp"].L() != entry["cash_before"].L() || pending["item_before"].L() != entry["inventory_before"].L()
                        || pending["expected_cash_delta"].L() != entry["cash_delta"].L() || pending["expected_item_delta"].L() != entry["item_delta"].L()
                        || entry["receipt_id"] != null && !JsonNode.DeepEquals(pending["receipt_id"], entry["receipt_id"])) Review("Collection outcome ambiguous; exact partial receipt required");
                    var amounts = backend.CollectionAmounts((int)raw["slot"].L());
                    if (amounts["cash_delta"].L() != entry["cash_delta"].L() || amounts["item_delta"].L() != entry["item_delta"].L()) Review("Shutdown partial collection amounts changed");
                }
            }
            else
            {
                if (backend is IOverviewCollector overview)
                {
                    var actual = overview.CollectOverview(raw.I("slot"));
                    entry["offer"] = raw.Copy(); entry["stage"] = "COLLECTING";
                    entry["cash_before"] = actual["cash_before_gp"]?.DeepClone(); entry["inventory_before"] = actual["item_before"]?.DeepClone();
                    entry["cash_delta"] = actual["cash_delta"]?.DeepClone(); entry["item_delta"] = actual["item_delta"]?.DeepClone();
                    entry["collection_receipt"] = actual.Copy(); Save(); return null;
                }
                var amounts = backend.CollectionAmounts((int)raw["slot"].L()); var refreshed = backend.Observe();
                var check = refreshed.Objects("offers").FirstOrDefault(o => Lifecycle.Slot(o) == Lifecycle.Slot(raw));
                if (refreshed["account_key"].S() != Plan["account_key"].S() || !JsonNode.DeepEquals(refreshed["session"], state["session"])
                    || check == null || !Lifecycle.SameFields(check, raw, "state", "item_id", "quantity", "filled", "price", "spent")) throw new InvalidOperationException("Settled offer changed while reading collection amounts");
                entry["offer"] = raw.Copy(); var pending = PendingReceipt(refreshed, raw);
                if (pending != null && (amounts["cash_delta"].L() != pending["expected_cash_delta"].L() || amounts["item_delta"].L() != pending["expected_item_delta"].L())) Review("Shutdown pending collection amounts changed");
                if ((amounts.ContainsKey("cash_before_gp") || amounts.ContainsKey("item_before")) && (pending == null || amounts["cash_before_gp"].L(-1) != pending["cash_before_gp"].L() || amounts["item_before"].L(-1) != pending["item_before"].L())) Review("Shutdown original collection baseline is unverified");
                entry["stage"] = "COLLECTING"; entry["cash_before"] = pending?["cash_before_gp"]?.DeepClone() ?? refreshed["cash_inventory_gp"]?.DeepClone();
                entry["inventory_before"] = pending != null ? pending["item_before"]?.DeepClone() : JsonValue.Create(Lifecycle.CountItem(refreshed, (int)raw["item_id"].L()));
                entry["cash_delta"] = amounts["cash_delta"]?.DeepClone(); entry["item_delta"] = amounts["item_delta"]?.DeepClone(); if (pending != null) entry["receipt_id"] = pending["receipt_id"]?.DeepClone(); Save();
            }
            var returned = backend.Collect((int)raw["slot"].L());
            if (returned != null) { if (!ReceiptMatchesEntry(returned, entry)) Review("Shutdown collection receipt does not match frozen settlement"); entry["collection_receipt"] = returned.Copy(); Save(); }
            return null;
        }
        Plan["stage"] = "DONE"; Plan["completed_at"] = state["observed_at"]?.DeepClone(); Save(); return null;
    }
    public void Acknowledge(JsonObject collected) { collected["applied"] = true; Save(); }
}
