using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OsrsFlipping.Core;

public static class ProtocolHelpers
{
    public static string Plain(string? value) => Regex.Replace(value ?? "", "<[^>]*>", "").Trim();
    public static string PrimaryAction(JsonObject widget) => widget.Arr("actions").Select(x => Plain(x?.ToString())).FirstOrDefault(x => x.Length > 0)?.ToLowerInvariant() ?? "";
    public static long InventoryQuantity(JsonObject state, int itemId) => state.Objects("inventory").Where(i => i.L("unnoted_id", i.L("id")) == itemId).Sum(i => i.L("quantity"));
    public static bool Same(JsonObject left, JsonObject right, params string[] fields) => fields.All(k => JsonNode.DeepEquals(left[k], right[k]));
    public static string SetupSignature(JsonObject state) => string.Join("\n", state.Objects("widgets")
        .Where(w => w.S("symbol").StartsWith("GeOffers.SETUP", StringComparison.Ordinal) && (w.L("index") is 34 or 41 || w.L("item_id", -1) > 0 || w.S("text").Length > 0))
        .Select(w => J.O(("key", w.S("key")), ("item_id", w.L("item_id", -1)), ("text", Plain(w.S("text"))), ("name", Plain(w.S("name")))).ToJsonString()).Order(StringComparer.Ordinal));
    public static string TextPromptSignature(JsonObject state) => string.Join("\n", state.Objects("widgets").Where(w => w.S("symbol").StartsWith("Chatbox.MES_TEXT", StringComparison.Ordinal))
        .Select(w => J.O(("key", w.S("key")), ("text", Plain(w.S("text")))).ToJsonString()).Order(StringComparer.Ordinal));
    public static string ItemSearchQuery(string name, bool shorter = false)
    {
        if (!shorter && Regex.IsMatch(name, "^[A-HJ-Za-hj-z0-9 ]{3,80}$")) return name;
        var query = Regex.Matches(name, "[A-HJ-Za-hj-z0-9]+").Select(m => m.Value).OrderByDescending(x => x.Length).FirstOrDefault() ?? "";
        if (query.Length < 3) throw new ArgumentException("No unambiguous keyboard-compatible search fragment");
        return query;
    }
    public static JsonObject? SearchResultIcon(JsonObject state, int itemId, string itemName)
    {
        var widgets = state.Objects("widgets").ToArray();
        var pairs = new List<JsonObject>();
        foreach (var row in widgets.Where(w => w.S("symbol").StartsWith("Chatbox.", StringComparison.Ordinal) && Plain(w.S("name")).Equals(itemName, StringComparison.OrdinalIgnoreCase)
            && PrimaryAction(w) == "select" && w.L("x", -1) >= 0 && w.L("y", -1) >= 0 && w.L("width") > 0 && w.L("height") > 0))
        {
            var icons = widgets.Where(w => w.L("item_id") == itemId && w.S("symbol").StartsWith("Chatbox.", StringComparison.Ordinal) && w.L("id") == row.L("id")
                && row.L("x") <= w.L("x", -1) && w.L("x", -1) < row.L("x") + row.L("width") && row.L("y") <= w.L("y", -1) && w.L("y", -1) < row.L("y") + row.L("height")).ToArray();
            if (icons.Length == 1) pairs.Add(icons[0]);
        }
        return pairs.Count == 1 ? pairs[0] : null;
    }
    public static long? SetupNumber(JsonObject state, int index)
    {
        var fields = state.Objects("widgets").Where(w => w.S("symbol") == "GeOffers.SETUP" && w.L("index") == index && w.L("x", -1) >= 0 && w.L("y", -1) >= 0).ToArray();
        if (fields.Length != 1) return null;
        var m = Regex.Match(Plain(fields[0].S("text")), "^([\\d,]+)(?:\\s+coins?)?$");
        return m.Success && long.TryParse(m.Groups[1].Value.Replace(",", ""), out var value) ? value : null;
    }
    public static JsonObject[] InventorySellTargets(JsonObject state, int itemId, long quantity)
    {
        var ids = state.Objects("inventory").Where(i => i.L("unnoted_id", i.L("id")) == itemId).GroupBy(i => i.L("id"))
            .Where(g => g.Sum(i => i.L("quantity")) >= quantity).Select(g => g.Key).ToHashSet();
        var targets = state.Objects("widgets").Where(w => ids.Contains(w.L("item_id")) && w.S("symbol").StartsWith("GeOffersSide.", StringComparison.Ordinal)
            && w.L("x", -1) >= 0 && PrimaryAction(w) == "offer").ToArray();
        return targets.Length > 1 && targets.Select(w => w.L("item_id")).Distinct().Count() == 1
            ? [targets.OrderBy(w => w.L("index")).ThenBy(w => w.L("y")).ThenBy(w => w.L("x")).First()] : targets;
    }
    public static string? ClickContext(JsonObject widget)
    {
        if (widget.S("symbol") == "GeOffers.SETUP_CONFIRM") return "confirm";
        var action = PrimaryAction(widget);
        if (action.StartsWith("enter quantity") || action.StartsWith("enter price")) return "number";
        if (action.StartsWith("select") || action.StartsWith("offer")) return "select";
        if (action.Contains("collect")) return "collect";
        if (action.Contains("abort")) return "cancel";
        return null;
    }
    public static double TypingReceiptTimeout(string text, int delayMs, IReadOnlyList<int>? delaysMs = null, bool scoped = false, int enterPauseMs = 0, IReadOnlyList<int>? keyHoldMs = null, int enterHoldMs = 0)
    {
        var duration = text.Select((ch, i) => (delaysMs is null ? delayMs : delaysMs[i]) * (scoped ? 2 : char.IsUpper(ch) || ch is '(' or ')' ? 4 : 2)).Sum();
        duration += keyHoldMs?.Sum() ?? 0;
        return Math.Max(12, (duration + enterHoldMs + enterPauseMs) / 1000d + 6);
    }
}

public class InputRejected : InvalidOperationException
{
    public static readonly HashSet<string> NoDispatchReasons = ["Widget not visible", "Stale command", "Expired command", "Previous input still busy", "Text buffer length changed", "Unsupported text edit timing", "Unsupported pointer timing", "Unsupported key hold timing", "Pointer click not dispatched"];
    public bool NoDispatch { get; }
    public InputRejected(string reason) : base(reason) { NoDispatch = NoDispatchReasons.Contains(reason); }
}
public class TransientUIError(string message) : TimeoutException(message);
public class OfferNotSubmitted(int itemId, string side, string phase, JsonObject details) : TimeoutException($"{side} item {itemId}: {phase} failed before confirmation")
{
    public int ItemId { get; } = itemId;
    public string Side { get; } = side;
    public string Phase { get; } = phase;
    public JsonObject Details { get; } = details;
    public bool NoSubmission => true;
}
