using System.Text.Json.Nodes;
using CommonStorage = OsrsFlipping.Core.Storage;

namespace OsrsFlipping.Core;

public sealed class SessionController
{
    readonly IBridgeExchange backend;
    readonly JsonObject config;
    readonly string? journalPath;
    readonly Func<string, string?> pinProvider;
    double lastLoginAttempt, lastNavigationAction;
    int pinIndex;
    double? pinFinishedAt;
    public JsonObject? Task { get; private set; }
    public bool Pending => Task is not null && Task.S("stage") is not ("DONE" or "REVIEW");
    public string? Reason { get; private set; }
    public long? BankGpObserved { get; set; }
    static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
    public SessionController(IBridgeExchange backend, JsonObject config, string? journalPath = null, Func<string, string?>? pinProvider = null)
    {
        this.backend = backend; this.config = config; this.journalPath = journalPath;
        this.pinProvider = pinProvider ?? (account => backend is LiveExchange live ? Credentials.LoadBankPin(Path.Combine(live.Folder, "bank-pin.protected.json"), account) : null);
        if (journalPath is not null && File.Exists(journalPath)) Task = CommonStorage.ReadJson(journalPath) as JsonObject ?? throw new InvalidDataException("Invalid bank transfer journal");
    }
    public static long BankCount(JsonObject state, int itemId) => state.Objects("bank_inventory").Where(i => i.L("id") == itemId && !i.B("placeholder")).Sum(i => i.L("quantity"));
    public void Save() { if (journalPath is not null && Task is not null) CommonStorage.WriteJson(journalPath, Task); }
    void Click(JsonObject state, JsonObject widget) => backend.Send(state, J.O(("type", "click"), ("widget_key", widget.S("key"))));
    public void CloseInterface(JsonObject state)
    {
        var choices = state.Objects("widgets").Where(w => (w.S("symbol").StartsWith("GeOffers.") || w.S("symbol").StartsWith("Bankmain.")) && ProtocolHelpers.PrimaryAction(w) == "close").ToArray();
        if (choices.Length != 1) throw new InvalidOperationException("No unique observed interface close target"); Click(state, choices[0]);
    }
    public bool EnsureLoggedIn(JsonObject state)
    {
        Reason = null;
        if (Task?.S("stage") == "REVIEW") { Reason = "BANK_TRANSFER_REQUIRES_REVIEW"; return false; }
        if (!state.B("session_control_supported")) { Reason = "UPDATED_BRIDGE_REQUIRED"; return false; }
        if (!state.B("logged_in"))
        {
            if (config.B("saved_session_login", true) && state.S("game_state") == "LOGIN_SCREEN" && state.L("login_index") == 10 && Now - lastLoginAttempt >= 30)
            { lastLoginAttempt = Now; backend.Send(state, J.O(("type", "login_saved"))); Reason = "SAVED_SESSION_LOGIN_PENDING"; }
            else Reason = "LOGIN_REQUIRED";
            return false;
        }
        if (state.B("bank_pin_open"))
        {
            var pin = pinProvider(state.S("account_key"));
            if (string.IsNullOrEmpty(pin)) { Reason = "BANK_PIN_REQUIRES_USER"; return false; }
            if (pinIndex >= 4) { Reason = Now - (pinFinishedAt ?? 0) > 10 ? "BANK_PIN_REJECTED_REQUIRES_USER" : "VERIFYING_BANK_PIN"; return false; }
            var wanted = pin[pinIndex].ToString();
            var children = state.Objects("widgets").Where(w => (w.L("id") >> 16) == 213 && w.L("index") == 1 && ProtocolHelpers.Plain(w.S("text")) == wanted).Select(w => w.L("id")).ToHashSet();
            var digits = state.Objects("widgets").Where(w => children.Contains(w.L("id")) && w.L("index") == 0 && w.Arr("actions").Any(a => a?.ToString() == "Select")).ToArray();
            if (digits.Length == 0) digits = state.Objects("widgets").Where(w => (w.L("id") >> 16) == 213 && ProtocolHelpers.Plain(w.S("text")) == wanted).ToArray();
            if (digits.Length != 1) { Reason = "BANK_PIN_LAYOUT_REQUIRES_REVIEW"; return false; }
            try { backend.Send(state, J.O(("type", "bank_pin"), ("widget_key", digits[0].S("key")))); }
            catch { pinIndex = 4; pinFinishedAt = Now - 11; throw; }
            pinIndex++; if (pinIndex == 4) pinFinishedAt = Now; Reason = "ENTERING_SAVED_BANK_PIN"; return false;
        }
        if (pinIndex > 0 && state.B("bank_open")) { pinIndex = 0; pinFinishedAt = null; }
        if (state.B("security_prompt_open")) { Reason = "BANK_PIN_REQUIRES_USER"; return false; }
        if (state.B("welcome_open"))
        {
            var choices = state.Objects("widgets").Where(w => w.S("symbol") == "WelcomeScreen.PLAY" && ProtocolHelpers.PrimaryAction(w) == "play").ToArray();
            if (choices.Length != 1) { Reason = "WELCOME_LAYOUT_REQUIRES_REVIEW"; return false; }
            Click(state, choices[0]); Reason = "ENTERING_GAME"; return false;
        }
        return true;
    }
    public bool EnsureGe(JsonObject state)
    {
        if (!EnsureLoggedIn(state)) return false;
        if (state.B("input_busy")) { Reason = "WAITING_FOR_INPUT_COMPLETION"; return false; }
        if (state.B("ge_open")) return true;
        if (Now - lastNavigationAction < 3) { Reason = "WAITING_FOR_NAVIGATION"; return false; }
        lastNavigationAction = Now;
        if (state.B("bank_open")) { CloseInterface(state); Reason = "CLOSING_BANK"; return false; }
        if (state.Objects("npc_targets").Any(n => n.S("name").Equals("grand exchange clerk", StringComparison.OrdinalIgnoreCase) && n.L("distance") <= 32))
        { backend.NpcAction(state, "Exchange"); Reason = "OPENING_GE"; return false; }
        if (state.B("in_wilderness")) { Reason = "GE_ROUTE_UNAVAILABLE_IN_WILDERNESS"; return false; }
        var rings = state.Objects("inventory").Where(i => i.S("name").Contains("ring of wealth", StringComparison.OrdinalIgnoreCase) && Enumerable.Range(1, 5).Any(n => i.S("name").Contains($"({n})"))).Select(i => i.L("id")).ToHashSet();
        var options = state.Objects("widgets").Where(w => (w.S("symbol").StartsWith("Chatmenu.") || (w.L("id") >> 16) is 187 or 219) && ProtocolHelpers.Plain(w.S("text")).Contains("grand exchange", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (options.Length == 1) { Click(state, options[0]); Reason = "TELEPORTING_TO_GE"; return false; }
        var candidates = state.Objects("widgets").Where(w => rings.Contains(w.L("item_id")) && w.S("symbol").StartsWith("Inventory.")).ToArray();
        if (candidates.Length == 1 && config.B("ge_ring_teleport", true)) { backend.WidgetAction(state, candidates[0], "Rub"); Reason = "CHOOSING_GE_TELEPORT"; return false; }
        Reason = "GE_ROUTE_REQUIRES_RING_OR_NEARBY_CLERK"; return false;
    }
    public bool StartTransfer(JsonObject state, string kind, int itemId, long quantity, string purpose, string? tradeId = null)
    {
        if (Task?.S("stage") == "REVIEW") throw new InvalidOperationException("Resolve uncertain bank transfer before a new task");
        if (Pending) return false;
        if (kind is not ("WITHDRAW" or "DEPOSIT") || quantity <= 0 || quantity > config.L("budget_gp")) throw new ArgumentException("Bank transfer quantity outside policy");
        Task = J.O(("account_key", state.S("account_key")), ("kind", kind), ("item_id", itemId), ("quantity", quantity), ("purpose", purpose), ("trade_id", tradeId), ("stage", "OPEN_BANK"), ("started_at", state.L("observed_at")));
        Save(); return true;
    }
    JsonObject Complete(JsonObject state, bool skipped = false, string? reason = null)
    {
        Task!["stage"] = "DONE"; Task["completed_at"] = state.L("observed_at");
        if (skipped) Task["skipped"] = true; if (reason is not null) Task["skip_reason"] = reason;
        Save(); return Task.Copy();
    }
    public JsonObject? StepTransfer(JsonObject state)
    {
        if (!Pending || !EnsureLoggedIn(state)) return null;
        var t = Task!; var itemId = (int)t.L("item_id");
        if (state.S("account_key") != t.S("account_key")) { t["stage"] = "REVIEW"; Save(); throw new InvalidOperationException("Bank task account changed"); }
        if (state.B("input_busy")) return null;
        if (!state.B("bank_open"))
        {
            if (Now - lastNavigationAction < 3) { Reason = "WAITING_FOR_BANK"; return null; }
            lastNavigationAction = Now; if (state.B("ge_open")) CloseInterface(state); else backend.NpcAction(state, "Bank"); Reason = "OPENING_BANK"; return null;
        }
        var currentInv = ProtocolHelpers.InventoryQuantity(state, itemId); var currentBank = BankCount(state, itemId); BankGpObserved = BankCount(state, 995);
        if (t.S("stage") is "OPEN_BANK" or "SELECT")
        {
            var available = t.S("kind") == "WITHDRAW" ? currentBank : currentInv;
            if (t.S("purpose") == "TOP_UP_WORKING_BUDGET" && t.S("kind") == "WITHDRAW")
            { t["quantity"] = Math.Min(t.L("quantity"), available); if (t.L("quantity") == 0) return Complete(state, true); }
            if (available < t.L("quantity")) { Reason = "BANK_STOCK_UNAVAILABLE"; return Complete(state, true, "PRE_SUBMISSION_STOCK_UNAVAILABLE"); }
            if (t.S("kind") == "WITHDRAW" && itemId != 995 && !state.B("bank_note_mode"))
            {
                var notes = state.Objects("widgets").Where(w => w.S("symbol") == "Bankmain.NOTE" && w.Arr("actions").Count > 0).ToArray();
                if (notes.Length != 1) throw new InvalidOperationException("Cannot confirm banknote withdrawal mode"); Click(state, notes[0]); Reason = "SETTING_NOTE_WITHDRAWAL"; return null;
            }
            var withdrawing = t.S("kind") == "WITHDRAW"; var prefix = withdrawing ? "Bankmain." : "Bankside.";
            var actualIds = withdrawing ? new HashSet<long> { itemId } : state.Objects("inventory").Where(i => i.L("unnoted_id", i.L("id")) == itemId).Select(i => i.L("id")).ToHashSet();
            var action = withdrawing ? "Withdraw-X" : "Deposit-X";
            var targets = state.Objects("widgets").Where(w => w.S("symbol").StartsWith(prefix) && actualIds.Contains(w.L("item_id")) && w.Arr("actions").Any(a => a?.ToString() == action)).ToArray();
            if (targets.Length != 1) throw new InvalidOperationException("No unique observed bank transfer target");
            t["stage"] = "PROMPT"; t["inventory_before"] = currentInv; t["bank_before"] = currentBank; Save(); backend.WidgetAction(state, targets[0], action); return null;
        }
        var sign = t.S("kind") == "WITHDRAW" ? 1 : -1;
        var complete = currentInv == t.L("inventory_before") + sign * t.L("quantity") && currentBank == t.L("bank_before") - sign * t.L("quantity");
        if (t.S("stage") is "ENTERING" or "VERIFY" && complete) { BankGpObserved = BankCount(state, 995); return Complete(state); }
        if (t.S("stage") == "PROMPT")
        {
            if (currentInv != t.L("inventory_before") || currentBank != t.L("bank_before")) { t["stage"] = "REVIEW"; Save(); throw new InvalidOperationException("Balances changed before bank amount input"); }
            var prompt = state.Objects("widgets").Any(w => w.S("symbol").StartsWith("Chatbox.MES_TEXT") && new[] { "how many", "amount", "quantity" }.Any(s => ProtocolHelpers.Plain(w.S("text")).Contains(s, StringComparison.OrdinalIgnoreCase)));
            if (state.L("input_type") == 0 || !prompt) { Reason = "WAITING_BANK_AMOUNT_PROMPT"; return null; }
            t["stage"] = "ENTERING"; t["submitted_at"] = state.L("observed_at"); Save(); backend.Send(state, J.O(("type", "text"), ("text", t.L("quantity").ToString(System.Globalization.CultureInfo.InvariantCulture)), ("enter", true))); t["stage"] = "VERIFY"; Save(); return null;
        }
        if (t.S("stage") is "ENTERING" or "VERIFY")
        {
            if (state.L("observed_at") - t.L("submitted_at", t.L("started_at")) > 60) { t["stage"] = "REVIEW"; Save(); throw new InvalidOperationException("Bank transfer outcome unconfirmed"); }
            Reason = "VERIFYING_BANK_TRANSFER";
        }
        return null;
    }
    public bool Reconcile(JsonObject state)
    {
        if (!Pending) return true;
        if (Task!.S("account_key") != state.S("account_key")) return false;
        return Task.S("stage") is "ENTERING" or "VERIFY" ? StepTransfer(state) is not null : Task.S("stage") is "OPEN_BANK" or "SELECT" or "PROMPT";
    }
    public JsonObject? ResolveCompletedReview(JsonObject state)
    {
        var t = Task; if (t is null || t.S("stage") != "REVIEW" || state.B("input_busy") || !state.B("bank_open") || t.S("account_key") != state.S("account_key")) return null;
        var inventory = ProtocolHelpers.InventoryQuantity(state, (int)t.L("item_id")); var bank = BankCount(state, (int)t.L("item_id")); var quantity = t.L("quantity");
        if (t.L("submitted_at") == 0)
        {
            if (t.S("kind") == "WITHDRAW" && inventory >= quantity || t.S("kind") == "DEPOSIT" && bank >= quantity || t.S("kind") == "WITHDRAW" && bank < quantity && inventory < quantity || t.S("kind") == "DEPOSIT" && inventory < quantity && bank < quantity)
                return Complete(state, true, "PRE_SUBMISSION_REVIEW_RECONCILED");
            return null;
        }
        var sign = t.S("kind") == "WITHDRAW" ? 1 : -1;
        if (inventory != t.L("inventory_before") + sign * quantity || bank != t.L("bank_before") - sign * quantity) return null;
        t["reconciled_after_review"] = true; return Complete(state);
    }
}
