using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

/// <summary>One serialized account worker; independent read-only scans never submit game input.</summary>
public sealed class EngineWorker : IDisposable
{
    private readonly string folder;
    private readonly BlockingCollection<(string Command, JsonObject Payload)> commands = new();
    private readonly CancellationTokenSource halt = new();
    private readonly Func<string, Ledger, IExchange>? exchangeFactory;
    private readonly Func<JsonObject, CancellationToken, Task<JsonObject>>? scanOverride;
    private readonly Func<List<JsonObject>, JsonObject, CancellationToken, Task<JsonObject>>? exitsOverride;
    private Task? task;
    private volatile bool auto, cleanupRequested, parkRequested, queuedStart, manualHold;
    private long nextQueuedStartAt;
    private AutomaticUserHandoff userHandoff = new();
    private UserHandoffDecision? handoffDecision;
    private volatile bool activityMonitorOperational, activityMonitorBound;
    private string? activityMonitorError;
    private long loggedHandoffSequence;
    private string mode;
    private JsonObject config = new(), lastSnapshot = new();
    private Ledger ledger = null!;
    private IExchange backend = null!;
    private OrderManager manager = null!;
    private MarginRunner runner = null!;
    private ShutdownCoordinator shutdown = null!;
    private SessionController? session;
    private ExecutionStallMonitor monitor = null!;
    private MarketAnalyzer market = null!;
    private JsonObject? report, exitReport;
    private string? exitAccount, recoveryHold, lastRiskHold, lastError, profitSignature, lastStallHold;
    private bool reconnectNeeded = true, stallResetRequested, lastWorkAdvanced;
    private int generation, scanRunning, exitScanRunning, maxSlots;
    private long lastScan, lastExitScan;
    private long? cycleStarted, cycleStopped;
    private int? selected;
    private readonly Dictionary<int, JsonObject> verified = [], entryFailures = [];
    public ConcurrentQueue<(string Kind, JsonObject Payload)> Events { get; } = new();
    public bool Auto => auto;
    public bool IsRunning => task != null && !task.IsCompleted;
    public string Mode => mode;
    public string? LastError => lastError;
    public JsonObject Snapshot => lastSnapshot.Copy();
    public IExchange? Exchange => backend;
    public void SetActivityMonitorState(bool operational, bool bound, string? error)
    { activityMonitorOperational = operational; activityMonitorBound = bound; activityMonitorError = error; }
    public void NotifyPhysicalUserActivity()
    {
        if (!config.B("pause_on_physical_input", true)) return;
        if (mode != "LIVE" || manualHold || !(auto || cleanupRequested || parkRequested || queuedStart)) return;
        userHandoff.RecordActivity(Environment.TickCount64, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d);
        (backend as LiveExchange)?.InterruptForUser();
    }

    public EngineWorker(string folder, string initialMode = "LIVE", Func<string, Ledger, IExchange>? exchangeFactory = null,
        Func<JsonObject, CancellationToken, Task<JsonObject>>? scanOverride = null,
        Func<List<JsonObject>, JsonObject, CancellationToken, Task<JsonObject>>? exitsOverride = null)
    { this.folder = folder; mode = initialMode; this.exchangeFactory = exchangeFactory; this.scanOverride = scanOverride; this.exitsOverride = exitsOverride; }
    public void Start()
    {
        if (task != null) return;
        task = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public void Post(string command, JsonObject? payload = null)
    {
        if (halt.IsCancellationRequested) return;
        if (command is "start" or "pause" or "manual")
            Storage.WriteJson(PathFor("desired-run-state.json"), J.O(("state", command == "start" ? "RUN" : command == "manual" ? "MANUAL" : "STOP"), ("updated_at", J.Now)));
        if (command == "pause") { manualHold = false; Stop(true); }
        if (command == "manual") { manualHold = true; Stop(false); }
        commands.Add((command, payload ?? new JsonObject()));
    }
    public void Stop(bool cancelOffers = false)
    {
        if (auto) cycleStopped = J.Now;
        auto = false; queuedStart = false;
        if (cancelOffers) { if (!parkRequested) cleanupRequested = true; parkRequested = true; }
        backend?.RequestStop();
    }
    public void Dispose()
    {
        Stop(); halt.Cancel();
        if (task != null && !task.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("İşçi kapanışı henüz tamamlanmadı; uygulama kilidi korunmalı.");
        if (backend is IDisposable disposable) disposable.Dispose();
        halt.Dispose(); commands.Dispose();
    }
    private string PathFor(string name) => Path.Combine(folder, name);
    private void Emit(string kind, JsonObject payload)
    {
        Events.Enqueue((kind, payload.Copy()));
        if (kind == "log")
        {
            var log = payload.Copy(); log["at"] = J.Now;
            File.AppendAllText(PathFor("engine-events.jsonl"), log.ToJsonString() + Environment.NewLine, System.Text.Encoding.UTF8);
        }
    }
    private void Log(string text) => Emit("log", J.O(("text", text)));
    private JsonObject Telemetry(int? slots = null)
    {
        if (auto && slots != null) maxSlots = Math.Max(maxSlots, slots.Value);
        var end = auto ? J.Now : cycleStopped;
        return J.O(("cycle_started_at", cycleStarted), ("cycle_uptime_seconds", cycleStarted != null && end != null ? Math.Max(0, end.Value - cycleStarted.Value) : 0),
            ("max_slots_used_this_cycle", maxSlots));
    }
    private void Publish(string? reason, JsonObject? state = null, Portfolio? portfolio = null, JsonObject? extra = null)
    {
        var status = Telemetry(portfolio?.Offers.Count);
        status["observed_at"] = state?.L("observed_at") ?? J.Now; status["mode"] = mode; status["implementation"] = "C#/.NET";
        status["auto"] = auto; status["last_error"] = lastError; status["hold_reason"] = reason;
        status["manual_control"] = manualHold;
        status["automatic_user_pause"] = userHandoff.IsHolding;
        status["user_activity"] = J.O(("holding", userHandoff.IsHolding), ("reason", handoffDecision?.Reason),
            ("quiet_remaining_seconds", handoffDecision?.QuietRemainingSeconds ?? 0), ("activity_sequence", userHandoff.ActivitySequence),
            ("monitor_operational", activityMonitorOperational), ("monitor_bound", activityMonitorBound), ("monitor_error", activityMonitorError));
        status["hold_description"] = EngineRules.Describe(reason);
        status["cleanup_pending"] = cleanupRequested || parkRequested || (shutdown?.Pending ?? false) || (session?.Pending ?? false);
        status["start_after_cleanup"] = queuedStart; status["input_stall"] = monitor?.Status.DeepClone();
        if (portfolio != null)
        {
            status["cash_inventory_gp"] = portfolio.FreeCashGp; status["active_offers"] = portfolio.Offers.Count;
            status["slot_capacity"] = LimitRules.SlotCapacity(config, portfolio); status["buy_limits_verified"] = portfolio.BuyLimitsVerified;
            status["deployed_cost_gp"] = portfolio.DeployedCostGp;
        }
        else if (state != null)
        { status["cash_inventory_gp"] = state["cash_inventory_gp"]?.DeepClone(); status["active_offers"] = state.Objects("offers").Count(o => o.S("state") != "EMPTY"); }
        if (state != null)
        {
            status["canvas_focused"] = state.B("canvas_focused"); status["background_input_supported"] = state.B("background_input_supported");
            status["state"] = state.DeepClone();
        }
        if (manager != null) { status["positions"] = J.A(manager.Positions.Cast<JsonNode?>()); status["position_stages"] = J.A(manager.Positions.Select(p => p["stage"])); }
        if (manager != null && state != null) status["recovery_reviews"] = ExternalCollection.Reviews(manager, state, config, runner?.Test);
        status["margin"] = runner?.Test?.DeepClone(); status["margin_stage"] = runner?.Test?.S("stage");
        status["stop_stage"] = shutdown?.Plan?.S("stage"); status["bank_task_stage"] = session?.Task?.S("stage");
        status["eligible_candidates"] = report?.Objects("candidates").Count(c => c.B("strategy_eligible")) ?? 0;
        status["entry_retry_status"] = J.A(entryFailures.Values.Select(e =>
        { var r = e.Copy(); r["cooldown_active"] = r.L("retry_after") > J.Now; return (JsonNode?)r; }));
        if (backend is LiveExchange live) status["input_pacing"] = live.InputPacingStatus();
        if (extra != null) foreach (var pair in extra) status[pair.Key] = pair.Value?.DeepClone();
        lastSnapshot = status;
        if (mode == "LIVE") { var disk = status.Copy(); disk.Remove("state"); disk.Remove("positions"); disk.Remove("margin"); Storage.WriteJson(PathFor("runtime-status.json"), disk); }
        Emit("state", status);
    }
    private void PublishProfit(JsonObject state)
    {
        var account = state.S("account_key"); if (account == "") return;
        var now = state.L("observed_at");
        foreach (var p in manager.Positions.Where(p => p.S("account_key") == account && p.S("stage") == "DONE")) ledger.RecordPosition(account, p);
        var t = runner.Test;
        if (t != null && t.S("account_key") == account && t.S("stage") == "DONE" && t["result"] is JsonObject result)
        {
            var r = result.Copy(); r["item_name"] = t.S("item_name"); r["started_at"] = t.L("started_at"); r["account_key"] = account;
            ledger.RecordMarginResult(account, r, now);
        }
        var history = ledger.ProfitHistory(account, now);
        var names = manager.Positions.Where(p => p.S("account_key") == account).GroupBy(p => p.I("item_id")).ToDictionary(g => g.Key, g => g.Last().S("item_name"));
        foreach (var c in report?.Objects("candidates") ?? []) names[c.I("id")] = c.S("name");
        if (t != null && t.S("account_key") == account) names[t.I("item_id")] = t.S("item_name");
        foreach (var r in history.Objects("margin_results"))
            if (r.S("item_name") == "") r["item_name"] = names.GetValueOrDefault(r.I("item_id"), $"Eşya #{r.I("item_id")}");
        var profit = ProfitReport.Build(account, now, manager.Positions, t, state.Objects("offers"), history.Objects("closed_positions"), history.Objects("margin_results"));
        profit["mode"] = mode; var copy = profit.Copy(); copy.Remove("generated_at"); var signature = copy.ToJsonString();
        if (signature == profitSignature) return;
        profitSignature = signature; if (mode == "LIVE") Storage.WriteJson(PathFor("profit-report.json"), profit);
        Emit("profit", J.O(("report", profit)));
    }
    private void SwitchMode(string newMode)
    {
        Stop(); generation++; scanRunning = exitScanRunning = 0; report = exitReport = null; exitAccount = null; lastScan = lastExitScan = 0;
        cleanupRequested = parkRequested = false; queuedStart = manualHold = false; mode = newMode;
        userHandoff = new(); handoffDecision = null; loggedHandoffSequence = 0;
        if (mode is not ("LIVE" or "SIMULATION" or "HISTORY")) throw new ArgumentException("Unknown mode");
        backend = exchangeFactory?.Invoke(mode, ledger) ?? (mode == "LIVE" ? new LiveExchange(folder, ledger) : mode == "HISTORY" ? new HistoryExchange() : new SimExchange(config));
        if (backend is SimExchange sim) sim.Now = J.Now;
        if (backend is LiveExchange live) live.UserInputBlocked = () => config.B("pause_on_physical_input", true) && userHandoff.IsHolding;
        runner = new(backend, config, mode == "LIVE" ? PathFor("margin-journal.json") : null);
        manager = new(backend, config, mode == "LIVE" ? PathFor("position-journal.json") : null);
        shutdown = new(backend, mode == "LIVE" ? PathFor("shutdown-journal.json") : null); shutdown.Recover();
        session = mode == "LIVE" && backend is IBridgeExchange bridge ? new(bridge, config, PathFor("bank-task.json")) : null;
        parkRequested = shutdown.Pending;
        if (mode == "LIVE") { runner.Recover(); manager.Recover(); }
        verified.Clear(); entryFailures.Clear(); reconnectNeeded = true; recoveryHold = null; lastError = null; profitSignature = null;
        monitor.Reset(); stallResetRequested = false;
        Emit("profit", J.O(("report", J.O(("mode", mode), ("rows", new JsonArray()), ("totals", new JsonObject()), ("account_key", null)))));
        Log($"Mode: {mode}. C# engine initialized; automatic trading stopped.");
    }
    private void RequestScan()
    {
        if (Interlocked.CompareExchange(ref scanRunning, 1, 0) != 0) return;
        var gen = generation; lastScan = J.Now;
        _ = Task.Run(async () =>
        {
            try
            {
                var r = scanOverride != null ? await scanOverride(config, halt.Token) : await market.AnalyzeAsync(halt.Token);
                Post("market_ready", J.O(("report", r), ("generation", gen)));
            }
            catch (Exception ex) { Post("market_error", J.O(("text", ex.Message), ("generation", gen))); }
        });
    }
    private void RequestExitScan(JsonObject state)
    {
        if (!auto || mode != "LIVE" || !backend.IsLive || !state.B("logged_in") || J.Now - lastExitScan < 30 || exitScanRunning != 0) return;
        var items = manager.Positions.Where(p => p.S("account_key") == state.S("account_key") && ActiveOwned(p))
            .Select(p => J.O(("id", p.I("item_id")), ("name", p.S("item_name")))).ToList();
        if (items.Count == 0 || Interlocked.CompareExchange(ref exitScanRunning, 1, 0) != 0) return;
        var gen = generation; var account = state.S("account_key"); lastExitScan = J.Now;
        _ = Task.Run(async () =>
        {
            try
            {
                var r = exitsOverride != null ? await exitsOverride(items, config, halt.Token) : await market.FetchExitQuotesAsync(items, halt.Token);
                Post("exits_ready", J.O(("report", r), ("generation", gen), ("account", account)));
            }
            catch (Exception ex) { Post("exits_error", J.O(("text", ex.Message), ("generation", gen))); }
        });
    }
    private static bool ActiveOwned(JsonObject p) => EngineRules.Active(p) && p.L("owned_quantity") > 0;
    private bool WatchInputs(JsonObject state)
    {
        var live = backend as LiveExchange;
        var active = auto && !shutdown.Pending && !cleanupRequested && !parkRequested;
        if (active && stallResetRequested)
        {
            var bank = session?.Task; var uncertain = bank != null && bank.S("stage") is "ENTERING" or "VERIFY" or "REVIEW";
            if (bank != null && bank.S("stage") is "ENTERING" or "VERIFY")
            {
                var sign = bank.S("kind") == "WITHDRAW" ? 1 : -1;
                uncertain = bank.S("account_key") != state.S("account_key")
                    || EngineRules.CountItem(state, bank.I("item_id")) != bank.L("inventory_before") + sign * bank.L("quantity")
                    || EngineRules.BankCount(state, bank.I("item_id")) != bank.L("bank_before") - sign * bank.L("quantity");
            }
            if (live?.PendingId == null && !(live?.ExecutionPhase.B("active") ?? false) && !uncertain && manager.Reconcile(state, allowIsolatedOwnedSales: true) && runner.Reconcile(state)) monitor.Reset();
            stallResetRequested = false;
        }
        var status = monitor.Observe(state, manager.Positions, runner.Test, session?.Task, live?.PendingId, live?.ExecutionPhase, session?.Reason, active);
        var hold = active ? status.S("hold_reason") : "";
        if (active && hold == "" && (live?.PendingId != null || (live?.ExecutionPhase.B("active") ?? false))) hold = "RECONCILING_DELAYED_OPERATION";
        if (hold != lastStallHold) { lastStallHold = hold; if (hold != "") Log("Input progress hold: " + status.S("reason")); }
        if (hold == "") return false;
        PublishProfit(state); Publish(hold, state, extra: J.O(("input_stall", status))); return true;
    }
    private (JsonObject State, Portfolio Portfolio) AdvanceWork(JsonObject state, JsonObject? current, bool allowNew)
    {
        lastWorkAdvanced = false;
        if (manager.Positions.Any(p => EngineRules.CollectionStages.Contains(p.S("stage"))) || runner.Test?.S("stage") == "NEW" && !allowNew)
            lastWorkAdvanced = manager.Step(state, current);
        else if (runner.Test != null && runner.Test.S("stage") is not ("DONE" or "STOPPED"))
        {
            var initial = runner.Test.S("stage"); lastWorkAdvanced = runner.Step(state);
            if (initial == "NEW" && runner.Test.S("stage") == "WAIT_BUY") entryFailures.Remove(runner.Test.I("item_id"));
            state = backend.Observe();
            if (!lastWorkAdvanced && auto && !state.B("input_busy") && (backend as LiveExchange)?.PendingId == null)
                lastWorkAdvanced = manager.Step(state, current);
        }
        else lastWorkAdvanced = manager.Step(state, current);
        state = backend.Observe(); return (state, EngineRules.ExposeCostBasis(ledger.Ingest(state), manager, runner));
    }
    private JsonObject EntryReport(JsonObject current, long now)
    {
        var result = current.Copy();
        foreach (var c in result.Objects("candidates"))
        {
            if (!entryFailures.TryGetValue(c.I("id"), out var f) || f.L("retry_after") <= now) continue;
            c["strategy_eligible"] = false; c["entry_retry_after"] = f.L("retry_after"); c.Arr("strategy_reasons").Add("ENTRY_RETRY_COOLDOWN");
        }
        return result;
    }
    private void ApplyTransfer(JsonObject completed)
    {
        ledger.Event(completed.S("account_key"), "BANK_TRANSFER", completed, completed.L("completed_at", J.Now));
        if (completed.B("skipped"))
        {
            if (completed.S("purpose") == "RESUME_OWNED_STOCK")
                foreach (var p in manager.Positions.Where(p => p.S("trade_id") == completed.S("trade_id") && completed.S("trade_id") != ""))
                {
                    p["stage"] = "STOPPED"; p["recovery_stage"] = "PAUSED_HELD"; p["storage_location"] = "UNKNOWN";
                    p["last_risk_reason"] = "BANK_STOCK_UNAVAILABLE";
                    p["manual_review"] = J.O(("reason", "BANK_STOCK_UNAVAILABLE"), ("observed_at", completed.L("completed_at", J.Now)), ("account_key", completed.S("account_key")));
                }
            manager.Save(); return;
        }
        foreach (var p in manager.Positions.Where(p => p.S("trade_id") == completed.S("trade_id") && completed.S("trade_id") != ""))
            p["storage_location"] = completed.S("kind") == "DEPOSIT" ? "BANK" : "INVENTORY";
        if (runner.Test != null && completed.S("trade_id") == "margin:" + runner.Test.L("started_at"))
        { runner.Test["storage_location"] = completed.S("kind") == "DEPOSIT" ? "BANK" : "INVENTORY"; runner.Save(); }
        manager.Save();
    }
    private void ReconcileObservedChanges(JsonObject state)
    {
        if (!state.B("logged_in") || !state.B("ge_open") || state.B("input_busy")) return;
        // Shutdown owns these settlements until acknowledgement. Normal recovery
        // must not consume the same receipt first and change its owned quantity.
        if (shutdown.Pending || cleanupRequested) return;
        var confirmationsPath = PathFor("manual-completion-confirmations.json");
        if (File.Exists(confirmationsPath))
            foreach (var confirmation in Storage.ReadJson(confirmationsPath).AsArray().OfType<JsonObject>())
                foreach (var position in manager.Positions)
                    if (ManualCompletion.Apply(position, confirmation, state))
                    {
                        manager.Save(); ledger.Event(state.S("account_key"), "USER_COMPLETED_EXTERNALLY_PROFIT_UNKNOWN", confirmation, state.L("observed_at"));
                        Log(position.S("item_name") + " kullanıcı doğrulamasıyla elle tamamlanan olarak arşivlendi; bilinmeyen kâr toplama eklenmedi.");
                    }
        EngineRules.SyncStorage(manager, state);
        manager.ReviewManualChanges(state); runner.ReviewManualChanges(state);
        ArchiveMissingSales(state);
        if (!reconnectNeeded && !manager.Positions.Any(p => p.S("stage") == "STOPPED") && runner.Test?.S("stage") != "STOPPED") return;
        var ok = manager.Reconcile(state, allowIsolatedOwnedSales: true) & runner.Reconcile(state);
        recoveryHold = ok ? null : "RECOVERY_NEEDS_REVIEW"; reconnectNeeded = !ok;
        foreach (var p in manager.Positions.Concat(runner.Test == null ? [] : new[] { runner.Test }))
        {
            if (p.S("stage") != "STOPPED" || p.S("account_key") != state.S("account_key") || p.S("recovery_stage") != "WAIT_BUY"
                || !(p.Obj("manual_review").S("reason").EndsWith("OFFER_MISSING") || p.Obj("manual_review").S("reason").EndsWith("OFFER_CHANGED")) || p.B("buy_limit_review_recorded")) continue;
            ledger.InvalidateBuyHistory(state.S("account_key"), state.L("observed_at"), p.Obj("manual_review"));
            p["buy_limit_review_recorded"] = true; manager.Save(); runner.Save();
            Log("Kaybolan/değişen alışın son dolumu doğrulanamadı; alış limiti beyanı gerekiyor: " + p.S("item_name"));
        }
    }
    private void ArchiveMissingSales(JsonObject state)
    {
        var changed = false;
        foreach (var p in manager.Positions.Where(p => ManualCompletion.CanArchiveMissingSale(p, state)).ToArray())
        {
            p["stage"] = "DONE"; p["recovery_stage"] = ""; p["completed_at"] = state["observed_at"]?.DeepClone();
            p["realized_profit_gp"] = null; p["profit_unknown"] = true;
            p["last_risk_reason"] = "MISSING_EXTERNAL_SALE_ARCHIVED_PROFIT_UNKNOWN"; p.Remove("manual_review");
            ledger.Event(state.S("account_key"), "MISSING_EXTERNAL_SALE_ARCHIVED_PROFIT_UNKNOWN", p, state.L("observed_at"));
            Log(p.S("item_name") + ": kayıp eski satış yeni işlemleri durdurmaması için bilinmeyen kârla arşivlendi."); changed = true;
        }
        if (changed) manager.Save();
        if (runner.Test is not JsonObject margin || !ManualCompletion.CanArchiveMissingSale(margin, state)) return;
        var archivePath = PathFor("unresolved-margin-history.json");
        var archive = File.Exists(archivePath) ? Storage.ReadJson(archivePath).AsArray() : new JsonArray();
        if (!archive.OfType<JsonObject>().Any(t => t.S("account_key") == margin.S("account_key") && t.L("started_at") == margin.L("started_at")))
            archive.Add(margin.Copy());
        Storage.WriteJson(archivePath, archive);
        ledger.Event(state.S("account_key"), "MARGIN_PROCEEDS_UNKNOWN", margin, state.L("observed_at"));
        Log(margin.S("item_name") + ": eski margin satışı bilinmeyen sonuçla arşivlendi; yeni testler devam edebilir.");
        margin["stage"] = "DONE"; margin["recovery_stage"] = ""; margin["completed_at"] = state["observed_at"]?.DeepClone();
        margin["profit_unknown"] = true; margin["result"] = null; margin.Remove("manual_review"); runner.Save();
    }
    private void PreserveInterruptedSubmissions()
    {
        if (backend is not LiveExchange live || live.InterruptedSubmission is not JsonObject evidence) return;
        foreach (var p in manager.Positions.Concat(runner.Test == null ? [] : new[] { runner.Test }))
        {
            if (p.S("stage") is not ("BUY_SUBMITTING" or "SELL_SUBMITTING") || p.I("item_id") != evidence.I("item_id")
                || p.S("account_key") != evidence.S("account_key")
                || (p.S("stage") == "BUY_SUBMITTING" ? "BUY" : "SELL") != evidence.S("side")) continue;
            p["user_interrupted_submission"] = evidence.Copy(); p["recovery_stage"] = p["stage"]?.DeepClone(); p["stage"] = "STOPPED";
        }
        manager.Save(); runner.Save();
    }
    private bool HoldForPhysicalUser(JsonObject state)
    {
        if (mode != "LIVE") return false;
        if (!config.B("pause_on_physical_input", true))
        {
            handoffDecision = null; loggedHandoffSequence = 0; return false;
        }
        handoffDecision = userHandoff.Evaluate(Environment.TickCount64, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d,
            state, auto || cleanupRequested || parkRequested || queuedStart || shutdown.Pending);
        if (!handoffDecision.Holding) return false;
        var live = backend as LiveExchange; live?.CancelPending();
        if (loggedHandoffSequence == 0)
            Log("RuneLite'ta kullanıcı müdahalesi algılandı; otomatik girdiler duraklatıldı, açık teklifler korunuyor.");
        loggedHandoffSequence = handoffDecision.ActivitySequence;
        if (handoffDecision.ResumeReady)
        {
            if (live != null && !live.TrySettleInterruptedInput(state))
            { Publish("USER_ACTIVITY_PENDING_INPUT", state); return true; }
            reconnectNeeded = true; ReconcileObservedChanges(state); PublishProfit(state);
            if (!userHandoff.AcknowledgeResume(handoffDecision.ActivitySequence)) { Publish("USER_ACTIVITY_ACTIVE", state); return true; }
            backend.Resume(); stallResetRequested = true; loggedHandoffSequence = 0; handoffDecision = null;
            Log("Elle müdahale bitti; güncel teklifler ve tahsilatlar uzlaştırıldı, otomatik döngü devam ediyor.");
            return false;
        }
        // Observe while held, but do not infer an incomplete transport's outcome.
        if (live?.PendingId == null) ReconcileObservedChanges(state);
        if (state.B("logged_in")) PublishProfit(state);
        Publish(handoffDecision.Reason, state, state.B("logged_in") ? EngineRules.ExposeCostBasis(ledger.Ingest(state), manager, runner) : null);
        return true;
    }
    private void Run()
    {
        try
        {
            config = Storage.ReadJson(PathFor("config.json")).AsObject(); monitor = new(config); market = new(config);
            using var db = new Ledger(PathFor("trades.sqlite3")); ledger = db; SwitchMode(mode);
            while (!halt.IsCancellationRequested)
            {
                commands.TryTake(out var item, 500); var command = item.Command ?? "tick"; var payload = item.Payload ?? new();
                try { Tick(command, payload); }
                catch (UserIntervention)
                {
                    PreserveInterruptedSubmissions(); reconnectNeeded = true; (backend as LiveExchange)?.CancelPending();
                    Publish("USER_ACTIVITY_ACTIVE");
                }
                catch (InputRejected) when (userHandoff.IsHolding)
                {
                    PreserveInterruptedSubmissions(); reconnectNeeded = true; (backend as LiveExchange)?.CancelPending();
                    Publish("USER_ACTIVITY_ACTIVE");
                }
                catch (OfferNotSubmitted ex)
                {
                    reconnectNeeded = true;
                    if (ex.Side == "BUY")
                    {
                        entryFailures.TryGetValue(ex.ItemId, out var old); var cooldown = Math.Max(1, config.L("entry_retry_cooldown_seconds", 600));
                        var count = old != null && J.Now - old.L("last_failure_at") < cooldown ? old.L("failure_count") + 1 : 1;
                        entryFailures[ex.ItemId] = J.O(("item_id", ex.ItemId), ("failure_count", count), ("last_failure_at", J.Now),
                            ("retry_after", count >= config.L("entry_retry_max_failures", 2) ? J.Now + cooldown : null), ("phase", ex.Phase), ("details", ex.Details));
                    }
                    Log($"Offer not submitted: {ex.ItemId}, {ex.Side}, {ex.Phase}; reconcile before bounded retry.");
                    Publish(entryFailures.GetValueOrDefault(ex.ItemId)?.L("retry_after") > J.Now ? "NEW_ENTRY_ITEM_RETRY_COOLDOWN" : "RETRYING_ITEM_ENTRY");
                }
                catch (TimeoutException ex) { PreserveInterruptedSubmissions(); reconnectNeeded = true; (backend as LiveExchange)?.CancelPending(); Log("Delayed outcome: " + ex.Message); Publish("RECONCILING_DELAYED_OPERATION"); }
                catch (Exception ex) when (ex is ConnectionUnavailable or FileNotFoundException or UnauthorizedAccessException or JsonException)
                { reconnectNeeded = true; Emit("connection", J.O(("text", ex.Message))); Publish("WAITING_FOR_CONNECTION"); }
                catch (InputRejected ex) when (ex.NoDispatch)
                { reconnectNeeded = true; Log("Bridge rejected before dispatch: " + ex.Message); Publish("REFRESHING_REJECTED_INPUT"); }
                catch (Exception ex)
                {
                    if (manualHold && (ex is OperationCanceledException or InputRejected || ex.Message is "Stopped" or "Live input disabled or stopped"))
                    { PreserveInterruptedSubmissions(); reconnectNeeded = true; Publish("MANUAL_CONTROL_PAUSED"); continue; }
                    if (!auto && (cleanupRequested || halt.IsCancellationRequested) && (ex is OperationCanceledException or InputRejected || ex.Message is "Stopped" or "Live input disabled or stopped"))
                    { PreserveInterruptedSubmissions(); reconnectNeeded = true; Publish(cleanupRequested ? "CANCELLING_AND_COLLECTING" : "CYCLE_STOPPED"); continue; }
                    PreserveInterruptedSubmissions(); lastError = $"{ex.GetType().Name}: {ex.Message}"; Stop();
                    if (session?.Task?.S("stage") == "REVIEW") parkRequested = false;
                    foreach (var p in manager.Positions.Concat(runner.Test == null ? [] : new[] { runner.Test }))
                        if (p.S("stage") is "BUY_SUBMITTING" or "SELL_SUBMITTING") { p["recovery_stage"] = p.S("stage"); p["stage"] = "STOPPED"; }
                    manager.Save(); runner.Save(); Log("Stopped: " + lastError); Publish(lastError);
                }
            }
        }
        catch (Exception ex) { lastError = ex.ToString(); auto = false; Emit("log", J.O(("text", "Engine initialization failed: " + ex.Message))); }
    }

    private void Tick(string command, JsonObject payload)
    {
        var desiredPath = PathFor("desired-run-state.json");
        var desired = File.Exists(desiredPath) ? Storage.ReadJson(desiredPath).S("state") : "";
        if (command == "tick" && desired == "RUN" && !auto && !manualHold && !queuedStart) command = "start";
        if (command == "tick" && mode == "LIVE") command = OperatorControl.Read(folder, backend.Account) ?? command;
        switch (command)
        {
            case "mode": SwitchMode(payload.S("mode")); break;
            case "scan": RequestScan(); break;
            case "market_ready" when payload.L("generation", generation) == generation:
                scanRunning = 0; report = payload.Obj("report").Copy(); ledger.RegisterLimitMetadata(report.Arr("limit_metadata"));
                if (mode == "LIVE") Storage.WriteJson(PathFor("market-report.json"), report);
                Emit("market", J.O(("report", report))); break;
            case "market_error" when payload.L("generation", generation) == generation:
                scanRunning = 0; Log("Market scan unavailable; fresh data required: " + payload.S("text")); break;
            case "exits_ready" when payload.L("generation", -1) == generation:
                exitScanRunning = 0;
                if (mode == "LIVE" && payload.S("account") == backend.Account)
                { exitReport = payload.Obj("report").Copy(); exitAccount = payload.S("account"); }
                break;
            case "exits_error" when payload.L("generation", -1) == generation:
                exitScanRunning = 0; Log("Owned-stock price refresh unavailable: " + payload.S("text")); break;
            case "demo":
                if (mode != "SIMULATION") throw new InvalidOperationException("Demo verisi yalnızca simülasyonda kullanılabilir.");
                report = DemoData.Report(J.Now); Emit("market", J.O(("report", report))); break;
            case "start":
                if (File.Exists(desiredPath) && Storage.ReadJson(desiredPath).S("state") != "RUN") break;
                if (mode == "HISTORY") throw new InvalidOperationException("Geçmiş modu oyun emri gönderemez.");
                manualHold = false; queuedStart = true;
                if (shutdown.Plan?.S("stage") == "REVIEW")
                {
                    JsonObject startState;
                    try { startState = backend.Connection(); }
                    catch (ConnectionUnavailable)
                    {
                        nextQueuedStartAt = J.Now + 3; Publish("START_WAITING_FOR_CONNECTION"); break;
                    }
                    if (!startState.B("logged_in"))
                    {
                        nextQueuedStartAt = J.Now + 3; Publish("START_WAITING_FOR_LOGIN", startState); break;
                    }
                    if (!shutdown.ResumeReview(startState))
                        throw new InvalidOperationException("Durdurma temizliği uzlaştırılamadı: " + shutdown.Plan.S("review_reason"));
                }
                if (shutdown.Plan?.S("stage") == "DONE" && runner.Test != null)
                {
                    try
                    {
                        var completedStopState = backend.Connection();
                        if (completedStopState.B("logged_in") && runner.ResolveSubmissionAfterCompletedStop(completedStopState))
                        {
                            monitor.Reset(); stallResetRequested = false;
                            Log("Önceki durdurmadan kalan gönderilmemiş margin emri temizlendi.");
                        }
                    }
                    catch (ConnectionUnavailable) { nextQueuedStartAt = J.Now + 3; Publish("START_WAITING_FOR_CONNECTION"); break; }
                }
                if (runner.Test is JsonObject unresolvedMargin && unresolvedMargin.S("stage") == "STOPPED"
                    && unresolvedMargin.S("recovery_stage") == "WAIT_SELL"
                    && shutdown.Plan?.Objects("entries").Any(e => e.S("stage") == "UNRESOLVED"
                        && Lifecycle.Slot(e.Obj("offer")) == Lifecycle.Slot(unresolvedMargin)
                        && e.Obj("offer").I("item_id") == unresolvedMargin.I("item_id")) == true)
                {
                    var archivePath = PathFor("unresolved-margin-history.json");
                    var archive = File.Exists(archivePath) ? Storage.ReadJson(archivePath).AsArray() : new JsonArray();
                    if (!archive.OfType<JsonObject>().Any(t => t.S("account_key") == unresolvedMargin.S("account_key") && t.L("started_at") == unresolvedMargin.L("started_at")))
                        archive.Add(unresolvedMargin.Copy());
                    Storage.WriteJson(archivePath, archive);
                    ledger.Event(unresolvedMargin.S("account_key"), "MARGIN_PROCEEDS_UNKNOWN", unresolvedMargin, J.Now);
                    Log(unresolvedMargin.S("item_name") + ": tahsilat tutarı doğrulanamadı; ayrı inceleme kaydına taşındı, kâr toplamına eklenmedi.");
                    unresolvedMargin["stage"] = "DONE"; unresolvedMargin["profit_unknown"] = true; runner.Save(); runner.Test = null;
                }
                if (cleanupRequested || shutdown.Pending || parkRequested) { nextQueuedStartAt = J.Now + 3; Log("Start queued until stop cleanup finishes."); break; }
                if (!auto) { cycleStarted = J.Now; cycleStopped = null; maxSlots = 0; }
                manualHold = false; auto = true; queuedStart = false; nextQueuedStartAt = 0; backend.Resume(); lastError = null; recoveryHold = null; reconnectNeeded = true; stallResetRequested = true;
                if (session != null) session.BankGpObserved = null;
                Log("Automatic C# cycle enabled; reconciling existing offers."); break;
            case "pause": if (!cleanupRequested && !shutdown.Pending && !parkRequested) Stop(true); else queuedStart = false; break;
            case "manual": manualHold = true; Stop(false); selected = null; reconnectNeeded = true; Log("Elle yönetim açık: otomatik girdiler duraklatıldı; teklifler korunuyor ve gözlem sürüyor."); break;
            case "baseline":
            case "baseline_untracked":
                if (mode != "LIVE") throw new InvalidOperationException("Önce canlı hesaba bağlanın.");
                var observed = backend.Connection();
                if (!observed.B("logged_in")) throw new ConnectionUnavailable("Alış geçmişini doğrulamak için hesabın bağlı olması gerekir.");
                ledger.Ingest(observed);
                if (command == "baseline") ledger.ConfirmNoPriorBuys(observed.S("account_key"), J.Now);
                else ledger.ConfirmNoUntrackedBuys(observed.S("account_key"), J.Now);
                Log(command == "baseline" ? "User-confirmed zero purchases in previous 4 hours recorded." : "User-confirmed no untracked purchases beyond recorded bot activity."); break;
            case "confirm_external_collection":
                var reconciliationState = backend.Observe();
                if (ExternalCollection.Apply(manager, payload, reconciliationState, config))
                {
                    ledger.Event(reconciliationState.S("account_key"), "OPERATOR_EXTERNAL_COLLECTION", payload, reconciliationState.L("observed_at"));
                    reconnectNeeded = true; recoveryHold = null;
                    Log("Elle toplanan satış kullanıcı doğrulaması ve tam bakiye eşleşmesiyle uzlaştırıldı: " + payload.S("item_name"));
                    PublishProfit(reconciliationState);
                }
                break;
            case "margin": selected = payload.I("item_id"); break;
        }
        if (queuedStart && !cleanupRequested && !parkRequested && !shutdown.Pending && J.Now >= nextQueuedStartAt)
        { nextQueuedStartAt = J.Now + 3; Post("start"); }
        if (auto && mode == "LIVE" && J.Now - lastScan >= 60) RequestScan();
        if (backend is SimExchange simulation) simulation.Now = Math.Max(simulation.Now, J.Now);
        if (manualHold)
        {
            var manualState = mode == "LIVE" ? backend.Connection() : backend.Observe();
            if (manualState.B("logged_in") && manualState.S("account_key") != "")
            {
                if (backend is not LiveExchange ml || ml.TrySettleInterruptedInput(manualState)) ReconcileObservedChanges(manualState);
                PublishProfit(manualState);
                Publish("MANUAL_CONTROL_PAUSED", manualState, EngineRules.ExposeCostBasis(ledger.Ingest(manualState), manager, runner));
            }
            else Publish("MANUAL_CONTROL_PAUSED", manualState);
            return;
        }
        if (mode == "LIVE")
        {
            var handoffState = backend.Connection();
            if (HoldForPhysicalUser(handoffState)) return;
            if (backend is LiveExchange interrupted && interrupted.PendingId != null && interrupted.TrySettleInterruptedInput(handoffState))
            { reconnectNeeded = true; stallResetRequested = true; }
        }
        if (session != null)
        {
            var connection = backend.Connection();
            ReconcileObservedChanges(connection);
            if (auto && EngineRules.UnexpectedManualBank(connection, session.Task, cycleStarted))
            {
                NotifyPhysicalUserActivity(); reconnectNeeded = true;
                Log("Banka otomatik aktarım dışında açıldı; GE ekranına dönülene kadar otomatik girdiler bekliyor.");
                HoldForPhysicalUser(connection); return;
            }
            if (exitAccount != null && exitAccount != connection.S("account_key")) { exitReport = null; exitAccount = null; lastExitScan = 0; }
            RequestExitScan(connection); PublishProfit(connection);
            var reviewed = session.ResolveCompletedReview(connection); if (reviewed != null) ApplyTransfer(reviewed);
            if (WatchInputs(connection)) return;
            if (auto && !session.Pending && connection.B("logged_in"))
            {
                EngineRules.PrepareBankStorageProbe(manager, connection);
                var t = runner.Test;
                if (t?.S("stage") == "PAUSED_HELD" && t.S("storage_location") == "BANK")
                    session.StartTransfer(connection, "WITHDRAW", t.I("item_id"), 1, "RESUME_MARGIN_STOCK", "margin:" + t.L("started_at"));
                var stored = manager.Positions.FirstOrDefault(p => p.S("storage_location") == "BANK" && ActiveOwned(p));
                if (stored != null)
                {
                    if (stored.S("account_key") != connection.S("account_key")) throw new InvalidOperationException("Stored position belongs to another account");
                    session.StartTransfer(connection, "WITHDRAW", stored.I("item_id"), stored.L("owned_quantity"), "RESUME_OWNED_STOCK", stored.S("trade_id"));
                }
            }
            if (session.Pending)
            {
                if (connection.B("input_busy")) { Publish("WAITING_FOR_INPUT_COMPLETION", connection); return; }
                backend.Resume(); var completed = session.StepTransfer(connection); if (completed != null) ApplyTransfer(completed);
                Publish(session.Reason ?? "BANK_TRANSFER", connection); return;
            }
            if (parkRequested && !cleanupRequested && shutdown.Plan?.S("stage") == "DONE")
            {
                var held = manager.Positions.FirstOrDefault(p => p.S("stage") == "PAUSED_HELD" && p.S("storage_location", "INVENTORY") == "INVENTORY" && ActiveOwned(p));
                if (held != null && config.B("bank_park_on_stop", true))
                { backend.Resume(); session.StartTransfer(connection, "DEPOSIT", held.I("item_id"), held.L("owned_quantity"), "STORE_OWNED_STOCK", held.S("trade_id")); return; }
                if (runner.Test?.S("stage") == "PAUSED_HELD" && runner.Test.S("storage_location", "INVENTORY") == "INVENTORY" && config.B("bank_park_on_stop", true))
                { backend.Resume(); session.StartTransfer(connection, "DEPOSIT", runner.Test.I("item_id"), 1, "STORE_MARGIN_STOCK", "margin:" + runner.Test.L("started_at")); return; }
                var surplus = connection.L("cash_inventory_gp") - config.L("budget_gp");
                if (config.B("bank_deposit_surplus", true) && surplus >= config.L("bank_surplus_min_gp", 25000))
                { backend.Resume(); session.StartTransfer(connection, "DEPOSIT", 995, surplus, "STORE_SURPLUS_GP"); return; }
                parkRequested = false; backend.RequestStop();
            }
            if (auto || cleanupRequested || shutdown.Pending)
            {
                if (!auto) backend.Resume();
                if (!session.EnsureGe(connection)) { Publish(session.Reason, connection); return; }
            }
            else if (!connection.B("ge_open")) { Publish("CYCLE_STOPPED", connection); return; }
        }
        var state = backend.Observe();
        ReconcileObservedChanges(state);
        RequestExitScan(state);
        var portfolio = backend is HistoryExchange history ? history.Portfolio : ledger.Ingest(state);
        if (mode == "LIVE" && auto && !portfolio.BuyLimitsVerified && config.B("trusted_no_manual_ge_buys", false)
            && state.B("logged_in") && state.S("account_key") != "")
        {
            ledger.ConfirmNoUntrackedBuys(state.S("account_key"), state.L("observed_at"));
            portfolio = ledger.Ingest(state);
            Log("Kalıcı kullanıcı tercihi uygulandı: bot dışı GE alımı olmadığı kabul edilerek alış limitleri yeniden doğrulandı.");
        }
        EngineRules.ExposeCostBasis(portfolio, manager, runner);
        if (WatchInputs(state)) return;
        if (mode == "SIMULATION" && !portfolio.BuyLimitsVerified)
        { ledger.ConfirmNoPriorBuys(state.S("account_key"), state.L("observed_at")); portfolio = ledger.Ingest(state); }
        var executionReady = mode != "LIVE" || state.B("input_enabled") && (state.B("canvas_focused") || config.B("background_input", true) && state.B("background_input_supported"));
        if ((auto || cleanupRequested || shutdown.Pending) && state.B("input_busy")) { Publish("WAITING_FOR_INPUT_COMPLETION", state, portfolio); return; }
        if (cleanupRequested || shutdown.Pending)
        {
            backend.Resume(); backend.Overview(); state = backend.Observe();
            if (cleanupRequested && !shutdown.Pending) { shutdown.Start(EngineRules.TrackedOfferState(manager, runner, state)); cleanupRequested = false; }
            var collected = shutdown.Step(state);
            if (collected != null)
            {
                var fresh = backend.Observe(); manager.ApplyShutdownCollection(collected, fresh); runner.ApplyShutdownCollection(collected, fresh);
                shutdown.Acknowledge(collected); PublishProfit(fresh);
            }
            Storage.WriteJson(PathFor("stop-status.json"), J.O(("observed_at", state.L("observed_at")), ("pending", shutdown.Pending), ("plan", shutdown.Plan)));
            if (!shutdown.Pending)
            {
                var stoppedState = backend.Observe();
                if (runner.ResolveSubmissionAfterCompletedStop(stoppedState))
                    Log("Durdurma sonunda gönderilmemiş margin emri uzlaştırıldı; sonraki BAŞLAT yeni çevrim açabilir.");
                monitor.Reset(); stallResetRequested = false;
                if (session == null) parkRequested = false; backend.RequestStop(); Log("Stop cleanup complete; owned unsold stock retained.");
            }
            Publish(shutdown.Pending ? "CANCELLING_AND_COLLECTING" : "PARKING_ON_STOP", state, portfolio);
            if (shutdown.Pending) return;
        }
        if (auto && recoveryHold == null) EngineRules.AdoptMarginStock(runner, manager, state);
        var daily = ledger.DailyRisk(state.S("account_key"), state.L("observed_at")); var riskHold = EngineRules.RiskReason(daily, config);
        if (auto && riskHold != lastRiskHold) Log(riskHold == null ? "Daily entry hold cleared." : "New entries waiting; existing offers continue: " + riskHold);
        lastRiskHold = riskHold;
        if (auto && recoveryHold == null && session != null && portfolio.BuyLimitsVerified && (runner.Test == null || runner.Test.S("stage") == "DONE")
            && !manager.Positions.Any(p => EngineRules.CollectionStages.Contains(p.S("stage")) || p.S("stage").EndsWith("SUBMITTING")))
        {
            var gap = config.L("budget_gp") - portfolio.FreeCashGp - portfolio.DeployedCostGp;
            if (riskHold == null && config.B("bank_enabled", true) && gap >= config.L("bank_topup_min_gp", 1) && session.BankGpObserved != 0)
            { session.StartTransfer(state, "WITHDRAW", 995, gap, "TOP_UP_WORKING_BUDGET"); return; }
        }
        var freshReport = report != null && (report.S("mode") == "SIMULATION" || J.Now - report.L("generated_at_epoch") is >= 0 and <= 90);
        var current = EngineRules.WithOwnedExitQuotes(freshReport ? report : null, exitReport, state.L("observed_at"));
        current = Adaptation.AdaptReport(current, ledger.Outcomes(state.S("account_key"), state.L("observed_at")), config);
        if (mode == "LIVE") Storage.WriteJson(PathFor("adaptive-report.json"), current);
        var entryReport = EntryReport(current, state.L("observed_at"));
        entryReport["candidates"] = J.A(Strategy.RankFundedCandidates(entryReport.Objects("candidates"), config, portfolio).Cast<JsonNode?>());
        var (rows, diagnostics) = DecisionEngine.Decisions(entryReport, config, portfolio, J.Now);
        if (mode == "LIVE") { Storage.WriteJson(PathFor("decisions.json"), rows); Storage.WriteJson(PathFor("decision-diagnostics.json"), diagnostics); }
        if (selected != null)
        {
            var id = selected.Value; selected = null; var c = entryReport.Objects("candidates").FirstOrDefault(c => c.I("id") == id);
            if (c == null || !c.B("strategy_eligible", true)) throw new InvalidOperationException("Seçili eşyanın güncel ve uygun fiyatı yok.");
            if (riskHold == null && recoveryHold == null && executionReady && runner.Test?.S("stage") != "STOPPED") runner.Start(c, portfolio, state);
            else Log("Seçili margin testi mevcut işlem uzlaştırılana kadar başlatılmadı.");
        }
        string? entryWait = null;
        if (auto && executionReady)
        {
            (state, portfolio) = AdvanceWork(state, current, riskHold == null && recoveryHold == null);
            // An active margin test owns only its own slot. It must not prevent
            // already validated candidates from filling independent empty slots.
            if (recoveryHold == null && !lastWorkAdvanced) entryWait = SelectEntry(current, state, portfolio, daily);
            if (backend is SimExchange sim)
                foreach (var pair in sim.Offers.ToList()) if (pair.Value.S("state") is "BUYING" or "SELLING") sim.Fill(pair.Key);
        }
        PublishProfit(state);
        var performance = ledger.Performance(state.S("account_key"), state.L("observed_at"));
        if (mode == "LIVE") Storage.WriteJson(PathFor("performance-report.json"), performance);
        var reason = cleanupRequested || parkRequested || shutdown.Pending || session?.Pending == true ? queuedStart ? "START_QUEUED_AFTER_STOP" : "CANCELLING_AND_COLLECTING"
            : !auto ? lastError ?? "CYCLE_STOPPED" : recoveryHold != null ? "MANUAL_REVIEW_OTHER_OFFERS_CONTINUE" : riskHold
            ?? (!state.B("input_enabled", mode != "LIVE") ? "LIVE_INPUT_DISABLED" : !executionReady ? "WAITING_FOR_GAME_FOCUS"
            : !portfolio.BuyLimitsVerified ? "BUY_LIMIT_HISTORY_UNVERIFIED" : runner.Test != null && runner.Test.S("stage") != "DONE" ? "MARGIN_TEST_" + runner.Test.S("stage")
            : manager.Positions.Any(p => EngineRules.CollectionStages.Contains(p.S("stage"))) ? "COLLECTING_POSITION" : report == null ? "WAITING_FOR_MARKET_SCAN"
            : entryWait ?? (!entryReport.Objects("candidates").Any(c => c.B("strategy_eligible", true)) ? "NO_ELIGIBLE_CANDIDATES" : portfolio.Offers.Count > 0 ? "MANAGING_OFFERS" : "SELECTING_NEXT_TRADE"));
        Publish(reason, state, portfolio, J.O(("daily_risk", daily), ("performance", performance), ("total_test_loss", ledger.TotalTestLoss(state.S("account_key")))));
    }
    private string? SelectEntry(JsonObject current, JsonObject state, Portfolio portfolio, JsonObject daily)
    {
        var t = runner.Test;
        if (t != null && t.S("stage") == "DONE" && t.S("account_key") == state.S("account_key") && t["result"] is JsonObject r)
        { verified[t.I("item_id")] = r.Copy(); ledger.RecordMarginResult(state.S("account_key"), r, state.L("observed_at")); }
        state = backend.Observe(); portfolio = EngineRules.ExposeCostBasis(ledger.Ingest(state), manager, runner);
        daily = ledger.DailyRisk(state.S("account_key"), state.L("observed_at"));
        if (EngineRules.RiskReason(daily, config) != null) return null;
        var entry = EntryReport(current, state.L("observed_at"));
        entry["candidates"] = J.A(Strategy.RankFundedCandidates(entry.Objects("candidates"), config, portfolio).Cast<JsonNode?>());
        var execution = entry.Copy(); var validated = new List<JsonNode?>();
        foreach (var c in entry.Objects("candidates"))
        {
            var v = MarketAnalyzer.ExecutionCandidate(c, verified.GetValueOrDefault(c.I("id")), config, state.L("observed_at"));
            if (v != null) validated.Add(v);
        }
        execution["candidates"] = J.A(validated);
        var (rows, diagnostics) = DecisionEngine.Decisions(execution, config, portfolio, J.Now);
        if (mode == "LIVE")
        {
            Storage.WriteJson(PathFor("execution-decisions.json"), rows);
            Storage.WriteJson(PathFor("execution-diagnostics.json"), diagnostics);
        }
        var active = manager.Positions.Where(EngineRules.Active).ToList(); var ids = active.Select(p => p.I("item_id")).ToHashSet();
        var capacity = LimitRules.SlotCapacity(config, portfolio);
        if (manager.Positions.Any(p => EngineRules.CollectionStages.Contains(p.S("stage")))) return "NEW_ENTRY_PENDING_COLLECTION";
        if (active.Count >= capacity) return "NEW_ENTRY_ACTIVE_POSITION_CAP";
        if (portfolio.Offers.Count >= capacity) return "NEW_ENTRY_GE_SLOTS_FULL";
        var chosen = rows.OfType<JsonObject>().FirstOrDefault(r => r.S("action") == "BUY" && !ids.Contains(r.I("item_id")));
        if (chosen != null)
        {
            var c = execution.Objects("candidates").Single(c => c.I("id") == chosen.I("item_id"));
            c["funded_allocation_position_count"] = chosen["funded_allocation_position_count"]?.DeepClone();
            var qty = diagnostics.Objects("details").Single(d => d.I("item_id") == c.I("id") && d.ContainsKey("planned_quantity")).L("planned_quantity");
            manager.Add(c, qty, portfolio, state, true); entryFailures.Remove(c.I("id")); return null;
        }
        if (!portfolio.BuyLimitsVerified) return "BUY_LIMIT_HISTORY_UNVERIFIED";
        // The runner is intentionally single-flight. We can still open validated
        // portfolio positions above, but must not replace an unfinished test here.
        if (runner.Test is JsonObject activeMargin && activeMargin.S("stage") is not ("DONE" or "STOPPED"))
            return "MARGIN_TEST_IN_PROGRESS";
        var slots = active.Where(p => p.S("stage") is "WAIT_BUY" or "WAIT_SELL").Select(p => p.I("slot")).ToHashSet();
        if (portfolio.Offers.Any(o => !slots.Contains(o.Slot))) return "NEW_ENTRY_UNTRACKED_GE_OFFERS";
        if (state.Objects("offers").Any(o => o.S("state") is not ("EMPTY" or "BUYING" or "SELLING"))) return "NEW_ENTRY_PENDING_COLLECTION";
        var counts = new Dictionary<string, int>();
        foreach (var c in entry.Objects("candidates"))
        {
            string? reason = null; var category = c.S("category", Strategy.ItemCategory(c.S("name")));
            if (!c.B("strategy_eligible", true) || c.S("risk") == "Elevated") continue;
            if (ids.Contains(c.I("id"))) reason = "ITEM_ALREADY_ACTIVE";
            else if (active.Count(p => Strategy.ItemCategory(p.S("item_name")) == category) >= config.L("max_category_positions", 2)) reason = "CATEGORY_CAP";
            else if (portfolio.Members != true && c.B("members")) reason = "MEMBERS_ITEM_UNAVAILABLE";
            else if (active.Any(p => LimitRules.LimitGroup(p.I("item_id"), p.S("item_name")) == c.S("limit_group", LimitRules.LimitGroup(c.I("id"), c.S("name"))))) reason = "LIMIT_FAMILY_ACTIVE";
            else if (verified.TryGetValue(c.I("id"), out var old) && state.L("observed_at") - old.L("completed_at") is >= 0 && state.L("observed_at") - old.L("completed_at") <= config.L("margin_retest_cooldown_seconds", 600)) reason = "MARGIN_COOLDOWN";
            else if (config.B("monetary_limits_enabled", true) && daily.L("margin_test_cost_gp") + c.L("estimated_test_loss_gp") > config.L("max_daily_test_cost_gp", 10000)) reason = "DAILY_TEST_COST_LIMIT";
            else if (c.L("buy_limit") - LimitRules.UsedLimit(c, portfolio) <= 1) reason = "BUY_LIMIT_EXHAUSTED";
            else if (Strategy.TestPositionPlan(c, config, portfolio).Profit <= 0) reason = "TEST_COST_NOT_RECOVERED";
            if (reason != null) { counts[reason] = counts.GetValueOrDefault(reason) + 1; continue; }
            if (config.B("monetary_limits_enabled", true) && ledger.TotalTestLoss(state.S("account_key")) + c.L("estimated_test_loss_gp") > 1000000)
                return "NEW_ENTRY_TOTAL_TEST_COST_LIMIT";
            runner.Start(c, portfolio, state, slots); return null;
        }
        return counts.Count == 0 ? "NO_ELIGIBLE_CANDIDATES" : "NEW_ENTRY_" + counts.MaxBy(pair => pair.Value).Key;
    }
}
