using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OsrsFlipping.Core;

public static class LimitRules
{
    internal static readonly string[] PotionWords = ["potion", "brew", "restore", "prayer", "attack", "strength", "defence", "ranging", "magic", "stamina", "energy", "antipoison", "antidote"];
    public static string LimitGroup(int itemId, string name)
    {
        var original = name.ToLowerInvariant().Trim();
        var basis = Regex.Replace(original, @"\([1-4]\)$", "").Trim();
        return basis != original && PotionWords.Any(basis.Contains) ? "dose:" + basis : "item:" + itemId;
    }
    public static long UsedLimit(JsonObject candidate, Portfolio portfolio)
    {
        var id = (int)candidate.L("id");
        var group = candidate.S("limit_group", LimitGroup(id, candidate.S("name")));
        return Math.Max(portfolio.BoughtLast4h.GetValueOrDefault(id), portfolio.BoughtByLimitGroup.GetValueOrDefault(group));
    }
    public static int SlotCapacity(JsonObject config, Portfolio? portfolio = null, JsonObject? state = null)
    {
        var observed = state?.L("ge_slot_capacity", 3) ?? portfolio?.SlotCapacity ?? 3;
        return (int)Math.Min(Math.Clamp(observed, 0, 8), Math.Max(0, config.L("target_slots", 8)));
    }
    public static List<int> EmptySlots(JsonObject state, JsonObject config)
    {
        var capacity = SlotCapacity(config, state: state);
        return state.Objects("offers").Where(o => o.S("state") == "EMPTY" && o.L("slot") >= 0 && o.L("slot") < capacity).Select(o => (int)o.L("slot")).ToList();
    }
    public static long StagnationMinutes(JsonObject? position, JsonObject config)
    {
        var tier = position?.S("portfolio_tier") ?? "";
        return tier switch
        {
            "LIQUID_CORE" => config.L("liquid_stagnation_minutes", 6),
            "MID_LIQUID" => config.L("mid_stagnation_minutes", 12),
            "OPPORTUNITY" => config.L("opportunity_stagnation_minutes", 25),
            _ => config.L("stagnation_timeout_minutes", 12)
        };
    }
    public static bool IsStagnant(long now, long openedAt, long? lastProgressAt, JsonObject config, JsonObject? position = null) =>
        now - (lastProgressAt ?? openedAt) >= StagnationMinutes(position, config) * 60 || now - openedAt >= config.L("max_order_age_minutes", 45) * 60;
}
