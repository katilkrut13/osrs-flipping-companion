using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using OsrsFlipping.Core;

namespace OsrsFlipping.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string? output = null;
        try
        {
            output = Option(args, "--smoke-test") ?? Option(args, "--self-test") ?? Option(args, "--connection-check") ?? Option(args, "--maintenance-report") ?? Option(args, "--activity-monitor-check");
            if (Option(args, "--smoke-test") is { } smoke) return Smoke(smoke);
            if (Option(args, "--self-test") is { } tests) return SelfTest(tests);
            if (Option(args, "--activity-monitor-check") is { } activity) return ActivityMonitorDiagnostics.Run(activity);
            var folder = RuntimePaths.Resolve(Option(args, "--data-dir"));
            if (Option(args, "--apply-confirmation") is { } confirmation)
                return ApplyConfirmation(folder, confirmation, output ?? throw new ArgumentException("--maintenance-report gerekli."));
            if (Option(args, "--connection-check") is { } connection)
            {
                var result = J.O(("ok", false), ("runtime_folder", folder), ("implementation", "C#/.NET"), ("live_game_inputs", false));
                try
                {
                    var state = new LiveExchange(folder).Connection();
                    result["ok"] = true; result["observed_at"] = state.L("observed_at"); result["logged_in"] = state.B("logged_in");
                    result["ge_open"] = state.B("ge_open"); result["slot_capacity"] = state.L("ge_slot_capacity"); result["input_enabled"] = state.B("input_enabled");
                }
                catch (Exception ex) { result["error"] = ex.Message; }
                Storage.WriteJson(connection, result); return result.B("ok") ? 0 : 1;
            }
            using var applicationLock = new InstanceLock();
            if (!applicationLock.Acquire(folder)) { MessageBox.Show("Bu veri klasörünü kullanan bir uygulama zaten açık.", "OSRS Flipping Companion"); return 2; }
            RuntimePaths.Ensure(folder);
            var mode = args.Contains("--simulation") ? "SIMULATION" : args.Contains("--history") ? "HISTORY" : "LIVE";
            using var form = new MainForm(folder, mode, args.Contains("--start"));
            Application.Run(form); return 0;
        }
        catch (Exception ex)
        {
            if (output != null) Storage.WriteJson(output, J.O(("ok", false), ("implementation", "C#/.NET"), ("error", ex.ToString()), ("live_game_inputs", false)));
            else
            {
                var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsrsFlipping", "startup-error.log");
                Directory.CreateDirectory(Path.GetDirectoryName(log)!); File.AppendAllText(log, DateTime.Now + " " + ex + Environment.NewLine);
                MessageBox.Show(ex.Message + "\r\n\r\nHata kaydı: " + log, "Program açılamadı", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 1;
        }
    }
    private static int ApplyConfirmation(string folder, string inputPath, string output)
    {
        using var maintenanceLock = new InstanceLock();
        if (!maintenanceLock.Acquire(folder)) throw new InvalidOperationException("Uzlaştırmadan önce programı kapatın; çalışan kayıtlar değiştirilmeyecek.");
        var confirmation = Storage.ReadJson(inputPath).AsObject();
        var config = Storage.ReadJson(Path.Combine(folder, "config.json")).AsObject();
        using var ledger = new Ledger(Path.Combine(folder, "trades.sqlite3"));
        using var exchange = new LiveExchange(folder, ledger);
        var state = exchange.Observe();
        if (confirmation.S("account_key") == "" || confirmation.S("account_key") != state.S("account_key"))
            throw new InvalidOperationException("Doğrulama başka hesaba ait; işlem ve alış limiti kaydı değiştirilmeyecek.");
        ledger.Ingest(state);
        var manager = new OrderManager(exchange, config, Path.Combine(folder, "position-journal.json")); manager.Recover(state);
        var applied = ExternalCollection.Apply(manager, confirmation, state, config);
        if (applied) ledger.Event(state.S("account_key"), "OPERATOR_EXTERNAL_COLLECTION", confirmation, state.L("observed_at"));
        var reconciled = manager.Reconcile(state);
        foreach (var p in manager.Positions.Where(p => p.S("stage") == "DONE" && p.S("account_key") == state.S("account_key"))) ledger.RecordPosition(state.S("account_key"), p);
        if (confirmation.B("confirm_no_untracked_buys")) ledger.ConfirmNoUntrackedBuys(state.S("account_key"), state.L("observed_at"));
        var portfolio = ledger.Ingest(state);
        Storage.WriteJson(output, J.O(("ok", true), ("applied", applied), ("trade_id", confirmation.S("trade_id")),
            ("buy_limits_verified", portfolio.BuyLimitsVerified), ("reconciled", reconciled),
            ("recovery_hold", reconciled ? "" : "RECOVERY_NEEDS_REVIEW"), ("live_game_inputs", false), ("recovery_reviews", ExternalCollection.Reviews(manager, state, config))));
        return 0;
    }
    private static string? Option(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0) return null;
        if (index + 1 == args.Length) throw new ArgumentException(option + " bir dosya yolu gerektirir.");
        return Path.GetFullPath(args[index + 1]);
    }
    private static int SelfTest(string output)
    {
        var folder = Path.Combine(Path.GetTempPath(), "OsrsFlipSelfTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); RuntimePaths.Ensure(folder);
        var config = Storage.ReadJson(Path.Combine(folder, "config.json")).AsObject();
        var market = MarketRegressionChecks.Run(config); var api = MarketRegressionChecks.RunApiChecksAsync(config).GetAwaiter().GetResult();
        var lifecycle = LifecycleRegressionChecks.Run(config); var bridge = BridgeRegressionChecks.Run(config); var worker = WorkerRegressionChecks.Run(config);
        var allocation = AllocationRegressionChecks.Run(config); var recovery = ExternalCollectionRegressionChecks.Run();
        var resilience = ApiResilienceRegressionChecks.RunAsync().GetAwaiter().GetResult();
        var manualOrders = ManualOrderRegressionChecks.Run(config); var manualMargin = ManualMarginRegressionChecks.Run();
        var manualCollections = ManualCollectionRegressionChecks.Run();
        var handoff = UserHandoffRegressionChecks.Run(); var interrupted = InterruptedInput.RunChecks();
        var settlement = GeSettlementRegressionChecks.Run(); var interruptedSubmission = UserInterruptedSubmissionRegressionChecks.Run();
        var liveHandoff = LiveUserHandoffRegressionChecks.Run();
        var manualCompletion = ManualCompletion.RunChecks();
        var groups = J.O(("market", (object?)market), ("market_api", (object?)api), ("lifecycle", (object?)lifecycle), ("bridge", (object?)bridge), ("worker", (object?)worker),
            ("allocation", (object?)allocation), ("external_collection", (object?)recovery), ("api_resilience", (object?)resilience),
            ("manual_orders", (object?)manualOrders), ("manual_margin", (object?)manualMargin), ("manual_collections", (object?)manualCollections),
            ("user_handoff", (object?)handoff), ("interrupted_input", (object?)interrupted), ("ge_settlement", (object?)settlement),
            ("user_interrupted_submission", (object?)interruptedSubmission), ("live_user_handoff", (object?)liveHandoff), ("manual_completion", (object?)manualCompletion));
        var passed = groups.Select(pair => pair.Value?.L("passed") ?? 0).Sum();
        Storage.WriteJson(output, J.O(("ok", true), ("implementation", "C#/.NET"), ("python_runtime_required", false), ("live_game_inputs", false),
            ("passed", passed), ("groups", groups), ("isolated_runtime_folder", folder), ("completed_at", J.Now)));
        return 0;
    }
    private static int Smoke(string output)
    {
        var folder = Path.Combine(Path.GetTempPath(), "OsrsFlipGuiSmoke-" + Guid.NewGuid().ToString("N"));
        RuntimePaths.Ensure(folder);
        var result = J.O(("ok", false), ("implementation", "C#/.NET"), ("python_runtime_required", false), ("live_game_inputs", false), ("mode", "SIMULATION"));
        using var form = new MainForm(folder, "SIMULATION", smoke: true);
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        var deadline = DateTime.UtcNow.AddSeconds(25); var phase = 0;
        timer.Tick += (_, _) =>
        {
            try
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("GUI smoke timed out");
                if (phase == 0 && !form.Worker.Snapshot.ContainsKey("mode")) return;
                if (phase == 0)
                {
                    result["gui_initialized"] = form.IsHandleCreated; result["database_created"] = File.Exists(Path.Combine(folder, "trades.sqlite3"));
                    result["config_seeded"] = File.Exists(Path.Combine(folder, "config.json")); result["start_button"] = form.StartButton.Text == "BAŞLAT";
                    result["stop_button"] = form.StopButton.Text == "DURDUR";
                    var blocked = J.O(("mode", "LIVE"), ("auto", true), ("buy_limits_verified", false),
                        ("hold_reason", "BUY_LIMIT_HISTORY_UNVERIFIED"), ("state", J.O(("logged_in", true))));
                    form.RenderState(blocked);
                    result["buy_history_blocker_visible"] = form.ResolveBlockerButton.Visible && form.BlockerMessage.Text.Contains("yeni emir açılmıyor");
                    blocked["buy_limits_verified"] = true; form.RenderState(blocked);
                    result["buy_history_blocker_clears"] = !form.ResolveBlockerButton.Visible;
                    blocked.Remove("buy_limits_verified"); form.RenderState(blocked);
                    result["partial_status_does_not_fake_history_blocker"] = !form.ResolveBlockerButton.Visible;
                    var fixture = J.O(("account_key", "profit-ui-fixture"), ("trade_id", "profit-ui-trade"), ("item_id", 2), ("item_name", "Cannonball"),
                        ("quantity", 10), ("acquired_quantity", 10), ("owned_quantity", 7), ("buy_spent", 1011), ("sale_proceeds_gp", 350),
                        ("stage", "HELD"), ("created_at", 100), ("started_at", 100));
                    form.RenderProfit(ProfitReport.Build("profit-ui-fixture", 200, [fixture]));
                    result["profit_table_partial_sale"] = form.ProfitTable.Rows.Count == 1
                        && form.ProfitTable.Rows[0].Cells["sold"].Value?.ToString() == "3" && form.ProfitTable.Rows[0].Cells["profit"].Value?.ToString() == "+47";
                    fixture["stage"] = "DONE"; fixture["owned_quantity"] = 0; fixture["sale_proceeds_gp"] = 1060; fixture["completed_at"] = 201;
                    form.RenderProfit(ProfitReport.Build("profit-ui-fixture", 201, [fixture]));
                    result["profit_table_sale_refresh"] = form.ProfitTable.Rows.Count == 1
                        && form.ProfitTable.Rows[0].Cells["sold"].Value?.ToString() == "10" && form.ProfitTable.Rows[0].Cells["profit"].Value?.ToString() == "+49"
                        && form.ProfitSummary.Contains("+49 GP");
                    form.RenderRecovery(J.O(("recovery_reviews", J.A(J.O(("trade_id", "review-fixture"), ("item_name", "Bronze bar"),
                        ("stage", "WAIT_SELL"), ("expected_quantity", 8), ("observed_inventory_quantity", 0), ("reason", "Fixture recovery"))))));
                    result["recovery_table_shows_item_quantity"] = form.RecoveryTable.Rows.Count == 1
                        && form.RecoveryTable.Rows[0].Cells["item"].Value?.ToString() == "Bronze bar"
                        && form.RecoveryTable.Rows[0].Cells["expected"].Value?.ToString() == "8"
                        && form.RecoveryTable.Rows[0].Cells["inventory"].Value?.ToString() == "0"
                        && (form.RecoveryTable.Rows[0].Tag as JsonObject)?.S("trade_id") == "review-fixture";
                    var tabs = Descendants(form).OfType<TabControl>().First(); tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(t => t.Text == "Kâr ve işlemler");
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); var image = Path.ChangeExtension(output, ".png"); bitmap.Save(image, ImageFormat.Png); result["preview"] = image; }
                    form.Worker.Post("demo"); form.StartButton.PerformClick(); phase = 1;
                }
                else if (phase == 1 && form.Worker.Auto)
                { result["start_button_activates_cycle"] = true; form.ManualButton.PerformClick(); phase = 2; }
                else if (phase == 2 && !form.Worker.Auto && form.Worker.Snapshot.B("manual_control"))
                { result["manual_control_pauses_without_cleanup"] = !form.Worker.Snapshot.B("cleanup_pending"); form.StartButton.PerformClick(); phase = 3; }
                else if (phase == 3 && form.Worker.Auto && !form.Worker.Snapshot.B("manual_control"))
                { result["start_resumes_manual_control"] = true; form.StopButton.PerformClick(); form.ManualButton.PerformClick(); phase = 4; }
                else if (phase == 4 && form.Worker.Snapshot.B("manual_control"))
                { form.StartButton.PerformClick(); phase = 5; }
                else if (phase == 5 && form.Worker.Auto && !form.Worker.Snapshot.B("manual_control"))
                { result["manual_pause_during_cleanup_resumes_without_deadlock"] = true; form.StopButton.PerformClick(); phase = 6; }
                else if (phase == 6 && !form.Worker.Auto && form.Worker.Snapshot.B("cleanup_pending") == false)
                {
                    result["stop_button_stops_cycle"] = true;
                    result["ok"] = new[] { "gui_initialized", "database_created", "config_seeded", "start_button", "stop_button", "buy_history_blocker_visible", "buy_history_blocker_clears", "partial_status_does_not_fake_history_blocker", "profit_table_partial_sale", "profit_table_sale_refresh", "recovery_table_shows_item_quantity", "start_button_activates_cycle", "manual_control_pauses_without_cleanup", "start_resumes_manual_control", "manual_pause_during_cleanup_resumes_without_deadlock", "stop_button_stops_cycle" }.All(key => result.B(key));
                    timer.Stop(); form.Close();
                }
            }
            catch (Exception ex) { result["ok"] = false; result["error"] = ex.ToString(); timer.Stop(); form.Close(); }
        };
        form.Shown += (_, _) => timer.Start(); Application.Run(form);
        result["worker_exited"] = !form.Worker.IsRunning; result["ok"] = result.B("ok") && result.B("worker_exited");
        Storage.WriteJson(output, result); return result.B("ok") ? 0 : 1;
    }
    private static IEnumerable<Control> Descendants(Control root)
    { foreach (Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
}
