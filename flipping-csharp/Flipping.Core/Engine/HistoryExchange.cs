using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public sealed class HistoryExchange : IExchange
{
    private readonly string path;
    public string? Account { get; }
    public bool IsLive => false;
    public Portfolio Portfolio { get; private set; } = new();
    public HistoryExchange(string? path = null)
    {
        if (path == null)
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".runelite", "flipping");
            var paths = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").Where(p => Path.GetFileName(p) != "accountwide.json").ToArray() : [];
            if (paths.Length != 1) throw new InvalidOperationException("Tek bir Flipping Utilities hesap dosyası seçilmeli.");
            path = paths[0];
        }
        this.path = path; Account = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileNameWithoutExtension(path))));
    }
    public JsonObject Observe()
    {
        var data = Storage.ReadJson(path).AsObject(); if (data.L("version") != 1) throw new InvalidDataException("Unsupported history file version");
        var names = data.Objects("trades").GroupBy(t => t.I("id")).ToDictionary(g => g.Key, g => g.Last().S("name", g.Key.ToString()));
        var offers = new List<Offer>(); var history = new List<JsonNode?>();
        foreach (var pair in data.Obj("lastOffers"))
        {
            if (pair.Value is not JsonObject raw || raw.S("st") == "EMPTY" || raw.L("tQIT") <= 0) continue;
            offers.Add(new(int.Parse(pair.Key), raw.I("id"), names.GetValueOrDefault(raw.I("id"), raw.I("id").ToString()), raw.B("b") ? "BUY" : "SELL",
                raw.L("p"), raw.L("tQIT"), raw.L("cQIT"), raw.L("tradeStartedAt", raw.L("t")) / 1000));
        }
        foreach (var trade in data.Objects("trades")) foreach (var raw in trade.Obj("h").Objects("sO"))
            history.Add(J.O(("item_id", raw.I("id")), ("item_name", trade.S("name")), ("side", raw.B("b") ? "BUY" : "SELL"),
                ("price_gp", raw.L("p")), ("filled_quantity", raw.L("cQIT")), ("state", raw.S("st")), ("at_epoch", raw.L("t") / 1000)));
        Portfolio = new() { ObservedAt = data.L("lastStoredAt") / 1000, Offers = offers };
        return J.O(("schema", 1), ("observed_at", J.Now), ("account_key", Account), ("history", J.A(history.TakeLast(100))),
            ("offers", new JsonArray()), ("cash_inventory_gp", 0), ("inventory", new JsonArray()), ("widgets", new JsonArray()),
            ("history_updated_at", Portfolio.ObservedAt), ("input_enabled", false), ("logged_in", false), ("ge_open", false));
    }
    public JsonObject Connection() => Observe();
    public int Submit(string side, int itemId, string itemName, long quantity, long price) => throw new InvalidOperationException("History mode cannot submit orders");
    public void Cancel(int slot) => throw new InvalidOperationException("History mode cannot cancel orders");
    public JsonObject? Collect(int slot) => throw new InvalidOperationException("History mode cannot collect orders");
    public JsonObject CollectionAmounts(int slot) => throw new InvalidOperationException("History mode cannot collect orders");
    public void Overview() { }
    public void RequestStop() { }
    public void Resume() { }
}

public static class DemoData
{
    public static JsonObject Report(long now)
    {
        var c = J.O(("id", 882), ("name", "Bronze arrow"), ("indicative_buy_gp", 5), ("indicative_sell_gp", 7),
            ("estimated_net_margin_gp", 2), ("estimated_test_loss_gp", 2), ("buy_limit", 11000), ("quantity_proxy", 1000),
            ("risk", "Low"), ("velocity", "Ultra-Fast"), ("strategy_eligible", true), ("strategy_reasons", J.A("SIMULATION")),
            ("stress_net_margin_gp", 1), ("category", "AMMUNITION"), ("hourly_volume", 500000), ("capital_turnover_per_hour_proxy", 4),
            ("price_age_seconds", 0), ("expected_hold_minutes_proxy", 5), ("strategy_style", "FAST_CONSUMABLE"), ("selection_score", 10000));
        return J.O(("mode", "SIMULATION"), ("generated_at_epoch", now), ("candidates", J.A((JsonNode)c)), ("flagged_items", new JsonArray()));
    }
}
