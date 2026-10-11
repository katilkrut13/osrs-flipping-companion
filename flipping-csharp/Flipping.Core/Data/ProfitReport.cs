using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public static class ProfitReport
{
    static readonly string[] BuyStages = ["NEW", "BUY_SUBMITTING", "WAIT_BUY"];
    static readonly Dictionary<string, string> Labels = new()
    { ["NEW"]="Alış hazırlanıyor", ["BUY_SUBMITTING"]="Alış gönderiliyor", ["WAIT_BUY"]="Alış bekliyor", ["BUY_COLLECTING"]="Alış toplanıyor", ["SELL_SUBMITTING"]="Satış gönderiliyor", ["WAIT_SELL"]="Satış bekliyor", ["SELL_COLLECTING"]="Satış toplanıyor", ["PARTIAL_SELL_COLLECTING"]="Kısmi satış toplanıyor", ["HELD"]="Eşya elde", ["PAUSED_HELD"]="Duraklatıldı; eşya elde", ["STOPPED"]="Doğrulama bekliyor", ["DONE"]="Tamamlandı" };
    static readonly Dictionary<string, string> HeldLabels = new()
    { ["HELD_WAITING_FOR_FRESH_QUOTE"]="Elde; güncel satış fiyatı bekleniyor", ["HELD_QUOTE_BELOW_COST"]="Elde; satış fiyatı maliyeti karşılamıyor", ["HELD_STOCK_IN_BANK"]="Bankada; satış için çekilmeyi bekliyor", ["HELD_WAITING_FOR_FREE_SLOT"]="Elde; boş GE slotu bekleniyor", ["HELD_REPRICE_LIMIT_REACHED"]="Elde; fiyat düşürme sınırına ulaşıldı", ["HELD_MARKET_UNSUITABLE"]="Elde; satış koşulları uygun değil", ["STAGNANT_HELD"]="Elde; uygun satış fiyatı bekleniyor" };
    static readonly Dictionary<string, string> SellLabels = new()
    { ["STAGNANT_REPRICE"]="Satış durgun; fiyat güncelleniyor", ["STAGNANT_HELD"]="Satış durgun; uygun çıkış fiyatı bekleniyor", ["STAGNANT_WAITING_FOR_EXIT_PRICE"]="Satış açık; güncel/kârlı fiyat güncellemesi bekleniyor" };
    static long Quantity(JsonNode? n) => Math.Max(0, n.LongValue());
    static long At(JsonObject r) => new[] { "completed_at", "started_at", "created_at", "last_progress_at" }.Max(k => r[k].LongValue());
    static bool SameAccount(JsonObject r, string account, long now) => r["account_key"].S() == account && At(r) <= now;
    static string PositionKey(JsonObject p) => p["trade_id"].S() != "" ? p["trade_id"].S() : $"legacy:{p["item_id"]}:{p["created_at"]}:{p["slot"]}";
    static string MarginKey(JsonObject r) => $"{r["item_id"]}:{r["completed_at"]}";
    static JsonObject Aliases(JsonObject r)
    {
        r["totalbuy_gp"] = r["buy_cost_gp"]?.DeepClone();
        r["buy_average_gp"] = r["acquired_quantity"].L() > 0 ? (double)r["buy_cost_gp"].L() / r["acquired_quantity"].L() : null;
        r["netproceeds_gp"] = r["net_sale_proceeds_gp"]?.DeepClone(); r["profit_gp"] = r["realized_profit_gp"]?.DeepClone();
        r["sold_quantity_observed"] = r["sold_quantity"].L() + r["sale_uncollected_quantity"].L(); r["unsold_quantity"] = Math.Max(0, r["remaining_quantity"].L() - r["sale_uncollected_quantity"].L());
        var status = r["status"].S(); var label = Labels.GetValueOrDefault(status, status); var reasons = status == "HELD" ? HeldLabels : status == "WAIT_SELL" ? SellLabels : null;
        if (reasons != null) label = reasons.GetValueOrDefault(r["exit_reason"].S(), label);
        if (r["sale_uncollected_quantity"].L() > 0) label += " / satılan eşya için Collect bekliyor";
        if (r["unverified_sale_quantity"].L() > 0) label += " / Satış tahsilatı doğrulanmadı"; r["status_label"] = label; return r;
    }
    static JsonObject? MatchingOffer(JsonObject p, IEnumerable<JsonObject> offers, bool buying)
    {
        var quantity = Quantity(p[buying ? "quantity" : "owned_quantity"]); var price = p[buying ? "buy_price" : "sell_price"].L();
        var matches = offers.Where(o => (!o.ContainsKey("account_key") || o["account_key"].S() == p["account_key"].S())
            && Lifecycle.MatchingOffer(o, Lifecycle.Slot(p), (int)p["item_id"].L(), quantity, price, buying)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
    static JsonObject? PositionRow(JsonObject p, IEnumerable<JsonObject> offers)
    {
        var stage = p["stage"].StringValue("UNKNOWN");
        if (stage == "VOID" && p.B("profit_unknown") && p["external_completion"] is JsonObject)
            return J.O(("row_id", "position:" + PositionKey(p)), ("kind", "EXTERNAL_COMPLETION"), ("item_name", p.S("item_name")),
                ("status", "EXTERNALLY_COMPLETED"), ("status_label", "Elle tamamlandı; satış tutarı ve kâr bilinmiyor"),
                ("updated_at", At(p)), ("realized_profit_gp", null));
        if (stage == "VOID") return null;
        var buying = BuyStages.Contains(stage) || new[] { "STOPPED", "PAUSED_HELD" }.Contains(stage) && BuyStages.Contains(p["recovery_stage"].S());
        var buyOffer = buying ? MatchingOffer(p, offers, true) : null;
        var cost = Quantity(p["buy_spent"]); var acquired = Math.Max(Quantity(p["acquired_quantity"]), Quantity(p["owned_quantity"])); var remaining = Quantity(p["owned_quantity"]);
        if (buyOffer != null) { acquired = Math.Max(acquired, Quantity(buyOffer["filled"])); remaining = acquired; cost = Math.Max(cost, Quantity(buyOffer["spent"])); }
        if (acquired == 0 && !BuyStages.Contains(stage) && buyOffer == null || acquired > 0 && cost <= 0 || stage == "DONE" && p["completed_at"].L(-1) < 0) return null;
        if (stage == "DONE") remaining = 0; remaining = Math.Min(remaining, acquired); var sold = acquired - remaining;
        var proceeds = Quantity(p["sale_proceeds_gp"]); var recordedProfit = p["realized_profit_gp"].IsInteger() ? p["realized_profit_gp"].L() : (long?)null;
        if (stage == "DONE" && acquired > 0 && cost > 0 && proceeds == 0 && recordedProfit != null) { if (cost + recordedProfit < 0) return null; proceeds = cost + recordedProfit.Value; }
        if (stage == "DONE" && proceeds == 0 && recordedProfit == null) return null;
        var realizedCost = sold == acquired && acquired > 0 ? cost : acquired > 0 ? (long)((decimal)cost * sold / acquired) : 0;
        var unverified = sold > 0 && proceeds == 0 && stage != "DONE" ? sold : 0; if (unverified > 0) realizedCost = 0;
        if (sold == 0) proceeds = 0;
        var sellOffer = !buying && stage != "DONE" ? MatchingOffer(p, offers, false) : null; var pending = sellOffer != null ? Quantity(sellOffer["filled"]) : 0; long? pendingGp = null;
        if (stage is "SELL_COLLECTING" or "PARTIAL_SELL_COLLECTING")
        {
            var frozen = p["collection_offer"] as JsonObject;
            if (frozen != null && MatchingOffer(p, [frozen], false) != null) { pending = Quantity(frozen["filled"]); if (p["collection_expected_gp"].IsInteger() && p["collection_expected_gp"].L() >= 0) pendingGp = p["collection_expected_gp"].L(); }
        }
        var key = PositionKey(p);
        return Aliases(J.O(("row_id", "position:" + key), ("trade_id", key), ("kind", "POSITION"), ("item_id", p["item_id"].L()),
            ("item_name", p["item_name"].S() != "" ? p["item_name"].S() : p["item_id"]?.ToString() ?? ""), ("status", stage), ("exit_reason", p["last_risk_reason"].S()),
            ("slot", p["slot"]), ("started_at", p["started_at"] ?? p["created_at"]), ("completed_at", p["completed_at"]), ("updated_at", At(p)),
            ("planned_quantity", Quantity(p["quantity"])), ("acquired_quantity", acquired), ("sold_quantity", sold), ("remaining_quantity", remaining),
            ("buy_price_gp", Quantity(p["buy_price"])), ("sell_price_gp", Quantity(p["sell_price"])), ("buy_cost_gp", cost), ("net_sale_proceeds_gp", proceeds),
            ("realized_cost_gp", realizedCost), ("realized_profit_gp", p.B("profit_unknown") ? null : (object)(proceeds - realizedCost)), ("remaining_cost_gp", cost - realizedCost),
            ("pending_buy_quantity", buying ? Math.Max(0, Quantity(p["quantity"]) - acquired) : 0), ("sale_uncollected_quantity", Math.Min(remaining, pending)),
            ("sale_uncollected_gp", pendingGp), ("unverified_sale_quantity", p.B("profit_unknown") ? Math.Max(1, unverified) : unverified), ("storage_location", p["storage_location"].StringValue("INVENTORY")),
            ("cost_basis_source", "confirmed_purchase"), ("revenue_source", "collected_net_proceeds")));
    }
    static JsonObject? MarginRow(JsonObject r)
    {
        if (!r["verified"].B() || r["abandoned"].B() || r["transferred"].B() || r["held_stock_transferred"].B() || r["completed_at"] == null || Quantity(r["actual_buy_gp"]) <= 0 || r["actual_net_sale_gp"].L(-1) < 0) return null;
        var cost = Quantity(r["actual_buy_gp"]); var proceeds = Quantity(r["actual_net_sale_gp"]);
        return Aliases(J.O(("row_id", "margin:" + MarginKey(r)), ("trade_id", ""), ("kind", "MARGIN_TEST"), ("item_id", r["item_id"].L()),
            ("item_name", r["item_name"].S() != "" ? r["item_name"].S() : r["item_id"]?.ToString() ?? ""), ("status", "DONE"), ("slot", r["slot"]), ("started_at", r["started_at"]),
            ("completed_at", r["completed_at"]), ("updated_at", At(r)), ("planned_quantity", 1), ("acquired_quantity", 1), ("sold_quantity", 1), ("remaining_quantity", 0),
            ("buy_price_gp", cost), ("sell_price_gp", 0), ("buy_cost_gp", cost), ("net_sale_proceeds_gp", proceeds), ("realized_cost_gp", cost), ("realized_profit_gp", proceeds - cost),
            ("remaining_cost_gp", 0), ("pending_buy_quantity", 0), ("sale_uncollected_quantity", 0), ("sale_uncollected_gp", null), ("unverified_sale_quantity", 0),
            ("storage_location", ""), ("cost_basis_source", "actual_margin_purchase"), ("revenue_source", "collected_net_proceeds")));
    }
    static JsonObject? PendingMarginRow(JsonObject p, IEnumerable<JsonObject> offers)
    {
        if (p["stage"].S() is "DONE" or "VOID") return null; var surrogate = p.Copy(); var key = $"margin-pending:{p["item_id"]}:{p["started_at"]}";
        surrogate["trade_id"] = key; surrogate["quantity"] = 1; surrogate["acquired_quantity"] = Quantity(p["buy_spent"]) > 0 ? 1 : 0; surrogate["owned_quantity"] = surrogate["acquired_quantity"]?.DeepClone(); surrogate["sale_proceeds_gp"] = 0;
        if (p["stage"].S() == "SELL_COLLECTING") surrogate["collection_expected_gp"] = p["collection_expected_cash_gp"]?.DeepClone();
        var row = PositionRow(surrogate, offers); if (row != null) { row["kind"] = "MARGIN_TEST"; row["row_id"] = key; row["trade_id"] = ""; } return row;
    }
    public static JsonObject Build(string? account, long now, IEnumerable<JsonObject> positions, JsonObject? marginTest = null, IEnumerable<JsonObject>? offers = null,
        IEnumerable<JsonObject>? closedPositions = null, IEnumerable<JsonObject>? marginResults = null)
    {
        var rows = new List<JsonObject>(); var rawOffers = offers?.ToList() ?? [];
        if (!string.IsNullOrEmpty(account))
        {
            var chosen = new Dictionary<string, JsonObject>();
            foreach (var p in (closedPositions ?? []).Concat(positions)) if (SameAccount(p, account, now)) chosen[PositionKey(p)] = p;
            foreach (var p in chosen.Values) { var row = PositionRow(p, rawOffers); if (row != null) rows.Add(row); }
            var results = new Dictionary<string, JsonObject>();
            foreach (var result in marginResults ?? []) if (SameAccount(result, account, now) && MarginRow(result) != null) results[MarginKey(result)] = result;
            if (marginTest != null && SameAccount(marginTest, account, now))
            {
                if (marginTest["stage"].S() == "DONE" && marginTest["result"] is JsonObject result)
                {
                    var enriched = result.Copy(); enriched["account_key"] = account; enriched["item_name"] = marginTest["item_name"]?.DeepClone(); enriched["started_at"] = marginTest["started_at"]?.DeepClone();
                    if (SameAccount(enriched, account, now) && MarginRow(enriched) != null) results[MarginKey(enriched)] = enriched;
                }
                else { var row = PendingMarginRow(marginTest, rawOffers); if (row != null) rows.Add(row); }
            }
            foreach (var result in results.Values) { var row = MarginRow(result); if (row != null) rows.Add(row); }
        }
        rows = rows.OrderByDescending(r => r["updated_at"].L()).ThenByDescending(r => r["row_id"].S(), StringComparer.Ordinal).ToList();
        var trades = rows.Where(r => r["kind"].S() == "POSITION").ToList(); var tests = rows.Where(r => r["kind"].S() == "MARGIN_TEST").ToList(); var totals = new JsonObject();
        foreach (var key in new[] { "buy_cost_gp", "net_sale_proceeds_gp", "realized_cost_gp", "remaining_cost_gp", "acquired_quantity", "sold_quantity", "remaining_quantity", "sale_uncollected_quantity" }) totals[key] = rows.Sum(r => r[key].L());
        totals["position_profit_gp"] = trades.Sum(r => r["realized_profit_gp"].L()); totals["margin_test_profit_gp"] = tests.Sum(r => r["realized_profit_gp"].L()); totals["net_profit_gp"] = rows.Sum(r => r["realized_profit_gp"].L());
        totals["closed_position_count"] = trades.Count(r => r["status"].S() == "DONE"); totals["active_position_count"] = trades.Count(r => r["status"].S() != "DONE"); totals["margin_test_count"] = tests.Count(r => r["status"].S() == "DONE");
        totals["total_position_profit_gp"] = totals["position_profit_gp"]?.DeepClone(); totals["net_margin_test_profit_gp"] = totals["margin_test_profit_gp"]?.DeepClone(); totals["combined_net_profit_gp"] = totals["net_profit_gp"]?.DeepClone(); totals["inventory_cost_gp"] = totals["remaining_cost_gp"]?.DeepClone();
        return J.O(("account_scope", "current_account_only"), ("account_key", account), ("generated_at", now), ("rows", J.A(rows)), ("totals", totals), ("note", "Profit uses collected net proceeds; uncollected fills are shown separately."));
    }
}
