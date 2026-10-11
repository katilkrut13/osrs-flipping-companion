using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public static class EngineRules
{
    public static readonly HashSet<string> CollectionStages = ["BUY_COLLECTING", "SELL_COLLECTING", "PARTIAL_SELL_COLLECTING"];
    public static bool Active(JsonObject p) => p.S("stage") is not ("DONE" or "VOID");
    public static long CountItem(JsonObject state, int id) => state.Objects("inventory").Where(i => i.L("unnoted_id", i.L("id")) == id).Sum(i => i.L("quantity"));
    public static long BankCount(JsonObject state, int id) => state.Objects("bank_inventory").Where(i => i.L("id") == id && !i.B("placeholder")).Sum(i => i.L("quantity"));
    public static Portfolio ExposeCostBasis(Portfolio portfolio, OrderManager manager, MarginRunner runner)
    {
        long held = 0;
        foreach (var p in manager.Positions.Where(p => p.S("stage") is not ("DONE" or "VOID" or "WAIT_BUY" or "BUY_SUBMITTING") && p.L("buy_spent") > 0))
            held += (long)Math.Ceiling((decimal)p.L("buy_spent") * p.L("owned_quantity") / Math.Max(p.L("acquired_quantity"), 1));
        var t = runner.Test;
        if (t != null && t.S("stage") is not ("DONE" or "NEW" or "WAIT_BUY" or "BUY_SUBMITTING")) held += t.L("buy_spent");
        portfolio.DeployedCostGp = portfolio.Offers.Where(o => o.Side == "BUY").Sum(o => o.Quantity * o.Price) + held;
        return portfolio;
    }
    public static void SyncStorage(OrderManager manager, JsonObject state)
    {
        var changed = false;
        foreach (var p in manager.Positions.Where(p => Active(p) && p.L("owned_quantity") > 0 && p.S("account_key") == state.S("account_key")))
        {
            var inv = CountItem(state, p.I("item_id")); var bank = BankCount(state, p.I("item_id"));
            var expected = p.L("inventory_before") + p.L("owned_quantity");
            if (p.S("storage_location") == "BANK" && inv >= expected && bank < p.L("owned_quantity"))
            { p["storage_location"] = "INVENTORY"; p["last_risk_reason"] = "STORAGE_RECONCILED_INVENTORY"; changed = true; }
            else if (p.S("storage_location", "INVENTORY") == "INVENTORY" && bank >= p.L("owned_quantity") && inv < expected)
            { p["storage_location"] = "BANK"; p["last_risk_reason"] = "STORAGE_RECONCILED_BANK"; changed = true; }
        }
        if (changed) manager.Save();
    }
    public static bool PrepareBankStorageProbe(OrderManager manager, JsonObject state)
    {
        var p = manager.Positions.FirstOrDefault(p => p.S("stage") == "STOPPED" && p.S("recovery_stage") == "PAUSED_HELD"
            && p.S("storage_location", "INVENTORY") == "INVENTORY" && p.L("owned_quantity") > 0
            && p.S("account_key") == state.S("account_key") && p.Obj("manual_review").S("reason") == "INVENTORY_CHANGED"
            && CountItem(state, p.I("item_id")) < p.L("inventory_before") + p.L("owned_quantity")
            && !Lifecycle.HasOffer(state, p.L("item_id")));
        if (p is null) return false;
        p["stage"] = "PAUSED_HELD"; p["storage_location"] = "BANK";
        p["last_risk_reason"] = "VERIFYING_STOPPED_STOCK_IN_BANK"; p.Remove("manual_review");
        manager.Save(); return true;
    }
    public static JsonObject TrackedOfferState(OrderManager manager, MarginRunner runner, JsonObject state)
    {
        var copy = state.Copy();
        var tracked = manager.Positions.Concat(runner.Test == null ? [] : new[] { runner.Test })
            .Where(p => Active(p) && p.S("account_key") == state.S("account_key") && !(p.S("stage") == "STOPPED" && p["manual_review"] is JsonObject)).ToList();
        copy["offers"] = J.A(state.Objects("offers").Select(o =>
        {
            var matches = tracked.Any(p =>
            {
                var stage = p.S("stage") == "STOPPED" ? p.S("recovery_stage") : p.S("stage");
                var buying = stage is "WAIT_BUY" or "BUY_SUBMITTING" or "BUY_COLLECTING";
                var quantity = ReferenceEquals(p, runner.Test) ? 1 : p.L(buying ? "quantity" : "owned_quantity");
                return Lifecycle.MatchingOffer(o, Lifecycle.Slot(p), p.I("item_id"), quantity, p.L(buying ? "buy_price" : "sell_price"), buying);
            });
            return (JsonNode?)(matches ? o.Copy() : J.O(("slot", o.L("slot")), ("state", "EMPTY")));
        }));
        return copy;
    }
    public static bool UnexpectedManualBank(JsonObject state, JsonObject? transfer, long? cycleStarted)
        => state.B("bank_open") && !state.B("input_busy") && cycleStarted != null && state.L("observed_at") - cycleStarted > 5
            && (transfer == null || transfer.S("stage") is "DONE" or "REVIEW")
            && (transfer?.S("stage") != "DONE" || state.L("observed_at") - transfer.L("completed_at") > 5);
    public static bool AdoptMarginStock(MarginRunner runner, OrderManager manager, JsonObject state)
    {
        var t = runner.Test;
        if (t == null || t.S("stage") != "PAUSED_HELD" || t.S("storage_location", "INVENTORY") != "INVENTORY"
            || t.S("account_key") == "" || t.S("account_key") != state.S("account_key") || t.L("buy_spent") <= 0 || state.B("input_busy")) return false;
        var tradeId = $"margin-stock:{t.S("account_key")}:{t.L("started_at")}:{t.L("item_id")}";
        var existing = manager.Positions.FirstOrDefault(p => p.S("trade_id") == tradeId);
        if (existing != null)
        {
            if (existing.S("account_key") != t.S("account_key") || existing.L("item_id") != t.L("item_id")
                || existing.L("buy_spent") != t.L("buy_spent") || existing.L("acquired_quantity") != 1)
                throw new InvalidOperationException("Margin-stock transfer does not match its owned position");
        }
        else
        {
            if (CountItem(state, t.I("item_id")) != t.L("inventory_before") + 1
                || state.Objects("offers").Any(o => o.L("item_id") == t.L("item_id") && o.S("state") != "EMPTY")
                || manager.Positions.Any(p => Active(p) && p.L("item_id") == t.L("item_id"))) return false;
            manager.Positions.Add(J.O(("item_id", t.L("item_id")), ("item_name", t.S("item_name")), ("quantity", 1),
                ("buy_price", t.L("buy_spent")), ("sell_price", t.L("sell_price")), ("created_at", t.L("started_at")),
                ("started_at", t.L("started_at")), ("stage", "HELD"), ("buy_spent", t.L("buy_spent")),
                ("owned_quantity", 1), ("acquired_quantity", 1), ("inventory_before", t.L("inventory_before")),
                ("trade_id", tradeId), ("account_key", t.S("account_key")), ("storage_location", "INVENTORY"),
                ("last_risk_reason", "UNSOLD_MARGIN_STOCK")));
            manager.Save();
        }
        t["result"] = J.O(("item_id", t.L("item_id")), ("actual_buy_gp", t.L("buy_spent")), ("actual_net_sale_gp", 0),
            ("actual_test_loss_gp", 0), ("completed_at", state.L("observed_at")), ("verified", false), ("abandoned", true),
            ("held_stock_transferred", true), ("held_cost_basis_gp", t.L("buy_spent")), ("trade_id", tradeId));
        t["stage"] = "DONE"; runner.Save(); return true;
    }
    public static string? RiskReason(JsonObject daily, JsonObject config)
    {
        if (!config.B("monetary_limits_enabled", true)) return null;
        if (daily.L("net_gp") <= -config.L("max_daily_loss_gp", 100000)) return "DAILY_LOSS_LIMIT";
        return daily.L("margin_test_cost_gp") >= config.L("max_daily_test_cost_gp", 10000) ? "DAILY_TEST_COST_LIMIT" : null;
    }
    public static JsonObject WithOwnedExitQuotes(JsonObject? report, JsonObject? exits, long now)
    {
        var result = report?.Copy() ?? J.O(("candidates", new JsonArray()), ("flagged_items", new JsonArray()));
        if (exits == null || !exits["generated_at_epoch"].IsInteger() || now - exits.L("generated_at_epoch") is < 0 or > 90
            || exits["exit_quotes"] is not JsonArray) return result;
        result["exit_quotes"] = exits["exit_quotes"]!.DeepClone(); return result;
    }
    public static string Describe(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return "";
        return reason switch
        {
            "CYCLE_STOPPED" => "Durduruldu; BAŞLAT bekleniyor",
            "MANUAL_CONTROL_PAUSED" => "Elle yönetim açık; otomatik tıklamalar duraklatıldı. Devam için BAŞLAT",
            "USER_ACTIVITY_ACTIVE" => "Elle müdahale algılandı; otomatik girdiler bekliyor",
            "USER_ACTIVITY_VERIFYING_STATE" or "USER_ACTIVITY_RECONCILE_BEFORE_RESUME" => "Müdahale bitti; güncel teklifler doğrulanıyor",
            "USER_ACTIVITY_RETURN_TO_GE" => "Elle yönetim sürüyor; devam için GE teklif ekranına dön",
            "USER_ACTIVITY_FRESH_STATE_REQUIRED" => "Müdahale sonrası güncel oyun verisi bekleniyor",
            "USER_ACTIVITY_LOGIN_REQUIRED" => "Elle müdahale sonrası giriş bekleniyor",
            "USER_ACTIVITY_IDENTITY_REQUIRES_REVIEW" => "Hesap veya bağlantı değişti; yeniden bağlanma gerekiyor",
            "USER_ACTIVITY_INPUT_BUSY" or "USER_ACTIVITY_PENDING_INPUT" => "Kesilen girdinin tamamlanması doğrulanıyor; tekrar gönderilmiyor",
            "USER_ACTIVITY_CYCLE_STOPPED" => "Döngü durduruldu; kendiliğinden yeniden başlamaz",
            "MANUAL_REVIEW_OTHER_OFFERS_CONTINUE" => "Değişen işlem inceleme bekliyor; diğer bilinen teklifler yönetiliyor",
            "START_QUEUED_AFTER_STOP" => "Durdurma tamamlanınca başlayacak",
            "CANCELLING_AND_COLLECTING" => "Teklifler iptal edilip toplanıyor",
            "PARKING_ON_STOP" => "Kalan stok bankaya aktarılıyor",
            "BUY_LIMIT_HISTORY_UNVERIFIED" => "Alış geçmişi beyanı gerekiyor: Limit ve kayıt sekmesi",
            "RECOVERY_NEEDS_REVIEW" => "Yarım işlem mevcut tekliflerle doğrulanamadı",
            "WAITING_FOR_GAME_FOCUS" => "RuneLite odağı bekleniyor",
            "WAITING_FOR_MARKET_SCAN" => "Piyasa verisi bekleniyor",
            "WAITING_FOR_CONNECTION" => "Oyun bağlantısı bekleniyor",
            "WAITING_FOR_INPUT_COMPLETION" => "Önceki oyun girdisi tamamlanıyor",
            "WAITING_FOR_PROGRESS" => "İşlemin ilerlemesi bekleniyor",
            "INPUT_STALL_NEEDS_REVIEW" => "İşlem ilerlemedi; kontrol ve BAŞLAT bekleniyor",
            "LIVE_INPUT_DISABLED" => "RuneLite köprüsünde oyun girdisi etkin değil",
            "RECONCILING_DELAYED_OPERATION" => "Önceki işlemin sonucu doğrulanıyor",
            "REFRESHING_REJECTED_INPUT" => "Oyun girdisi uygulanmadı; ekran yenileniyor",
            "NO_ELIGIBLE_CANDIDATES" => "Yeni işlem koşullarını karşılayan eşya yok",
            "COLLECTING_POSITION" => "Tamamlanan teklif toplanıyor",
            "NEW_ENTRY_UNTRACKED_GE_OFFERS" => "Kayıtlarla eşleşmeyen GE teklifi var; yeni alışlar uzlaştırmayı bekliyor",
            "MANAGING_OFFERS" => "Açık teklifler izleniyor; piyasa dolumu bekleniyor",
            "SELECTING_NEXT_TRADE" => "Sonraki işlem seçiliyor",
            "DAILY_TEST_COST_LIMIT" => "Yeni alış test bütçesini bekliyor; mevcut teklifler yönetiliyor",
            "DAILY_LOSS_LIMIT" => "Yeni alış zarar sınırını bekliyor; mevcut teklifler yönetiliyor",
            "RETRYING_ITEM_ENTRY" => "Emir gönderilmediği doğrulandı; sınırlı tekrar hazırlanıyor",
            "NEW_ENTRY_ITEM_RETRY_COOLDOWN" => "Ürünün yeni alımları geçici olarak bekliyor",
            _ when reason.StartsWith("MARGIN_TEST_") => "Margin testi: " + reason[12..],
            _ => reason
        };
    }
}

public static class OperatorControl
{
    public static string? Read(string folder, string? boundAccount)
    {
        var path = Path.Combine(folder, "operator-command.json"); if (!File.Exists(path)) return null;
        var c = Storage.ReadJson(path).AsObject(); var receipt = Path.Combine(folder, "operator-receipt.json");
        if (File.Exists(receipt) && Storage.ReadJson(receipt).S("id") == c.S("id")) return null;
        string? reason = null; var now = J.Now;
        if (c.S("action") is not ("start" or "pause" or "baseline_untracked") || c.S("id") == "") reason = "UNSUPPORTED_COMMAND";
        else if (now - c.L("created_at") is < 0 or > 30) reason = "EXPIRED_COMMAND";
        else
        {
            JsonObject state;
            try { state = Storage.ReadSnapshot(Path.Combine(folder, "live-state.json"), now, false, false); }
            catch (Exception ex) when (ex is ConnectionUnavailable or IOException) { state = new(); }
            var account = state.B("logged_in") ? state.S("account_key") : boundAccount;
            if (string.IsNullOrEmpty(account) || account != c.S("account_key")) reason = "ACCOUNT_UNCONFIRMED";
            else if (c.S("action") is "start" or "baseline_untracked" && !state.B("logged_in")) reason = "LOGIN_REQUIRED";
        }
        Storage.WriteJson(receipt, J.O(("id", c.S("id")), ("status", reason == null ? "accepted" : "rejected"), ("reason", reason), ("at", now)));
        return reason == null ? c.S("action") : null;
    }
}
