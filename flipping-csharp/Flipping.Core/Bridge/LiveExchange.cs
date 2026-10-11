using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommonStorage = OsrsFlipping.Core.Storage;

namespace OsrsFlipping.Core;

public sealed class LiveExchange : IBridgeExchange, IReceiptExchange, IOverviewCollector, IDisposable
{
    readonly ManualResetEventSlim stop = new(false);
    readonly Ledger? ledger;
    readonly InputPacing pacing;
    readonly MotionTiming motion;
    readonly bool backgroundInput;
    readonly ManualCollectionObserver manualCollections;
    bool collectionInputActive;
    string? detailViewCache;
    bool confirmDispatched;
    string submissionPhase = "OVERVIEW";
    volatile JsonObject? pendingContext;
    public string Folder { get; }
    public JsonObject Config { get; }
    public string? Account { get; private set; }
    public bool IsLive => true;
    public string? PendingId { get; private set; }
    public JsonObject ExecutionPhase { get; private set; }
    public Func<bool>? UserInputBlocked { get; set; }
    public JsonObject? InterruptedSubmission { get; private set; }
    public double ClickPauseSeconds => pacing.ClickBase;
    public int TypingKeyDelayMs => pacing.KeyBase;
    static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
    public LiveExchange(string folder, Ledger? ledger = null, double? clickPauseSeconds = null, Random? pacingRng = null)
    {
        Folder = Path.GetFullPath(folder); this.ledger = ledger;
        var path = Path.Combine(Folder, "config.json"); Config = File.Exists(path) ? (JsonObject)CommonStorage.ReadJson(path) : new();
        manualCollections = new ManualCollectionObserver(Config);
        pacing = new InputPacing(Config, pacingRng, clickPauseSeconds); motion = new MotionTiming(Config, pacing.Rng, clickPauseSeconds.HasValue);
        backgroundInput = Config.B("background_input", true);
        ExecutionPhase = J.O(("phase", null), ("active", false), ("started_at", null), ("command_id", null), ("last_progress_at", null));
    }
    public void RequestStop() { stop.Set(); CancelPending(); }
    // Hook callbacks only set this signal; file I/O stays on the account worker.
    public void InterruptForUser() => stop.Set();
    public void Resume() => stop.Reset();
    public void Dispose() => stop.Dispose();
    void Pause(double seconds)
    {
        ThrowIfUserInput();
        if (stop.Wait(TimeSpan.FromSeconds(Math.Max(0, seconds)))) { ThrowIfUserInput(); throw new InvalidOperationException("Stopped"); }
        ThrowIfUserInput();
    }
    void ThrowIfUserInput() { if (UserInputBlocked?.Invoke() == true) throw new UserIntervention(); }
    JsonObject Snapshot(bool requireGe = true, bool requireLoggedIn = true) => CommonStorage.ReadSnapshot(Path.Combine(Folder, "live-state.json"), requireGe: requireGe, requireLoggedIn: requireLoggedIn);
    public JsonObject Observe()
    {
        var state = Snapshot(); Account ??= state.S("account_key");
        if (state.S("account_key") != Account) throw new InvalidOperationException("Account changed; stop and reconnect");
        ObserveManualCollections(state); ledger?.Ingest(state); return state;
    }
    public JsonObject Connection()
    {
        var state = Snapshot(false, false);
        if (state.B("logged_in") && state.S("account_key") != "")
        {
            Account ??= state.S("account_key"); if (state.S("account_key") != Account) throw new InvalidOperationException("Account changed; reconnect requires review"); ledger?.Ingest(state);
        }
        ObserveManualCollections(state);
        return state;
    }
    void ObserveManualCollections(JsonObject state)
    {
        if (collectionInputActive) { manualCollections.Reset(state); return; }
        var observed = manualCollections.Observe(state); if (observed.Count == 0) return;
        var existing = CollectionReceipts(true).ToArray();
        foreach (var receipt in observed)
        {
            var raw = receipt.Obj("offer");
            var matches = existing.Where(r => ProtocolHelpers.Same(r, receipt, "account_key", "session")
                && ProtocolHelpers.Same(r.Obj("offer"), raw, "slot", "item_id", "quantity", "price", "filled", "spent", "state")
                && (r.S("status") == "PREPARED" || r.L("observed_at") >= receipt.L("collect_started_at"))).ToArray();
            if (matches.Any(r => r.S("status") == "COMPLETED") || matches.Length > 1) continue;
            if (matches.Length == 1)
            {
                var pending = matches[0];
                if (pending.S("status") != "PREPARED" || pending.L("cash_before_gp") != receipt.L("cash_before_gp") || pending.L("item_before") != receipt.L("item_before")
                    || pending.L("expected_cash_delta") != receipt.L("cash_delta") || pending.L("expected_item_delta") != receipt.L("item_delta")) continue;
                receipt["receipt_id"] = pending["receipt_id"]?.DeepClone();
                receipt["expected_cash_delta"] = pending["expected_cash_delta"]?.DeepClone(); receipt["expected_item_delta"] = pending["expected_item_delta"]?.DeepClone();
            }
            SaveCollectionReceipt(receipt);
        }
    }
    public void CancelPending()
    {
        var context = pendingContext; if (context is null) return;
        try
        {
            var session = context.S("session"); var id = context.S("id");
            if (!string.IsNullOrEmpty(session) && id != "") CommonStorage.WriteJson(Path.Combine(Folder, "input-cancel.json"), J.O(("id", id), ("session", session)));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ConnectionUnavailable or UnauthorizedAccessException or JsonException) { }
    }
    public bool TrySettleInterruptedInput(JsonObject state)
    {
        if (PendingId is null) return true;
        JsonObject? receipt;
        try { receipt = CommonStorage.ReadJson(Path.Combine(Folder, "input-receipt.json")) as JsonObject; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return false; }
        if (!InterruptedInput.CanRelease(PendingId, pendingContext, receipt, state, Now)) return false;
        var evidence = J.O(("command_id", PendingId), ("status", receipt!.S("status")), ("session", state["session"]),
            ("observed_sequence", state["sequence"]), ("observed_at", state["observed_at"]), ("outcome_verified", false));
        CommonStorage.WriteJson(Path.Combine(Folder, "last-interrupted-input.json"), evidence);
        PendingId = null; pendingContext = null; ExecutionPhase["active"] = false; ExecutionPhase["last_progress_at"] = Now;
        ExecutionPhase["interruption_transport"] = evidence; detailViewCache = null;
        return true;
    }
    public JsonObject InputPacingStatus()
    {
        var result = pacing.Status(); foreach (var p in motion.Status()) result[p.Key] = p.Value?.DeepClone(); return result;
    }
    public void WidgetAction(JsonObject state, JsonObject widget, string action) => Send(state, J.O(("type", "widget_action"), ("widget_key", widget.S("key")), ("action", action)));
    public void NpcAction(JsonObject state, string action)
    {
        var name = action == "Bank" ? "Banker" : "Grand Exchange Clerk";
        var target = state.Objects("npc_targets").Where(n => n.S("name").Equals(name, StringComparison.OrdinalIgnoreCase) && n.Arr("actions").Any(a => a?.ToString() == action) && n.L("distance") <= 32).OrderBy(n => n.L("distance")).FirstOrDefault();
        if (target is null) throw new InvalidOperationException("No observed reachable " + name);
        Send(state, J.O(("type", "npc"), ("npc_index", target.L("index")), ("action", action)));
    }
    JsonObject Wait(Func<JsonObject, bool> predicate, double timeout = 12, bool connection = false)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < timeout)
        {
            ThrowIfUserInput(); if (stop.IsSet) throw new InvalidOperationException("Stopped");
            var state = connection ? Connection() : Observe(); if (predicate(state)) return state; Pause(.15);
        }
        throw new TimeoutException(connection ? "Session transition unconfirmed" : "Game transition not confirmed; no automatic retry");
    }
    void PhasePause(string phase) => Pause(pacing.PhaseDelaySeconds(phase));
    public void Send(JsonObject state, JsonObject command) => SendInput(state, command, false);
    void SendInput(JsonObject state, JsonObject command, bool confirmsOffer)
    {
        ThrowIfUserInput();
        command = command.Copy();
        if (command.S("type") == "text")
        {
            var pause = pacing.TypingStartDelaySeconds();
            if (pause > 0)
            {
                Pause(pause); var fresh = state.B("session_control_supported") ? Connection() : Observe();
                if (!ProtocolHelpers.Same(fresh, state, "session", "account_key", "input_type") || ProtocolHelpers.TextPromptSignature(fresh) != ProtocolHelpers.TextPromptSignature(state))
                    throw new TransientUIError("Text input context changed before typing; no input sent");
                state = fresh;
            }
        }
        ThrowIfUserInput(); if (stop.IsSet || !state.B("input_enabled")) throw new InvalidOperationException("Live input disabled or stopped");
        if (state.Objects("inventory").Any(i => i.L("id") == 13204 && i.L("quantity") > 0)) throw new InvalidOperationException("Platinum tokens present; GP-only reconciliation cannot verify currency conversion");
        double timeout = 12;
        if (backgroundInput && state.B("background_input_supported")) command["input_mode"] = "SCOPED_CANVAS";
        if (command.S("type") == "text")
        {
            if (!state.B("paced_text_supported")) throw new InvalidOperationException("Updated RuneLite bridge required for controlled typing");
            var text = command.S("text"); var delay = pacing.TypingDelayMs(text); command["key_delay_ms"] = delay;
            int[]? delays = null;
            if (state.B("character_timing_supported")) { delays = pacing.TypingDelaysMs(text, delay); command["key_delays_ms"] = J.Node(delays); }
            var holds = motion.KeyHolds(state, text, command.B("enter")); foreach (var field in holds) command[field.Key] = field.Value?.DeepClone();
            if (state.B("text_edit_timing_supported"))
            {
                if (!state["input_text_length"].IsInteger() || state.L("input_text_length") is < -1 or > 80) throw new ArgumentException("Observed input length required before precise text clearing");
                if (state.L("input_text_length") >= 0) command["clear_key_count"] = state.L("input_text_length");
                if (command.B("enter")) command["enter_pause_ms"] = pacing.EnterDelayMs();
            }
            var keyHolds = command["key_hold_ms"] is JsonArray ha ? ha.Select(n => (int)n.LongValue()).ToArray() : null;
            timeout = ProtocolHelpers.TypingReceiptTimeout(text, delay, delays, command.S("input_mode") == "SCOPED_CANVAS", (int)command.L("enter_pause_ms"), keyHolds, (int)command.L("enter_hold_ms"));
        }
        var request = J.O(("id", Guid.NewGuid().ToString()), ("session", state["session"]), ("sequence", state["sequence"]), ("created_at", J.Now));
        foreach (var p in command) request[p.Key] = p.Value?.DeepClone();
        ThrowIfUserInput();
        if (PendingId is not null) throw new TimeoutException("Previous input outcome must settle before another command");
        PendingId = request.S("id"); ExecutionPhase = J.O(("phase", command.S("type")), ("active", true), ("started_at", Now), ("command_id", PendingId), ("last_progress_at", Now));
        pendingContext = J.O(("id", PendingId), ("session", state["session"]), ("account_key", state["account_key"]), ("sequence", state["sequence"]), ("created_at", request["created_at"]));
        if (confirmsOffer) confirmDispatched = true;
        CommonStorage.WriteJson(Path.Combine(Folder, "input-command.json"), request);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < timeout)
        {
            JsonObject? receipt = null;
            try { receipt = CommonStorage.ReadJson(Path.Combine(Folder, "input-receipt.json")) as JsonObject; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
            if (receipt?.S("id") == request.S("id"))
            {
                if (receipt.S("status") != "dispatched")
                {
                    var reason = receipt.S("reason", "Input rejected");
                    if (InputRejected.NoDispatchReasons.Contains(reason)) { PendingId = null; pendingContext = null; ExecutionPhase["active"] = false; ExecutionPhase["last_progress_at"] = Now; }
                    throw new InputRejected(reason);
                }
                var connection = state.B("session_control_supported"); var completedSequence = (connection ? Connection() : Observe()).L("sequence");
                Wait(s => s.L("sequence") > completedSequence, connection: connection);
                PendingId = null; pendingContext = null; ExecutionPhase["active"] = false; ExecutionPhase["last_progress_at"] = Now; pacing.InputCompleted(); return;
            }
            Pause(.1);
        }
        throw new TimeoutException("Input receipt unavailable; outcome unknown");
    }
    static JsonObject Unique(JsonObject state, Func<JsonObject, bool> predicate)
    {
        var matches = state.Objects("widgets").Where(predicate).ToArray();
        if (matches.Length == 0) throw new TransientUIError("UI target temporarily unavailable; no input sent");
        if (matches.Length != 1) throw new InvalidOperationException($"Expected one UI target, found {matches.Length}; layout needs verification");
        return matches[0];
    }
    void Click(JsonObject state, JsonObject widget)
    {
        Pause(pacing.ClickDelaySeconds(ProtocolHelpers.ClickContext(widget)));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var fresh = Observe(); if (!ProtocolHelpers.Same(fresh, state, "session")) throw new InvalidOperationException("Game session changed while waiting to click");
            var target = Unique(fresh, w => w.S("key") == widget.S("key"));
            if (!ProtocolHelpers.Same(target, widget, "id", "index", "symbol", "item_id", "name", "actions")) throw new InvalidOperationException("Click target changed while waiting; no input sent");
            if (widget.S("symbol") == "GeOffers.SETUP_CONFIRM" && ProtocolHelpers.SetupSignature(fresh) != ProtocolHelpers.SetupSignature(state)) throw new InvalidOperationException("Offer contents changed while waiting to confirm; no input sent");
            try
            {
                var command = J.O(("type", "click"), ("widget_key", target.S("key"))); var plan = motion.PointerPlan(fresh, target); if (plan is not null) command["pointer_plan"] = plan; SendInput(fresh, command, widget.S("symbol") == "GeOffers.SETUP_CONFIRM"); return;
            }
            catch (InputRejected ex) when (ex.NoDispatch)
            {
                if (attempt == 2) throw new TransientUIError(ex.Message + "; no input dispatched"); Wait(s => s.L("sequence") > fresh.L("sequence"), 3);
            }
        }
    }
    static bool Inside(JsonObject widget, JsonObject container) => container.L("x") <= widget.L("x") && widget.L("x") < container.L("x") + container.L("width") && container.L("y") <= widget.L("y") && widget.L("y") < container.L("y") + container.L("height");
    static JsonObject Action(JsonObject state, string needle, JsonObject? container = null) => Unique(state, w => (container is null || Inside(w, container)) && ProtocolHelpers.PrimaryAction(w).Contains(needle, StringComparison.OrdinalIgnoreCase));
    static JsonObject SlotWidget(JsonObject state, int slot) => Unique(state, w => w.S("symbol") == $"GeOffers.INDEX_{slot}" && w.L("index") == -1);
    public void Overview()
    {
        var state = Observe();
        if (state.Objects("widgets").Any(w => w.S("symbol") is "GeOffers.SETUP" or "GeOffers.DETAILS" && w.L("index") == -1))
        { Click(state, Action(state, "back")); Wait(s => s.Objects("widgets").Any(w => w.S("symbol") == "GeOffers.INDEX_0" && w.L("index") == -1)); }
    }
    void SearchItem(int itemId, string itemName)
    {
        var state = Observe(); var icon = ProtocolHelpers.SearchResultIcon(state, itemId, itemName);
        bool HasItem(JsonObject s) => s.Objects("widgets").Any(w => w.L("item_id") == itemId && w.S("symbol").StartsWith("GeOffers."));
        if (icon is not null && !state.B("input_busy")) { Click(state, icon); Wait(HasItem); return; }
        var queries = new[] { ProtocolHelpers.ItemSearchQuery(itemName), ProtocolHelpers.ItemSearchQuery(itemName, true) }.Distinct().ToArray();
        for (var attempt = 0; attempt < queries.Length; attempt++)
        {
            var query = queries[attempt]; Send(Observe(), J.O(("type", "text"), ("text", query), ("enter", false)));
            try
            {
                state = Wait(s => !s.B("input_busy") && s.Objects("widgets").Any(w => w.S("symbol") == "Chatbox.MES_TEXT2" && ProtocolHelpers.Plain(w.S("text")).EndsWith(query + "*", StringComparison.OrdinalIgnoreCase)) && ProtocolHelpers.SearchResultIcon(s, itemId, itemName) is not null);
            }
            catch (TimeoutException)
            {
                if (attempt + 1 == queries.Length) throw;
                state = Observe(); if (state.B("input_busy") || !state.Objects("widgets").Any(w => w.S("symbol") == "Chatbox.MES_TEXT2" && ProtocolHelpers.Plain(w.S("text")).Contains("what would you like to buy?", StringComparison.OrdinalIgnoreCase))) throw; continue;
            }
            Click(state, ProtocolHelpers.SearchResultIcon(state, itemId, itemName)!); Wait(HasItem); return;
        }
    }
    void EnterNumber(string kind, long value)
    {
        var field = kind == "quantity" ? 34 : 41;
        var state = Wait(s => ProtocolHelpers.SetupNumber(s, field) is long n && (kind != "quantity" || n > 0));
        if (ProtocolHelpers.SetupNumber(state, field) == value) return;
        Click(state, Action(state, "Enter " + kind));
        var words = kind == "quantity" ? new[] { "quantity", "how many" } : ["price"];
        state = Wait(s => s.L("input_type") != 0 && s.Objects("widgets").Any(w => w.S("symbol").StartsWith("Chatbox.MES_TEXT") && words.Any(word => ProtocolHelpers.Plain(w.S("text")).Contains(word, StringComparison.OrdinalIgnoreCase))));
        Send(state, J.O(("type", "text"), ("text", value.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("enter", true)));
        Wait(s => s.L("input_type") == 0 && ProtocolHelpers.SetupNumber(s, field) == value);
    }
    public int Submit(string side, int itemId, string itemName, long quantity, long price)
    {
        var before = Observe(); confirmDispatched = false; submissionPhase = "OVERVIEW"; InterruptedSubmission = null;
        try { return SubmitOffer(side, itemId, itemName, quantity, price); }
        catch (Exception ex) when (ex is UserIntervention || stop.IsSet && ex.Message is "Stopped" or "Live input disabled or stopped")
        {
            InterruptedSubmission = J.O(("before_confirm", !confirmDispatched), ("at", Now), ("phase", submissionPhase),
                ("item_id", itemId), ("side", side), ("account_key", before["account_key"]), ("session", before["session"]));
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or InputRejected)
        {
            InterruptedSubmission = J.O(("before_confirm", !confirmDispatched), ("at", Now), ("phase", submissionPhase),
                ("item_id", itemId), ("side", side), ("account_key", before["account_key"]), ("session", before["session"]));
            if (confirmDispatched || PendingId is not null || ex is InputRejected rejected && !rejected.NoDispatch) throw;
            var after = Observe(); var occupiedBefore = before.Objects("offers").Where(o => o.S("state") != "EMPTY").Select(o => o.L("slot")).ToHashSet();
            if (after.B("input_busy") || !ProtocolHelpers.Same(after, before, "session", "account_key", "cash_inventory_gp") || ProtocolHelpers.InventoryQuantity(after, itemId) != ProtocolHelpers.InventoryQuantity(before, itemId) || after.Objects("offers").Any(o => o.S("state") != "EMPTY" && !occupiedBefore.Contains(o.L("slot")))) throw;
            var details = J.O(("observed_at", after["observed_at"]), ("cause", ex.Message), ("item_id", itemId), ("item_name", itemName), ("side", side), ("phase", submissionPhase),
                ("search_widgets", J.A(after.Objects("widgets").Where(w => w.S("symbol").StartsWith("Chatbox.")).Take(30).Select(w => { var r = new JsonObject(); foreach (var key in new[] { "id", "index", "symbol", "text", "name", "item_id", "actions", "x", "y" }) r[key] = w[key]?.DeepClone(); return (JsonNode?)r; }))));
            try { CommonStorage.WriteJson(Path.Combine(Folder, "last-offer-ui-failure.json"), details); } catch (IOException) { }
            throw new OfferNotSubmitted(itemId, side, submissionPhase, details);
        }
    }
    int Capacity(JsonObject state)
    {
        var observed = state.L("ge_slot_capacity", 3); return (int)Math.Min(Math.Clamp(observed, 0, 8), Math.Max(0, Config.L("target_slots", 8)));
    }
    int[] EmptySlots(JsonObject state) => state.Objects("offers").Where(o => o.S("state") == "EMPTY" && o.L("slot") >= 0 && o.L("slot") < Capacity(state)).Select(o => (int)o.L("slot")).ToArray();
    int SubmitOffer(string side, int itemId, string itemName, long quantity, long price)
    {
        if (side is not ("BUY" or "SELL")) throw new ArgumentException("Invalid offer side");
        PhasePause("offer_prepare"); Overview(); var state = Observe();
        if (state.Objects("offers").Count(o => o.S("state") != "EMPTY") >= Capacity(state) || quantity <= 0 || price <= 0) throw new ArgumentException("Slot/quantity constraint");
        if (side == "BUY" && checked(quantity * price) > Math.Min(Config.L("max_slot_allocation_gp", 500000), state.L("cash_inventory_gp"))) throw new ArgumentException("Slot budget exceeded");
        var slots = EmptySlots(state); if (slots.Length == 0) throw new InvalidOperationException("No verified empty slot"); var slot = slots.Min();
        submissionPhase = "OPEN_OFFER"; var action = side == "BUY" ? "buy" : "sell";
        state = Wait(s => s.Objects("widgets").Any(w => w.S("symbol") == $"GeOffers.INDEX_{slot}" && w.L("x", -1) >= 0 && w.Arr("actions").Any(a => ProtocolHelpers.Plain(a?.ToString()).Contains(action, StringComparison.OrdinalIgnoreCase))));
        Click(state, Unique(state, w => w.S("symbol") == $"GeOffers.INDEX_{slot}" && w.L("x", -1) >= 0 && ProtocolHelpers.PrimaryAction(w).Contains(action)));
        if (side == "BUY") { submissionPhase = "SEARCH"; Wait(s => s.L("input_type") != 0); SearchItem(itemId, itemName); }
        else
        {
            state = Wait(s => ProtocolHelpers.InventorySellTargets(s, itemId, quantity).Length > 0); var targets = ProtocolHelpers.InventorySellTargets(state, itemId, quantity);
            if (targets.Length != 1) throw new InvalidOperationException("Ambiguous inventory item for sale"); Click(state, targets[0]);
        }
        submissionPhase = "QUANTITY"; EnterNumber("quantity", quantity); submissionPhase = "PRICE"; EnterNumber("price", price); submissionPhase = "VERIFY_SETUP"; PhasePause("price_review"); state = Observe();
        var setup = Unique(state, w => w.S("symbol") == "GeOffers.SETUP" && w.L("index") == -1); var widgets = state.Objects("widgets").Where(w => Inside(w, setup)).ToArray();
        var contained = J.O(("widgets", J.A(widgets.Cast<JsonNode?>())));
        if (!widgets.Any(w => w.L("item_id") == itemId) || ProtocolHelpers.SetupNumber(contained, 34) != quantity || ProtocolHelpers.SetupNumber(contained, 41) != price || !string.Join(" ", widgets.Select(w => ProtocolHelpers.Plain(w.S("text")))).Contains(side, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Setup item/side/quantity/price not verified; offer not confirmed");
        var confirm = Unique(state, w => w.S("symbol") == "GeOffers.SETUP_CONFIRM" && w.L("index") == -1); Click(state, confirm);
        Wait(s => s.Objects("offers").Any(o => o.L("slot") == slot && o.L("item_id") == itemId && o.L("quantity") == quantity && o.L("price") == price && (side == "BUY" ? o.S("state") is "BUYING" or "BOUGHT" : o.S("state") is "SELLING" or "SOLD")));
        pacing.PhaseCompleted("offer_submitted", Guid.NewGuid().ToString()); return slot;
    }
    string? DetailViewKey(JsonObject state, int slot)
    {
        var raw = state.Objects("offers").FirstOrDefault(o => o.L("slot") == slot && o.S("state") != "EMPTY");
        if (raw is null || !GeSettlementView.MatchesOffer(state, raw)) return null;
        if (state.Objects("offers").Count(o => o.S("state") != "EMPTY" && GeSettlementView.SameIdentity(o, raw)) != 1) return null;
        return J.O(("account", state["account_key"]), ("session", state["session"]), ("slot", slot),
            ("item", raw["item_id"]), ("quantity", raw["quantity"]), ("price", raw["price"]), ("buying", GeSettlementView.Buying(raw))).ToJsonString();
    }
    void View(int slot)
    {
        var state = Observe(); var key = DetailViewKey(state, slot); if (key is not null && key == detailViewCache) return; detailViewCache = null;
        Overview(); state = Observe(); if (state.Objects("widgets").Any(w => w.S("symbol") == "GeOffers.DETAILS")) Click(state, Action(state, "back"));
        state = Wait(s => s.Objects("widgets").Any(w => w.S("symbol") == $"GeOffers.INDEX_{slot}" && w.L("index") == -1 && w.L("x", -1) >= 0));
        var expected = state.Objects("offers").Single(o => o.L("slot") == slot).Copy();
        Click(state, Action(state, "view", SlotWidget(state, slot)));
        state = Wait(s => s.Objects("offers").Any(o => o.L("slot") == slot && GeSettlementView.SameIdentity(o, expected) && GeSettlementView.MatchesOffer(s, o)));
        detailViewCache = DetailViewKey(state, slot);
    }
    public void Cancel(int slot)
    {
        View(slot); var state = Observe(); var raw = state.Objects("offers").Single(o => o.L("slot") == slot).Copy();
        if (!GeSettlementView.MatchesOffer(state, raw)) throw new TransientUIError("GE details changed before cancellation; no cancellation input sent");
        if (GeSettlementView.Settled(raw)) return;
        Click(state, Action(state, "abort"));
        state = Wait(s => s.Objects("offers").Any(o => o.L("slot") == slot && GeSettlementView.SameIdentity(o, raw) && GeSettlementView.Settled(o) && GeSettlementView.MatchesOffer(s, o)));
        detailViewCache = DetailViewKey(state, slot);
    }
    public JsonObject? Collect(int slot)
    {
        collectionInputActive = true;
        try { return CollectOffer(slot); }
        finally { collectionInputActive = false; }
    }
    public JsonObject CollectOverview(int slot, long precollectedItems = 0)
    {
        collectionInputActive = true;
        try
        {
            Overview(); PhasePause("collection_prepare"); var before = Observe(); ResolveOverviewJournal(before);
            var raw = before.Objects("offers").Single(o => o.I("slot") == slot);
            if (!GeSettlementView.Settled(raw)) throw new TransientUIError("Offer is not ready for overview collection");
            if (!ManualCollectionObserver.CanAttributeOverview(before, Config)
                || before.Objects("offers").Any(o => o.S("state") == "CANCELLED_SELL" && o.L("filled") > 0)
                || before.Objects("offers").Any(o => !GeSettlementView.Settled(o) && o.S("state") != "EMPTY" && o.L("filled") > 0))
            {
                if (raw.S("state") == "BOUGHT" && precollectedItems is > 0 && precollectedItems <= raw.L("filled"))
                {
                    var known = J.O(("cash_delta", 0), ("item_delta", raw.L("filled") - precollectedItems),
                        ("cash_before_gp", before.L("cash_inventory_gp")),
                        ("item_before", ProtocolHelpers.InventoryQuantity(before, raw.I("item_id"))));
                    return CollectOffer(slot, known)!;
                }
                // Several taxed sales cannot be apportioned from one aggregate
                // cash delta. Settle this slot exactly instead of blocking all work.
                var amounts = CollectionAmounts(slot); var detail = Observe();
                var exact = detail.Objects("offers").Single(o => o.I("slot") == slot);
                if (!Lifecycle.SameFields(exact, raw, "state", "item_id", "quantity", "filled", "price", "spent"))
                    throw new TransientUIError("Offer changed before individual collection");
                return Lifecycle.CollectionReceipt(this, detail, exact, amounts, Collect(slot));
            }
            // Collect also gathers partial fills. Do not silently consume active offers.
            if (before.Objects("offers").Any(o => !GeSettlementView.Settled(o) && o.S("state") != "EMPTY" && o.L("filled") > 0))
                throw new TransientUIError("Overview collection waiting for partially filled active offers");
            var targets = before.Objects("widgets").Where(w => w.L("x", -1) >= 0 && w.L("y", -1) >= 0
                && w.S("symbol") == "GeOffers.COLLECTALL" && w.Arr("actions").Any(a => ProtocolHelpers.Plain(a?.ToString() ?? "").Equals("Collect to inventory", StringComparison.OrdinalIgnoreCase))).ToArray();
            if (targets.Length != 1) throw new TransientUIError("Unique overview Collect control unavailable; no input sent");
            var path = Path.Combine(Folder, "overview-collection-pending.json");
            if (File.Exists(path) && (CommonStorage.ReadJson(path) as JsonObject)?.S("status") == "PREPARED")
                throw new TransientUIError("Previous overview collection needs reconciliation; no repeat input sent");
            CommonStorage.WriteJson(path, J.O(("before", before), ("requested_slot", slot), ("status", "PREPARED")));
            var observer = new ManualCollectionObserver(Config); observer.Reset(before);
            if (!ProtocolHelpers.PrimaryAction(targets[0]).Equals("collect to inventory", StringComparison.OrdinalIgnoreCase))
                throw new TransientUIError("Overview Collect primary action changed; no input sent");
            Click(before, targets[0]);
            var after = Wait(s => before.Objects("offers").Where(GeSettlementView.Settled).All(o => IsEmpty(s, o.I("slot"))), 4);
            CommonStorage.WriteJson(path, J.O(("before", before), ("after", after), ("requested_slot", slot), ("status", "PREPARED")));
            var receipts = observer.Observe(after);
            var receipt = receipts.SingleOrDefault(r => r.Obj("offer").I("slot") == slot);
            if (receipt is null) throw new TransientUIError("Overview collection completed; exact attribution needs reconciliation; no repeat input");
            foreach (var row in receipts) SaveCollectionReceipt(row);
            CommonStorage.WriteJson(path, J.O(("status", "COMPLETED"), ("receipts", J.A(receipts.Cast<JsonNode?>()))));
            pacing.PhaseCompleted("collection_completed", receipt.S("receipt_id")); return receipt;
        }
        finally { collectionInputActive = false; }
    }
    JsonObject? CollectOffer(int slot, JsonObject? suppliedAmounts = null)
    {
        PhasePause("collection_prepare"); if (suppliedAmounts != null) View(slot); var state = Observe(); var raw = state.Objects("offers").First(o => o.L("slot") == slot).Copy(); var pending = PendingCollection(state, slot, raw.S("state") == "EMPTY" ? null : raw);
        if (raw.S("state") == "EMPTY" && pending is not null) return CompleteCollection(pending, state);
        if (raw.S("state") is not ("BOUGHT" or "SOLD" or "CANCELLED_BUY" or "CANCELLED_SELL")) throw new InvalidOperationException("Offer still active");
        var amounts = suppliedAmounts ?? CollectionAmounts(slot); state = Observe(); var current = state.Objects("offers").First(o => o.L("slot") == slot);
        if (!ProtocolHelpers.Same(current, raw, "item_id", "quantity", "price", "filled", "spent", "state")) throw new InvalidOperationException("Offer changed before collection; no input sent");
        var receipt = pending ?? J.O(("receipt_id", Guid.NewGuid().ToString()), ("status", "PREPARED"), ("account_key", state["account_key"]), ("session", state["session"]), ("offer", raw),
            ("collect_started_at", state.L("observed_at")), ("cash_before_gp", state.L("cash_inventory_gp")), ("item_before", ProtocolHelpers.InventoryQuantity(state, (int)raw.L("item_id"))), ("expected_cash_delta", amounts.L("cash_delta")), ("expected_item_delta", amounts.L("item_delta")));
        if (pending is null) receipt["inventory_before_snapshot"] = state["inventory"]?.DeepClone();
        CollectionProgress(receipt, state); SaveCollectionReceipt(receipt);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var (cash, items) = CollectionProgress(receipt, state);
            if (cash == receipt.L("expected_cash_delta") && items == receipt.L("expected_item_delta")) { state = Wait(s => IsEmpty(s, slot)); return CompleteCollection(receipt, state); }
            var targets = state.Objects("widgets").Where(w => w.S("symbol") == "GeOffers.DETAILS_COLLECT" && w.L("x", -1) >= 0 && w.L("item_id", -1) > 0 && w.L("item_quantity") > 0
                && (w.L("item_id") == 995 ? receipt.L("expected_cash_delta") > cash : receipt.L("expected_item_delta") > items) && ProtocolHelpers.PrimaryAction(w).StartsWith("collect") && !ProtocolHelpers.PrimaryAction(w).Contains("bank")).OrderBy(w => w.L("index")).ToArray();
            if (targets.Length == 0) throw new InvalidOperationException("No verified inventory collect target"); var target = targets[0]; var beforeCash = state.L("cash_inventory_gp"); var beforeItems = ProtocolHelpers.InventoryQuantity(state, (int)raw.L("item_id"));
            Click(state, target);
            state = Wait(s => IsEmpty(s, slot) || s.L("cash_inventory_gp") != beforeCash || ProtocolHelpers.InventoryQuantity(s, (int)raw.L("item_id")) != beforeItems || !s.Objects("widgets").Any(w => w.S("key") == target.S("key") && w.Arr("actions").Count > 0 && ProtocolHelpers.Same(w, target, "item_id", "item_quantity")));
            if (IsEmpty(state, slot)) return CompleteCollection(receipt, state);
        }
        state = Wait(s => IsEmpty(s, slot)); return CompleteCollection(receipt, state);
    }
    static bool IsEmpty(JsonObject state, int slot) => state.Objects("offers").Any(o => o.L("slot") == slot && o.S("state") == "EMPTY");
    JsonObject? PendingCollection(JsonObject state, int slot, JsonObject? offer = null)
    {
        var matches = CollectionReceipts(true).Where(r => r.S("status") == "PREPARED" && r.S("account_key") == state.S("account_key") && ProtocolHelpers.Same(r, state, "session") && r.Obj("offer").L("slot") == slot
            && (offer is null || ProtocolHelpers.Same(r.Obj("offer"), offer, "item_id", "quantity", "price", "filled", "spent", "state"))).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Ambiguous pending collection receipts; no input sent"); return matches.FirstOrDefault();
    }
    static (long Cash, long Items) CollectionProgress(JsonObject receipt, JsonObject state)
    {
        if (!ProtocolHelpers.Same(state, receipt, "account_key", "session") || state.L("observed_at") < receipt.L("collect_started_at")) throw new InvalidOperationException("Collection account/session/time changed; needs reconciliation");
        var cash = state.L("cash_inventory_gp") - receipt.L("cash_before_gp"); var items = ProtocolHelpers.InventoryQuantity(state, (int)receipt.Obj("offer").L("item_id")) - receipt.L("item_before");
        if (cash < 0 || cash > receipt.L("expected_cash_delta") || items < 0 || items > receipt.L("expected_item_delta")) throw new InvalidOperationException("Collected wallet/inventory deltas do not match verified offer amounts"); return (cash, items);
    }
    JsonObject CompleteCollection(JsonObject receipt, JsonObject state)
    {
        var (cash, items) = CollectionProgress(receipt, state);
        if (cash != receipt.L("expected_cash_delta") || items != receipt.L("expected_item_delta") || !IsEmpty(state, (int)receipt.Obj("offer").L("slot"))) throw new InvalidOperationException("Collected wallet/inventory deltas do not match verified offer amounts");
        receipt["status"] = "COMPLETED"; receipt["cash_after_gp"] = state.L("cash_inventory_gp"); receipt["item_after"] = ProtocolHelpers.InventoryQuantity(state, (int)receipt.Obj("offer").L("item_id")); receipt["cash_delta"] = cash; receipt["item_delta"] = items; receipt["observed_at"] = state.L("observed_at");
        SaveCollectionReceipt(receipt); pacing.PhaseCompleted("collection_completed", receipt.S("receipt_id")); return receipt;
    }
    public void ReconcilePendingCollections(JsonObject state)
    {
        ResolveOverviewJournal(state);
        foreach (var offer in state.Objects("offers").Where(o => o.S("state") == "EMPTY"))
        {
            var receipt = PendingCollection(state, (int)offer.L("slot")); if (receipt is null) continue;
            try { CompleteCollection(receipt, state); } catch (InvalidOperationException) { }
        }
    }
    void ResolveOverviewJournal(JsonObject state)
    {
        var path = Path.Combine(Folder, "overview-collection-pending.json"); if (!File.Exists(path)) return;
        var pending = CommonStorage.ReadJson(path) as JsonObject;
        if (pending?.S("status") != "PREPARED" || pending["before"] is not JsonObject before) return;
        if (pending["after"] is JsonObject savedAfter && ProtocolHelpers.Same(before, savedAfter, "account_key", "session"))
        {
            var recovery = new ManualCollectionObserver(Config); recovery.Observe(before);
            var recovered = recovery.Observe(savedAfter).ToList();
            if (recovered.Count == 0 && pending["requested_slot"].IsInteger()
                && ManualCollectionObserver.RecoverRequestedOverview(before, savedAfter, pending.I("requested_slot"), Config) is JsonObject requested)
                recovered.Add(requested);
            foreach (var row in recovered) SaveCollectionReceipt(row);
        }
        else if (!ProtocolHelpers.Same(before, state, "account_key", "session")) return;
        var settled = before.Objects("offers").Where(GeSettlementView.Settled).ToArray();
        var receipts = CollectionReceipts().Where(r => r.L("collect_started_at") >= before.L("observed_at")
            && ProtocolHelpers.Same(r, before, "account_key", "session")).ToArray();
        var found = new List<JsonNode?>();
        foreach (var offer in settled)
        {
            var exact = receipts.Where(r => ProtocolHelpers.Same(r.Obj("offer"), offer, "slot", "state", "item_id", "quantity", "price", "filled", "spent")
                && Lifecycle.ValidCollectionReceipt(r, before.S("account_key"), offer.I("slot"), offer.I("item_id"), offer.L("quantity"), offer.L("price"), GeSettlementView.Buying(offer), before.L("observed_at"))).ToArray();
            if (exact.Length != 1)
            {
                var current = state.Objects("offers").FirstOrDefault(o => o.I("slot") == offer.I("slot"));
                var oldOfferGone = current is null || current.S("state") == "EMPTY" || !GeSettlementView.SameIdentity(current, offer);
                var isolationAge = Config.L("overview_collection_isolation_seconds", 30);
                if (oldOfferGone && state.L("observed_at") - before.L("observed_at") >= isolationAge)
                {
                    // Preserve the unresolved trade for its own recovery audit, but
                    // release the global overview collector. One historical receipt
                    // must never block every later settled offer indefinitely.
                    CommonStorage.WriteJson(path, J.O(("status", "ISOLATED_UNRESOLVED"),
                        ("requested_slot", pending["requested_slot"]), ("before", before),
                        ("isolated_at", state["observed_at"]), ("unresolved_offer", offer),
                        ("reason", "EXACT_COLLECTION_RECEIPT_UNAVAILABLE")));
                }
                return;
            }
            found.Add(exact[0].Copy());
        }
        if (settled.Length > 0) CommonStorage.WriteJson(path, J.O(("status", "COMPLETED"), ("reconciled_at", state["observed_at"]), ("receipts", J.A(found))));
    }
    public IEnumerable<JsonObject> CollectionReceipts(bool includePending = false)
    {
        var path = Path.Combine(Folder, "live-collection-receipts.json"); if (!File.Exists(path)) return [];
        var journal = (JsonObject)CommonStorage.ReadJson(path); if (journal.L("schema") != 1) throw new InvalidDataException("Unsupported collection receipt journal");
        return journal.Objects("receipts").Where(r => r.S("account_key") == Account && (includePending || r.S("status") == "COMPLETED")).Select(r => r.Copy()).ToArray();
    }
    void SaveCollectionReceipt(JsonObject receipt)
    {
        var path = Path.Combine(Folder, "live-collection-receipts.json"); var journal = File.Exists(path) ? (JsonObject)CommonStorage.ReadJson(path) : J.O(("schema", 1), ("receipts", new JsonArray()));
        if (journal.L("schema") != 1) throw new InvalidDataException("Unsupported collection receipt journal");
        var entries = journal.Objects("receipts").Where(r => r.S("receipt_id") != receipt.S("receipt_id")).Append(receipt).TakeLast(256); journal["receipts"] = J.A(entries.Cast<JsonNode?>()); CommonStorage.WriteJson(path, journal);
    }
    public JsonObject CollectionAmounts(int slot)
    {
        View(slot); var state = Observe(); var raw = state.Objects("offers").First(o => o.L("slot") == slot).Copy();
        if (!GeSettlementView.Settled(raw)) throw new TransientUIError("GE offer changed before reading settlement; no collection input sent");
        JsonObject? amounts = null;
        try
        {
            state = Wait(s =>
            {
                var current = s.Objects("offers").FirstOrDefault(o => o.L("slot") == slot);
                if (current is null || !ProtocolHelpers.Same(current, raw, "item_id", "quantity", "price", "filled", "spent", "state"))
                    throw new TransientUIError("GE offer changed while reading settlement; no collection input sent");
                var pending = PendingCollection(s, slot, raw);
                if (pending is null) return GeSettlementView.TryAmounts(s, raw, out amounts);
                var (collectedCash, collectedItems) = CollectionProgress(pending, s);
                if (!GeSettlementView.TryAmounts(s, raw, out _, pending.L("expected_cash_delta") - collectedCash, pending.L("expected_item_delta") - collectedItems)) return false;
                amounts = J.O(("cash_delta", pending.L("expected_cash_delta")), ("item_delta", pending.L("expected_item_delta")),
                    ("cash_before_gp", pending.L("cash_before_gp")), ("item_before", pending.L("item_before"))); return true;
            }, 4);
        }
        catch (TimeoutException) { throw new TransientUIError("GE settlement display not synchronized; amounts remain unconfirmed and no collection input sent"); }
        detailViewCache = DetailViewKey(state, slot); return amounts!;
    }
}
