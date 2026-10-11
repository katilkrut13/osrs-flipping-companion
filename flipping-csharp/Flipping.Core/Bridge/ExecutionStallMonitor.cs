using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public sealed class ExecutionStallMonitor
{
    static readonly HashSet<string> ExecutionStages = ["BUY_SUBMITTING", "SELL_SUBMITTING", "BUY_COLLECTING", "SELL_COLLECTING", "PARTIAL_SELL_COLLECTING"];
    static readonly HashSet<string> SessionPhases = ["OPENING_GE", "CLOSING_BANK", "CHOOSING_GE_TELEPORT", "TELEPORTING_TO_GE", "WAITING_FOR_NAVIGATION", "OPENING_BANK", "WAITING_FOR_BANK", "ENTERING_GAME", "SAVED_SESSION_LOGIN_PENDING", "ENTERING_SAVED_BANK_PIN", "VERIFYING_BANK_PIN"];
    readonly bool enabled;
    readonly double waitSeconds, reviewSeconds;
    readonly int minSnapshots;
    readonly Func<double> clock;
    string? fingerprint, reviewWorkflow;
    double? progressAt;
    int snapshots;
    bool reviewRequired;
    public JsonObject Status { get; private set; } = new();
    public ExecutionStallMonitor(JsonObject? config = null, Func<double>? clock = null)
    {
        config ??= new JsonObject();
        if (config.ContainsKey("input_stall_enabled") && !(config["input_stall_enabled"] is JsonValue flag && flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("input_stall_enabled must be a boolean");
        enabled = config.B("input_stall_enabled");
        waitSeconds = Number(config, "input_stall_wait_seconds", 15); reviewSeconds = Number(config, "input_stall_review_seconds", 45);
        if (waitSeconds is < 2 or > 120) throw new ArgumentException("input_stall_wait_seconds must be a finite number from 2 to 120");
        if (reviewSeconds <= waitSeconds || reviewSeconds > 300) throw new ArgumentException("input_stall_review_seconds must exceed the wait threshold and be at most 300");
        if (config.ContainsKey("input_stall_min_snapshots") && !config["input_stall_min_snapshots"].IsInteger()) throw new ArgumentException("input_stall_min_snapshots must be an integer from 3 to 30");
        minSnapshots = config.I("input_stall_min_snapshots", 3); if (minSnapshots is < 3 or > 30) throw new ArgumentException("input_stall_min_snapshots must be an integer from 3 to 30");
        this.clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency); Reset();
    }
    static double Number(JsonObject config, string key, double fallback)
    {
        if (!config.ContainsKey(key)) return fallback;
        if (!InputPacing.IsNumber(config[key], out var n)) throw new ArgumentException(key + " must be a finite number"); return n;
    }
    public void Reset()
    {
        fingerprint = null; progressAt = null; snapshots = 0; reviewRequired = false; reviewWorkflow = null;
        Status = J.O(("enabled", enabled), ("armed", false), ("hold_reason", null), ("reason", null), ("blocked_inputs", false), ("unchanged_seconds", 0), ("unchanged_snapshots", 0));
    }
    static JsonArray Fields(JsonObject? value, params string[] keys) => J.A(keys.Select(k => value?[k]));
    public JsonObject Observe(JsonObject state, IEnumerable<JsonObject>? positions = null, JsonObject? margin = null, JsonObject? bankTask = null, string? pendingId = null, JsonObject? executionPhase = null, string? sessionReason = null, bool active = true)
    {
        var phase = executionPhase ?? new JsonObject(); var entries = positions?.ToArray() ?? []; var stages = entries.Select(p => p.S("stage")).ToArray(); var marginStage = margin.S("stage"); var bankStage = bankTask.S("stage");
        // Position and margin journal stages are state-machine work, not proof that an
        // OS input is still running. Treating BUY_COLLECTING/SELL_COLLECTING as a
        // global input stall prevented Step() from performing the very next action
        // (for example, submitting the sell after a collected margin buy). Only arm
        // the global guard while a real bridge command or session UI operation is in
        // flight; journal reconciliation remains isolated to its own trade.
        var workflow = !string.IsNullOrEmpty(pendingId) || phase.B("active") || state.B("input_busy") ? "COMMAND_RECEIPT"
            : bankStage.Length > 0 && bankStage is not ("DONE" or "REVIEW") ? "BANK_" + bankStage
            : sessionReason is not null && SessionPhases.Contains(sessionReason) ? "SESSION_UI" : null;
        if (!enabled) { Reset(); return Status.Copy(); }
        double elapsed = 0; bool waiting = false;
        if (active && workflow is not null)
        {
            var progress = J.O(("account", state["account_key"]),
                ("positions", J.A(entries.OrderBy(p => p.S("trade_id", p.L("item_id").ToString())).Select(p => (JsonNode?)Fields(p, "trade_id", "item_id", "stage", "owned_quantity", "acquired_quantity", "buy_spent", "sale_proceeds_gp")))),
                ("margin", Fields(margin, "item_id", "stage", "buy_spent")), ("bank", Fields(bankTask, "item_id", "stage", "quantity")),
                ("offers", J.A(state.Objects("offers").OrderBy(o => o.L("slot")).Select(o => (JsonNode?)Fields(o, "slot", "item_id", "state", "quantity", "filled", "spent")))),
                ("cash", state["cash_inventory_gp"]), ("inventory", J.A(state.Objects("inventory").OrderBy(i => i.L("id")).Select(i => (JsonNode?)Fields(i, "id", "quantity")))),
                ("bank_inventory", J.A(state.Objects("bank_inventory").OrderBy(i => i.L("id")).Select(i => (JsonNode?)Fields(i, "id", "quantity")))),
                ("ui", Fields(state, "input_busy", "ge_open", "bank_open", "bank_pin_open", "input_type", "game_state", "login_index")), ("pending_id", pendingId),
                ("phase", phase.B("active") ? Fields(phase, "phase", "command_id", "last_progress_at") : null));
            if (workflow == "COMMAND_RECEIPT") progress = J.O(("account", state["account_key"]), ("pending_id", pendingId), ("phase", progress["phase"]), ("session", state["session"]), ("input_busy", state["input_busy"]));
            else if (bankStage.Length > 0 && bankStage is not ("DONE" or "REVIEW"))
            {
                var itemId = bankTask.I("item_id"); progress = J.O(("account", state["account_key"]), ("session", state["session"]), ("bank", progress["bank"]), ("ui", progress["ui"]),
                    ("inventory_quantity", ProtocolHelpers.InventoryQuantity(state, itemId)), ("bank_quantity", SessionController.BankCount(state, itemId)));
            }
            var next = progress.ToJsonString(); var now = clock();
            if (next != fingerprint) { fingerprint = next; progressAt = now; snapshots = 1; } else snapshots++;
            elapsed = Math.Max(0, now - progressAt!.Value); var repeated = snapshots >= minSnapshots;
            if (repeated && elapsed >= reviewSeconds) { if (!reviewRequired) reviewWorkflow = workflow; reviewRequired = true; }
            waiting = repeated && elapsed >= waitSeconds;
        }
        else { fingerprint = null; progressAt = null; snapshots = 0; }
        var hold = reviewRequired ? "INPUT_STALL_NEEDS_REVIEW" : waiting ? "WAITING_FOR_PROGRESS" : null;
        Status = J.O(("enabled", true), ("armed", active && workflow is not null), ("hold_reason", hold), ("reason", hold is not null ? $"{reviewWorkflow ?? workflow ?? "EXECUTION"}: no confirmed progress" : null),
            ("blocked_inputs", hold is not null), ("workflow", workflow), ("unchanged_seconds", Math.Round(elapsed, 2)), ("unchanged_snapshots", snapshots));
        return Status.Copy();
    }
}
