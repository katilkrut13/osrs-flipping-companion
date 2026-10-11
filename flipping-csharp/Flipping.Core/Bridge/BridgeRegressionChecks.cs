using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

/// <summary>Offline protocol checks. All exchange input is recorded in memory or rejected before dispatch.</summary>
public static class BridgeRegressionChecks
{
    public static JsonObject Run(JsonObject? config = null)
    {
        var checks = new List<string>();
        void Check(string name, Action action) { action(); checks.Add(name); }
        static void Assert(bool result, string message) { if (!result) throw new InvalidOperationException("Bridge regression: " + message); }
        static void Reject(Action action)
        {
            try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; }
            throw new InvalidOperationException("Bridge regression: expected refusal");
        }
        var folder = Path.Combine(Path.GetTempPath(), "OsrsFlippingBridgeTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            Check("search fragments and exact result ID", () =>
            {
                Assert(ProtocolHelpers.ItemSearchQuery("Zulrah's scales") == "Zulrah", "apostrophe fragment");
                Assert(ProtocolHelpers.ItemSearchQuery("Death rune") == "Death rune", "plain query");
                Assert(!ProtocolHelpers.ItemSearchQuery("Magic logs").Any(c => c is 'i' or 'I' or '\''), "unsupported characters");
                var state = J.O(("widgets", J.A(
                    J.O(("key", "icon"), ("id", 1), ("item_id", 21820), ("symbol", "Chatbox.RESULT"), ("x", 20), ("y", 400)),
                    J.O(("key", "row"), ("id", 1), ("name", "Revenant ether"), ("actions", new[] { "Select" }), ("symbol", "Chatbox.RESULT"), ("x", 16), ("y", 398), ("width", 500), ("height", 36)))));
                Assert(ProtocolHelpers.SearchResultIcon(state, 21820, "Revenant ether")?.S("key") == "icon", "exact paired icon");
                Assert(ProtocolHelpers.SearchResultIcon(state, 21821, "Revenant ether") is null, "wrong ID refused");
                state.Arr("widgets").Add(state.Arr("widgets")[0]!.DeepClone());
                Assert(ProtocolHelpers.SearchResultIcon(state, 21820, "Revenant ether") is null, "ambiguous icons refused");
            });
            Check("banknote sale and exact setup quantities", () =>
            {
                var note = J.O(("key", "note"), ("symbol", "GeOffersSide.ITEMS"), ("item_id", 2358), ("x", 10), ("actions", new[] { "Offer" }));
                var state = J.O(("inventory", J.A(J.O(("id", 2358), ("unnoted_id", 2357), ("quantity", 166)))), ("widgets", J.A(note)));
                Assert(ProtocolHelpers.InventorySellTargets(state, 2357, 166).Length == 1, "noted inventory mapping");
                Assert(ProtocolHelpers.InventorySellTargets(state, 2357, 167).Length == 0, "insufficient quantity refused");
                Assert(ProtocolHelpers.InventorySellTargets(state, 2359, 1).Length == 0, "wrong note refused");
                state["widgets"] = J.A(J.O(("symbol", "GeOffers.SETUP"), ("index", 34), ("x", 10), ("y", 10), ("text", "<col=ff9040>1,330</col>")));
                Assert(ProtocolHelpers.SetupNumber(state, 34) == 1330, "exact rendered quantity");
                state.Arr("widgets").Add(state.Arr("widgets")[0]!.DeepClone()); Assert(ProtocolHelpers.SetupNumber(state, 34) is null, "ambiguous quantity refused");
            });
            Check("confirmation signature freezes quantity and price", () =>
            {
                var state = J.O(("widgets", J.A(J.O(("key", "confirm"), ("symbol", "GeOffers.SETUP_CONFIRM"), ("index", -1)), J.O(("key", "number"), ("symbol", "GeOffers.SETUP"), ("index", 34), ("text", "100")))));
                var before = ProtocolHelpers.SetupSignature(state); state.Objects("widgets").Last()["text"] = "101";
                Assert(before != ProtocolHelpers.SetupSignature(state), "changed quantity detected");
                state.Objects("widgets").Last()["index"] = 41; Assert(before != ProtocolHelpers.SetupSignature(state), "changed price field detected");
            });
            Check("typing receipt duration and bounded pacing", () =>
            {
                Assert(ProtocolHelpers.TypingReceiptTimeout(new string('A', 80), 90, Enumerable.Repeat(150, 80).ToArray(), true) == 30, "scoped timeout");
                Assert(ProtocolHelpers.TypingReceiptTimeout(new string('A', 80), 90, Enumerable.Repeat(150, 80).ToArray()) == 54, "foreground Shift timeout");
                var legacy = new InputPacing(new JsonObject(), new Random(1)); Assert(legacy.ClickDelaySeconds() == 0 && legacy.TypingDelayMs() == 90, "legacy pacing");
                var settings = J.O(("click_pause_seconds", .18), ("click_pause_jitter_seconds", .035), ("typing_key_delay_ms", 96), ("typing_key_jitter_ms", 6), ("typing_character_jitter_ms", 10), ("typing_word_pause_ms", 12));
                var pacing = new InputPacing(settings, new Random(42));
                for (var i = 0; i < 200; i++) { var delay = pacing.ClickDelaySeconds(); Assert(delay >= .145 - 1e-12 && delay <= .215 + 1e-12, "click bounds"); Assert(pacing.TypingDelayMs() is >= 90 and <= 102, "typing bounds"); }
                var plan = pacing.TypingDelaysMs("Death rune", 100); Assert(plan.Length == 10 && plan.All(d => d is >= 50 and <= 150), "per-character bounds");
                Reject(() => new InputPacing(J.O(("typing_key_delay_ms", true)))); Reject(() => new InputPacing(J.O(("click_pause_jitter_seconds", .2))));
                Reject(() => new MotionTiming(J.O(("pointer_timing_enabled", "false"))));
                var motion = new MotionTiming(J.O(("pointer_timing_enabled", true)), new Random(1));
                var canvas = J.O(("pointer_timing_supported", true), ("canvas_width", 800), ("canvas_height", 600));
                Assert(motion.PointerPlan(canvas, J.O(("x", 10), ("y", 590), ("width", 150), ("height", 32))) is null, "clipped search row uses ordinary click");
                Assert(motion.PointerPlan(canvas, J.O(("x", 10), ("y", 500), ("width", 150), ("height", 32))) is not null, "fully visible target retains supported plan");
            });
            Check("input preflight refuses without creating command", () =>
            {
                using var live = new LiveExchange(folder);
                Reject(() => live.Send(J.O(("input_enabled", false), ("inventory", new JsonArray())), J.O(("type", "click"), ("widget_key", "unused"))));
                Reject(() => live.Send(J.O(("input_enabled", true), ("inventory", J.A(J.O(("id", 13204), ("quantity", 1))))), J.O(("type", "click"), ("widget_key", "unused"))));
                Reject(() => live.Send(J.O(("input_enabled", true), ("inventory", new JsonArray()), ("paced_text_supported", false)), J.O(("type", "text"), ("text", "Death rune"), ("enter", false))));
                live.RequestStop(); Reject(() => live.Send(J.O(("input_enabled", true), ("inventory", new JsonArray())), J.O(("type", "click"), ("widget_key", "unused"))));
                Assert(!File.Exists(Path.Combine(folder, "input-command.json")), "no dispatch file");
            });
            Check("fake IPC preserves sequence receipts and unknown outcomes", () =>
            {
                foreach (var reason in new string?[] { null, "Widget not visible", "Uncertain bridge failure" })
                {
                    foreach (var name in new[] { "input-command.json", "input-receipt.json", "input-cancel.json" }) { var path = Path.Combine(folder, name); if (File.Exists(path)) File.Delete(path); }
                    var state = Snapshot(); state["input_enabled"] = true; state["paced_text_supported"] = true; state["background_input_supported"] = true;
                    state["character_timing_supported"] = true; state["text_edit_timing_supported"] = true; state["input_text_length"] = 7;
                    Storage.WriteJson(Path.Combine(folder, "live-state.json"), state);
                    using var live = new LiveExchange(folder); live.Observe(); using var done = new CancellationTokenSource();
                    JsonObject? sent = null;
                    var responder = System.Threading.Tasks.Task.Run(() =>
                    {
                        var commandPath = Path.Combine(folder, "input-command.json");
                        while (!done.IsCancellationRequested && !File.Exists(commandPath)) Thread.Sleep(10);
                        if (done.IsCancellationRequested) return;
                        sent = (JsonObject)Storage.ReadJson(commandPath);
                        Storage.WriteJson(Path.Combine(folder, "input-receipt.json"), J.O(("id", sent.S("id")), ("status", reason is null ? "dispatched" : "rejected"), ("reason", reason)));
                        while (!done.IsCancellationRequested)
                        {
                            state["sequence"] = state.L("sequence") + 1; state["observed_at"] = J.Now;
                            Storage.WriteJson(Path.Combine(folder, "live-state.json"), state); Thread.Sleep(25);
                        }
                    });
                    try
                    {
                        if (reason is null)
                        {
                            live.Send(state.Copy(), J.O(("type", "text"), ("text", "Death rune"), ("enter", true)));
                            Assert(live.PendingId is null && !live.ExecutionPhase.B("active"), "only confirmed receipt clears command");
                        }
                        else
                        {
                            try { live.Send(state.Copy(), J.O(("type", "click"), ("widget_key", "fixture"))); throw new Exception("Bridge regression: expected fake rejection"); }
                            catch (InputRejected rejection) { Assert(rejection.NoDispatch == (reason == "Widget not visible"), "explicit no-dispatch classification"); }
                            Assert((live.PendingId is null) == (reason == "Widget not visible"), "unknown outcome retained");
                            if (live.PendingId is not null)
                            {
                                live.RequestStop(); var cancelled = (JsonObject)Storage.ReadJson(Path.Combine(folder, "input-cancel.json"));
                                Assert(cancelled.S("id") == live.PendingId && cancelled.S("session") == "bridge-session", "cancel bound to pending command and session");
                            }
                        }
                    }
                    finally { done.Cancel(); responder.GetAwaiter().GetResult(); }
                    Assert(sent is not null && sent.S("session") == "bridge-session" && sent.L("sequence") == 1 && sent.S("input_mode") == "SCOPED_CANVAS", "request preserves session, sequence and scoped mode");
                    if (reason is null) Assert(sent!.S("text") == "Death rune" && sent.L("clear_key_count") == 7 && sent.L("key_delay_ms") == 90 && sent.Arr("key_delays_ms").Count == 10, "precise typing payload");
                }
                foreach (var name in new[] { "input-command.json", "input-receipt.json", "input-cancel.json" }) { var path = Path.Combine(folder, name); if (File.Exists(path)) File.Delete(path); }
            });
            Check("pending collection completes only exact balances", () =>
            {
                var state = Snapshot(); state["inventory"] = J.A(J.O(("id", 100), ("quantity", 4))); Storage.WriteJson(Path.Combine(folder, "live-state.json"), state);
                using var live = new LiveExchange(folder); live.Observe();
                var receipt = J.O(("receipt_id", "exact-offline"), ("status", "PREPARED"), ("account_key", "bridge-fixture"), ("session", "bridge-session"),
                    ("offer", J.O(("slot", 0), ("item_id", 100), ("quantity", 4), ("price", 10), ("filled", 4), ("spent", 40), ("state", "BOUGHT"))), ("collect_started_at", state.L("observed_at")),
                    ("cash_before_gp", 100), ("item_before", 0), ("expected_cash_delta", 0), ("expected_item_delta", 4));
                Storage.WriteJson(Path.Combine(folder, "live-collection-receipts.json"), J.O(("schema", 1), ("receipts", J.A(receipt))));
                live.ReconcilePendingCollections(state); var done = live.CollectionReceipts().Single(); Assert(done.S("status") == "COMPLETED" && done.L("item_delta") == 4 && done.L("cash_delta") == 0, "exact collection settled");
                receipt["receipt_id"] = "mismatched-offline"; receipt["item_before"] = 4; receipt["expected_item_delta"] = 1;
                Storage.WriteJson(Path.Combine(folder, "live-collection-receipts.json"), J.O(("schema", 1), ("receipts", J.A(receipt)))); live.ReconcilePendingCollections(state);
                Assert(!live.CollectionReceipts().Any() && live.CollectionReceipts(true).Single().S("status") == "PREPARED", "mismatch preserved");
                Assert(!File.Exists(Path.Combine(folder, "input-command.json")), "receipt repair is read-only");
            });
            Check("bank transfer waits and proves both balances", () =>
            {
                var state = Snapshot(); state["bank_open"] = true; state["inventory"] = J.A(J.O(("id", 995), ("quantity", 100))); state["bank_inventory"] = J.A(J.O(("id", 995), ("quantity", 1000)));
                state["widgets"] = J.A(J.O(("key", "bank-gp"), ("symbol", "Bankmain.ITEMS"), ("item_id", 995), ("actions", new[] { "Withdraw-X" })));
                var exchange = new RecordingExchange(state); var session = new SessionController(exchange, J.O(("budget_gp", 2000000)));
                Assert(session.StartTransfer(state, "WITHDRAW", 995, 300, "FIXTURE"), "transfer started"); session.StepTransfer(state); Assert(session.Task!.S("stage") == "PROMPT" && exchange.Commands.Count == 1, "one amount prompt");
                state["input_type"] = 7; state.Arr("widgets").Add(J.O(("symbol", "Chatbox.MES_TEXT"), ("text", "How many?"))); session.StepTransfer(state);
                Assert(session.Task.S("stage") == "VERIFY" && exchange.Commands.Count == 2, "amount entered once"); session.StepTransfer(state); session.StepTransfer(state); Assert(exchange.Commands.Count == 2 && session.Pending, "unchanged server balances never resend");
                state.Objects("inventory").Single()["quantity"] = 400; Assert(session.StepTransfer(state) is null, "one balance alone insufficient"); state.Objects("bank_inventory").Single()["quantity"] = 700;
                Assert(session.StepTransfer(state)?.S("stage") == "DONE" && !session.Pending, "both balances prove transfer");
                Assert(exchange.Commands.Count == 2, "no extra bank actions");
            });
            Check("stall monitor ignores market waits and latches unknown receipt", () =>
            {
                double now = 0; var monitor = new ExecutionStallMonitor(J.O(("input_stall_enabled", true)), () => now); var state = Snapshot(); var passive = J.O(("item_id", 100), ("stage", "WAIT_BUY"));
                foreach (var t in new[] { 0d, 20, 60, 7200 }) { now = t; Assert(!monitor.Observe(state, [passive], passive).B("armed"), "passive market wait"); }
                var phase = J.O(("phase", "click"), ("active", true), ("command_id", "unknown"), ("last_progress_at", 0));
                foreach (var t in new[] { 0d, 20, 50 }) { now = t; state["cash_inventory_gp"] = 100 + (long)t; monitor.Observe(state, pendingId: "unknown", executionPhase: phase); }
                Assert(monitor.Status.S("hold_reason") == "INPUT_STALL_NEEDS_REVIEW", "unrelated balance changes cannot acknowledge command");
                now = 51; Assert(monitor.Observe(state).S("hold_reason") == "INPUT_STALL_NEEDS_REVIEW", "review latched"); monitor.Reset(); Assert(monitor.Observe(state)["hold_reason"] is null, "explicit reset");
                Reject(() => new ExecutionStallMonitor(J.O(("input_stall_enabled", null)))); Reject(() => new ExecutionStallMonitor((JsonObject)JsonNode.Parse("{\"input_stall_min_snapshots\":3.0}")!));
            });
            if (OperatingSystem.IsWindows()) Check("DPAPI fake PIN roundtrip and account binding", () =>
            {
                var path = Path.Combine(folder, "test-bank-pin.protected.json"); Credentials.StoreBankPin(path, "offline-fixture", "8042");
                Assert(Credentials.LoadBankPin(path, "offline-fixture") == "8042", "protected fake PIN roundtrip"); Assert(Credentials.LoadBankPin(path, "other-fixture") is null, "account binding");
                var record = (JsonObject)Storage.ReadJson(path); Assert(record.S("protected") != "8042" && record.Count == 2, "only protected value stored");
            });
            if (config is not null) Check("provided pacing configuration", () => { _ = new InputPacing(config); _ = new MotionTiming(config); _ = new ExecutionStallMonitor(config); });
            return J.O(("passed", checks.Count), ("checks", checks));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }
    static JsonObject Snapshot() => J.O(("schema", 1), ("observed_at", J.Now), ("sequence", 1), ("session", "bridge-session"), ("account_key", "bridge-fixture"), ("logged_in", true), ("session_control_supported", true),
        ("ge_open", true), ("input_busy", false), ("cash_inventory_gp", 100), ("inventory", new JsonArray()), ("bank_inventory", new JsonArray()), ("widgets", new JsonArray()), ("offers", J.A(J.O(("slot", 0), ("state", "EMPTY")))));
    sealed class RecordingExchange(JsonObject state) : IBridgeExchange
    {
        public readonly List<JsonObject> Commands = [];
        public string? Account => state.S("account_key"); public bool IsLive => false;
        public JsonObject Observe() => state; public JsonObject Connection() => state;
        public void Send(JsonObject _, JsonObject command) => Commands.Add(command.Copy());
        public void WidgetAction(JsonObject observed, JsonObject widget, string action) => Send(observed, J.O(("type", "widget_action"), ("widget_key", widget.S("key")), ("action", action)));
        public void NpcAction(JsonObject observed, string action) => Send(observed, J.O(("type", "npc"), ("action", action)));
        public int Submit(string side, int itemId, string itemName, long quantity, long price) => throw new NotSupportedException();
        public void Cancel(int slot) => throw new NotSupportedException(); public JsonObject? Collect(int slot) => throw new NotSupportedException(); public JsonObject CollectionAmounts(int slot) => throw new NotSupportedException();
        public void Overview() { } public void RequestStop() { } public void Resume() { }
    }
}
