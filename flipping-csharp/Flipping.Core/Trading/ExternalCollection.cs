using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

/// <summary>Explicit operator reconciliation of a sale collected outside the bot.</summary>
public static class ExternalCollection
{
    public static JsonObject? Plan(JsonObject p, JsonObject state, JsonObject config)
    {
        if (p.S("stage") != "STOPPED" || p.S("recovery_stage") != "WAIT_SELL"
            || p.S("account_key") == "" || p.S("account_key") != state.S("account_key")
            || !state.B("logged_in") || !state.B("ge_open") || state.B("input_busy")
            || p.S("storage_location", "INVENTORY") != "INVENTORY"
            || p["submission_cash_gp"] is null || p.L("owned_quantity") <= 0
            || p.L("owned_quantity") > p.L("acquired_quantity") || p.L("buy_spent") <= 0
            || p.L("sell_price") <= 0 || p.S("trade_id") == ""
            || Lifecycle.HasOffer(state, p.L("item_id"))
            || Lifecycle.CountItem(state, p.I("item_id")) != p.L("inventory_before")) return null;
        var quantity = p.L("owned_quantity");
        var netPerItem = p.L("sell_price") - MarketAnalyzer.Tax(p.L("sell_price"), p.I("item_id"), config);
        var proceeds = checked(quantity * netPerItem);
        // A balance coincidence alone never closes a trade: a separate explicit
        // human confirmation is required by Apply, and is retained in the journal.
        if (proceeds <= 0 || state.L("cash_inventory_gp") != checked(p.L("submission_cash_gp") + proceeds)) return null;
        return J.O(("trade_id", p.S("trade_id")), ("account_key", p.S("account_key")),
            ("item_id", p.L("item_id")), ("item_name", p.S("item_name")), ("sold_quantity", quantity),
            ("net_proceeds_gp", proceeds), ("previous_proceeds_gp", p.L("sale_proceeds_gp")),
            ("buy_spent_gp", p.L("buy_spent")), ("cash_inventory_gp", state.L("cash_inventory_gp")),
            ("inventory_before", p.L("inventory_before")), ("session", state.S("session")),
            ("proposed_at", state.L("observed_at")), ("source", "OPERATOR_CONFIRMATION_AND_EXACT_BALANCE"));
    }

    public static bool Apply(OrderManager manager, JsonObject confirmation, JsonObject state, JsonObject config)
    {
        if (!confirmation.B("confirmed") || confirmation.S("confirmation_id") == "")
            throw new InvalidOperationException("Elle tahsilat için açık kullanıcı doğrulaması gerekiyor.");
        var positions = manager.Positions.Where(p => p.S("trade_id") == confirmation.S("trade_id")).ToArray();
        if (positions.Length != 1) throw new InvalidOperationException("Uzlaştırılacak işlem tekil değil.");
        var p = positions[0];
        if (p.S("account_key") == "" || confirmation.S("account_key") != p.S("account_key") || state.S("account_key") != p.S("account_key"))
            throw new InvalidOperationException("Uzlaştırma doğrulaması mevcut hesaba ait değil.");
        if (p.S("stage") == "DONE" && p.Obj("external_collection_evidence").S("confirmation_id") == confirmation.S("confirmation_id")) return false;
        var plan = Plan(p, state, config);
        if (plan is null || state.L("observed_at") - confirmation.L("proposed_at") is < 0 or > 1800
            || !new[] { "trade_id", "account_key", "item_id", "sold_quantity", "net_proceeds_gp", "previous_proceeds_gp", "buy_spent_gp", "cash_inventory_gp", "inventory_before", "session" }
                .All(key => JsonNode.DeepEquals(plan[key], confirmation[key])))
            throw new InvalidOperationException("Uzlaştırma hesabı, bakiye veya eşya miktarı değişti; yeniden kontrol gerekiyor.");
        var evidence = confirmation.Copy(); evidence["applied_at"] = state.L("observed_at");
        p["external_collection_evidence"] = evidence;
        p["sale_proceeds_gp"] = checked(p.L("sale_proceeds_gp") + plan.L("net_proceeds_gp"));
        p["owned_quantity"] = 0; p["collection_remaining_quantity"] = 0;
        p["collection_expected_gp"] = null; p["collection_receipt"] = null; p["collection_offer"] = null;
        p["pending_sell_price"] = null; p["stage"] = "DONE"; p["recovery_stage"] = "";
        p["completed_at"] = state.L("observed_at");
        p["realized_profit_gp"] = p.L("sale_proceeds_gp") - p.L("buy_spent");
        p["last_risk_reason"] = "OPERATOR_CONFIRMED_SALE_COLLECTION";
        manager.Save(); return true;
    }

    public static JsonArray Reviews(OrderManager manager, JsonObject state, JsonObject config, JsonObject? margin = null)
    {
        var pending = manager.Positions.Where(p => p.S("stage") == "STOPPED").ToList();
        if (margin?.S("stage") == "STOPPED") pending.Add(margin);
        return J.A(pending.Select(p =>
        {
            var isMargin = ReferenceEquals(p, margin);
            var reason = p.Obj("manual_review").S("reason").Replace("MANUAL_MARGIN_", "");
            var row = J.O(("trade_id", isMargin ? "margin:" + p.S("account_key") + ":" + p.L("started_at") : p.S("trade_id")), ("item_id", p.L("item_id")), ("item_name", p.S("item_name") + (isMargin ? " (margin testi)" : "")),
                ("stage", p.S("recovery_stage")), ("expected_quantity", isMargin ? 1 : p.L("owned_quantity")),
                ("observed_inventory_quantity", Lifecycle.CountItem(state, p.I("item_id"))),
                ("reason", reason switch
                {
                    "OFFER_MISSING" => "Teklif elle toplanmış veya kaldırılmış olabilir; tahsilat kanıtı bekleniyor.",
                    "OFFER_CHANGED" => "Teklifin eşyası, fiyatı veya adedi değişti; bu teklife dokunulmayacak.",
                    "INVENTORY_CHANGED" => "Envanterdeki adet değişti; kayıtlı stok ve kâr korunarak doğrulama bekleniyor.",
                    "STOCK_CHANGED" => "Envanterdeki adet değişti; margin testinin sonucu doğrulanmalı.",
                    "UNEXPECTED_ITEM_OFFER" => "Kayıtlı eşya için elle teklif açıldı; bu teklif otomatik yönetilmeyecek.",
                    "COLLECTION_CHANGED" => "Tahsilat miktarı eşleşmedi; para/eşya farkı doğrulanana kadar bu işlem bekliyor.",
                    "PARTIAL_COLLECTION_OR_STOCK_CHANGE" => "Teklif açıkken eşya miktarı değişti; kısmi tahsilat veya stok hareketi doğrulanmalı.",
                    _ => "Teklif veya tahsilat kaydı eşleşmedi; otomatik tekrar yapılmadı."
                }));
            var plan = isMargin ? null : Plan(p, state, config); if (plan != null) row["manual_collection_plan"] = plan;
            return (JsonNode?)row;
        }));
    }
}
