using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

/// <summary>Uses fake bridge files in unique temporary folders; never connects to RuneLite or sends native inputs.</summary>
public static class LiveUserHandoffRegressionChecks
{
    public static JsonObject Run()
    {
        var checks = new JsonArray();
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidDataException("Live user handoff regression: " + name);
            checks.Add(name);
        }
        foreach (var terminalStatus in new[] { "dispatched", "rejected" })
        {
            using var fixture = new FakeBridge();
            var exchange = fixture.Exchange;
            var initial = fixture.State;
            var command = J.O(("type", "click"), ("widget_key", "fake-ge-widget"));
            fixture.Blocked = true;
            Exception? blockedError = null;
            try { exchange.Send(initial, command); } catch (Exception e) { blockedError = e; }
            Check(blockedError is UserIntervention && exchange.PendingId is null && !File.Exists(fixture.CommandPath),
                terminalStatus + ": physical-input guard blocks before any command file or pending request");

            fixture.Blocked = false;
            var send = Task.Run(() =>
            {
                try { exchange.Send(initial, command); return (Exception?)null; }
                catch (Exception e) { return e; }
            });
            fixture.OutstandingSend = send;
            Check(SpinWait.SpinUntil(() => File.Exists(fixture.CommandPath), 2000), terminalStatus + ": fake bridge receives one isolated IPC request");
            var request = Storage.ReadJson(fixture.CommandPath).AsObject();
            var originalCommand = File.ReadAllText(fixture.CommandPath);
            Check(exchange.PendingId == request.S("id") && exchange.ExecutionPhase.B("active"), terminalStatus + ": in-flight transport retains request identity");
            fixture.Blocked = true;
            exchange.InterruptForUser();
            Check(send.Wait(2000) && send.GetAwaiter().GetResult() is UserIntervention,
                terminalStatus + ": interruption wakes an in-flight Send as user handoff");
            Check(exchange.PendingId == request.S("id") && exchange.ExecutionPhase.B("active"),
                terminalStatus + ": interrupted transport remains pending until terminal evidence");

            var changedSession = initial.Copy(); changedSession["session"] = "replacement-session"; changedSession["sequence"] = 15;
            Storage.WriteJson(fixture.StatePath, changedSession);
            exchange.CancelPending();
            var cancel = Storage.ReadJson(fixture.CancelPath).AsObject();
            Check(cancel.S("id") == request.S("id") && cancel.S("session") == initial.S("session"),
                terminalStatus + ": cancel addresses original request session after live snapshot replacement");

            var settled = initial.Copy(); settled["sequence"] = 11; settled["observed_at"] = J.Now;
            Check(!exchange.TrySettleInterruptedInput(settled) && exchange.PendingId == request.S("id"),
                terminalStatus + ": missing terminal receipt keeps original pending request");
            File.WriteAllText(fixture.ReceiptPath, "{");
            Check(!exchange.TrySettleInterruptedInput(settled) && exchange.PendingId == request.S("id"),
                terminalStatus + ": malformed receipt keeps original pending request");
            foreach (var status in new[] { "pending", "unknown", "" })
            {
                Storage.WriteJson(fixture.ReceiptPath, J.O(("id", request.S("id")), ("status", status)));
                Check(!exchange.TrySettleInterruptedInput(settled) && exchange.PendingId == request.S("id"),
                    terminalStatus + ": nonterminal receipt remains pending: " + status);
            }
            Storage.WriteJson(fixture.ReceiptPath, J.O(("id", "another-command"), ("status", terminalStatus)));
            Check(!exchange.TrySettleInterruptedInput(settled) && exchange.PendingId == request.S("id"),
                terminalStatus + ": receipt from another command cannot release interrupted request");
            var receipt = J.O(("id", request.S("id")), ("status", terminalStatus), ("reason", "InterruptedException"));
            Storage.WriteJson(fixture.ReceiptPath, receipt);
            foreach (var field in new[] { "session", "account_key" })
            {
                var wrong = settled.Copy(); wrong[field] = "different";
                Check(!exchange.TrySettleInterruptedInput(wrong) && exchange.PendingId == request.S("id"),
                    terminalStatus + ": terminal receipt cannot release transport against changed " + field);
            }
            var unavailable = settled.Copy(); unavailable["input_busy"] = true;
            Check(!exchange.TrySettleInterruptedInput(unavailable), terminalStatus + ": bridge still busy keeps pending request");
            unavailable = settled.Copy(); unavailable["logged_in"] = false;
            Check(!exchange.TrySettleInterruptedInput(unavailable), terminalStatus + ": logged-out state cannot release request");
            unavailable = settled.Copy(); unavailable["sequence"] = initial.L("sequence");
            Check(!exchange.TrySettleInterruptedInput(unavailable), terminalStatus + ": request-era snapshot cannot release request");
            unavailable = settled.Copy(); unavailable["observed_at"] = J.Now - 7;
            Check(!exchange.TrySettleInterruptedInput(unavailable), terminalStatus + ": stale state cannot release request");
            Check(!File.Exists(fixture.InterruptionPath) && File.ReadAllText(fixture.CommandPath) == originalCommand,
                terminalStatus + ": uncertain outcomes neither synthesize settlement evidence nor replay input");

            settled["observed_at"] = J.Now;
            Storage.WriteJson(fixture.StatePath, settled);
            var fresh = exchange.Connection();
            Check(exchange.TrySettleInterruptedInput(fresh) && exchange.PendingId is null && !exchange.ExecutionPhase.B("active"),
                terminalStatus + ": matching terminal receipt and fresh original identity release transport");
            var evidence = Storage.ReadJson(fixture.InterruptionPath).AsObject();
            Check(evidence.S("command_id") == request.S("id") && evidence.S("status") == terminalStatus && !evidence.B("outcome_verified"),
                terminalStatus + ": transport evidence explicitly does not claim offer or collection success");
            Check(exchange.TrySettleInterruptedInput(fresh) && File.ReadAllText(fixture.CommandPath) == originalCommand,
                terminalStatus + ": repeated settlement is idempotent and never resends original command");
            Check(!File.Exists(Path.Combine(fixture.Folder, "live-collection-receipts.json")),
                terminalStatus + ": interrupted input settlement does not invent collection receipt");
        }
        return J.O(("passed", checks.Count), ("checks", checks), ("live_game_inputs", false));
    }

    sealed class FakeBridge : IDisposable
    {
        int blocked;
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "OsrsFakeUserHandoff-" + Guid.NewGuid().ToString("N"));
        public string StatePath => Path.Combine(Folder, "live-state.json");
        public string CommandPath => Path.Combine(Folder, "input-command.json");
        public string ReceiptPath => Path.Combine(Folder, "input-receipt.json");
        public string CancelPath => Path.Combine(Folder, "input-cancel.json");
        public string InterruptionPath => Path.Combine(Folder, "last-interrupted-input.json");
        public LiveExchange Exchange { get; }
        public Task<Exception?>? OutstandingSend { get; set; }
        public bool Blocked { get => Volatile.Read(ref blocked) != 0; set => Volatile.Write(ref blocked, value ? 1 : 0); }
        public JsonObject State { get; } = J.O(("schema", 1), ("observed_at", J.Now), ("sequence", 10),
            ("account_key", "offline-handoff-account"), ("session", "offline-handoff-session"),
            ("logged_in", true), ("ge_open", true), ("input_enabled", true), ("input_busy", false),
            ("background_input_supported", true), ("cash_inventory_gp", 100), ("inventory", new JsonArray()),
            ("offers", new JsonArray()), ("widgets", new JsonArray()));

        public FakeBridge()
        {
            Directory.CreateDirectory(Folder);
            Storage.WriteJson(Path.Combine(Folder, "config.json"), new JsonObject());
            Storage.WriteJson(StatePath, State);
            Exchange = new LiveExchange(Folder, clickPauseSeconds: 0);
            Exchange.UserInputBlocked = () => Blocked;
        }
        public void Dispose()
        {
            Blocked = true; Exchange.InterruptForUser();
            if (OutstandingSend is not null && !OutstandingSend.Wait(3000))
                throw new TimeoutException("Fake handoff IPC did not stop; isolated fixture retained for inspection.");
            Exchange.Dispose();
            foreach (var path in Directory.EnumerateFiles(Folder)) File.Delete(path);
            Directory.Delete(Folder);
        }
    }
}
