using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public sealed class SimExchange : IExchange, IReceiptExchange
{
    readonly JsonObject config; readonly List<JsonObject> receipts = [];
    public string? Account { get; } = "simulation-" + Guid.NewGuid();
    public bool IsLive => false;
    public long Cash { get; set; }
    public long Now { get; set; } = 100000;
    public int Capacity { get; }
    public Dictionary<int, long> Inventory { get; } = [];
    public Dictionary<int, JsonObject> Offers { get; } = [];
    public SimExchange(JsonObject config, long cash = 2000000) { this.config = config; Cash = cash; Capacity = (int)config["simulation_slot_capacity"].L(8); }
    public JsonObject Observe() => J.O(("schema", 1), ("logged_in", true), ("ge_open", true), ("account_key", Account), ("session", "simulation"),
        ("sequence", Now), ("observed_at", Now), ("cash_inventory_gp", Cash), ("input_enabled", true), ("ge_slot_capacity", Capacity), ("account_members", Capacity == 8),
        ("inventory", J.A(Inventory.Where(v => v.Value != 0).Select(v => J.O(("id", v.Key), ("unnoted_id", v.Key), ("quantity", v.Value))))),
        ("offers", J.A(Enumerable.Range(0, Capacity).Select(s => Offers.TryGetValue(s, out var o) ? o.Copy() : J.O(("slot", s), ("state", "EMPTY"), ("item_id", 0), ("item_name", ""), ("price", 0), ("quantity", 0), ("filled", 0), ("spent", 0))))), ("widgets", new JsonArray()));
    public JsonObject Connection() => J.O(("connected", true), ("logged_in", true), ("ge_open", true), ("account_key", Account), ("input_enabled", true), ("simulation", true));
    public int Submit(string side, int itemId, string itemName, long quantity, long price)
    {
        if (Offers.Count >= Capacity || quantity <= 0 || price <= 0 || side is not ("BUY" or "SELL")) throw new InvalidOperationException("Invalid position");
        if (side == "BUY") { if (checked(quantity * price) > Cash) throw new InvalidOperationException("Insufficient cash"); Cash -= quantity * price; }
        else { if (Inventory.GetValueOrDefault(itemId) < quantity) throw new InvalidOperationException("Insufficient items"); Inventory[itemId] -= quantity; }
        var slot = Enumerable.Range(0, Capacity).First(s => !Offers.ContainsKey(s));
        Offers[slot] = J.O(("slot", slot), ("state", side == "BUY" ? "BUYING" : "SELLING"), ("item_id", itemId), ("item_name", itemName),
            ("quantity", quantity), ("price", price), ("filled", 0), ("spent", 0), ("opened_at", Now)); return slot;
    }
    public void Fill(int slot, long? quantity = null, long? price = null)
    {
        var offer = Offers[slot]; var buying = offer["state"].S() == "BUYING";
        if (!new[] { "BUYING", "SELLING" }.Contains(offer["state"].S())) throw new InvalidOperationException("Offer not active");
        var count = quantity ?? offer["quantity"].L() - offer["filled"].L(); var actual = price ?? offer["price"].L();
        if (count < 0 || count > offer["quantity"].L() - offer["filled"].L()) throw new InvalidOperationException("Invalid fill");
        if (buying && actual > offer["price"].L() || !buying && actual < offer["price"].L()) throw new InvalidOperationException("Fill violates offer price");
        offer["filled"] = offer["filled"].L() + count;
        offer["spent"] = offer["spent"].L() + count * (buying ? actual : actual - MarketAnalyzer.Tax(actual, (int)offer["item_id"].L(), config));
        if (offer["filled"].L() == offer["quantity"].L()) offer["state"] = buying ? "BOUGHT" : "SOLD"; Now++;
    }
    public void Cancel(int slot) { var o = Offers[slot]; if (o["state"].S() is "BUYING" or "SELLING") o["state"] = o["state"].S() == "BUYING" ? "CANCELLED_BUY" : "CANCELLED_SELL"; }
    public JsonObject CollectionAmounts(int slot)
    {
        var o = Offers[slot]; var buying = o["state"].S() is "BOUGHT" or "CANCELLED_BUY";
        return J.O(("cash_delta", buying ? o["quantity"].L() * o["price"].L() - o["spent"].L() : o["spent"].L()), ("item_delta", buying ? o["filled"].L() : o["quantity"].L() - o["filled"].L()));
    }
    public JsonObject? Collect(int slot)
    {
        var o = Offers[slot].Copy(); if (o["state"].S() is not ("BOUGHT" or "SOLD" or "CANCELLED_BUY" or "CANCELLED_SELL")) throw new InvalidOperationException("Only settled offers can be collected");
        var before = Observe(); var amounts = CollectionAmounts(slot); Cash += amounts["cash_delta"].L(); var id = (int)o["item_id"].L();
        Inventory[id] = Inventory.GetValueOrDefault(id) + amounts["item_delta"].L(); Offers.Remove(slot); Now++;
        var receipt = Lifecycle.CollectionReceipt(this, before, o, amounts); receipts.Add(receipt.Copy()); return receipt;
    }
    public IEnumerable<JsonObject> CollectionReceipts(bool includePending = false) => receipts.Select(r => r.Copy());
    public void ReconcilePendingCollections(JsonObject state) { }
    public void Overview() { }
    public void RequestStop() { }
    public void Resume() { }
}
