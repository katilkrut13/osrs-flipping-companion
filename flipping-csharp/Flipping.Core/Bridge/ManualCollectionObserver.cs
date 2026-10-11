using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

/// <summary>Observes inventory collections without dispatching game input. Ambiguous transitions produce no receipt.</summary>
public sealed class ManualCollectionObserver(JsonObject config)
{
    JsonObject? baseline;
    long lastSequence = -1;
    public void Reset(JsonObject? state = null)
    {
        baseline = state is not null && SafeContext(state) ? state.Copy() : null;
        lastSequence = baseline?.L("sequence") ?? -1;
    }
    public IReadOnlyList<JsonObject> Observe(JsonObject state)
    {
        if (!SafeContext(state)) { Reset(); return []; }
        if (baseline is null) { Reset(state); return []; }
        if (ProtocolHelpers.Same(baseline, state, "account_key", "session") && state.L("sequence") <= lastSequence) return [];
        lastSequence = state.L("sequence");
        var before = baseline;
        if (!Consecutive(before, state)) { baseline = state.Copy(); return []; }
        var receipts = Build(before, state, config);
        if (receipts.Count > 0) { baseline = state.Copy(); return receipts; }
        // The two collection icons may be clicked separately. Keep the original balance
        // only while every offer is unchanged and observed deltas fit its remaining settlement.
        if (!PossiblePartial(before, state, config)) baseline = state.Copy();
        return [];
    }

    static readonly string[] OfferFields = ["slot", "state", "item_id", "quantity", "price", "filled", "spent"];
    static bool SafeContext(JsonObject s) => s.B("logged_in") && s.B("ge_open") && !s.B("bank_open") && !s.B("input_busy")
        && s.S("account_key").Length > 0 && s.S("session").Length > 0 && Integer(s, "sequence", 0) && Integer(s, "observed_at", 0)
        && Integer(s, "cash_inventory_gp", 0) && ValidInventory(s) && Offers(s) is not null;
    static bool Integer(JsonObject s, string key, long minimum) => s[key].IsInteger() && s.L(key) >= minimum;
    static bool Consecutive(JsonObject before, JsonObject after) => SafeContext(before) && SafeContext(after)
        && ProtocolHelpers.Same(before, after, "account_key", "session") && after.L("sequence") > before.L("sequence")
        && after.L("observed_at") >= before.L("observed_at") && after.L("observed_at") - before.L("observed_at") <= 5;
    static bool ValidInventory(JsonObject s) => s["inventory"] is JsonArray && s.Objects("inventory").All(i => Integer(i, "id", 1) && i.L("id") <= int.MaxValue
        && Integer(i, "quantity", 1) && (!i.ContainsKey("unnoted_id") || Integer(i, "unnoted_id", 1) && i.L("unnoted_id") <= int.MaxValue)
        && (i.L("id") == 995) == (i.L("unnoted_id", i.L("id")) == 995))
        && s.Arr("inventory").Count == s.Objects("inventory").Count()
        && s.Objects("inventory").Where(i => i.L("id") == 995).Sum(i => (decimal)i.L("quantity")) == s.L("cash_inventory_gp");
    static Dictionary<int, JsonObject>? Offers(JsonObject state)
    {
        if (state["offers"] is not JsonArray array || array.Count != state.Objects("offers").Count()) return null;
        var rows = state.Objects("offers").ToArray();
        if (rows.Length is not (3 or 8) || rows.Length < state.L("ge_slot_capacity", 8)
            || rows.Any(o => !Integer(o, "slot", 0) || o.L("slot") >= rows.Length || o.S("state").Length == 0)
            || rows.Select(o => o.L("slot")).Distinct().Count() != rows.Length) return null;
        return rows.ToDictionary(o => (int)o.L("slot"));
    }
    static Dictionary<int, long> Inventory(JsonObject state) => state.Objects("inventory").GroupBy(i => (int)i.L("unnoted_id", i.L("id")))
        .ToDictionary(g => g.Key, g => checked(g.Sum(i => i.L("quantity"))));
    static bool Settled(JsonObject o) => o.S("state") is "BOUGHT" or "CANCELLED_BUY" or "SOLD" or "CANCELLED_SELL"
        && Integer(o, "item_id", 1) && o.L("item_id") != 995 && o.L("item_id") <= int.MaxValue && Integer(o, "quantity", 1)
        && o.L("quantity") <= int.MaxValue && Integer(o, "price", 1) && Integer(o, "filled", 0) && o.L("filled") <= o.L("quantity")
        && Integer(o, "spent", 0) && (o.S("state") is not ("BOUGHT" or "SOLD") || o.L("filled") == o.L("quantity"));
    static bool Buying(JsonObject o) => o.S("state") is "BOUGHT" or "CANCELLED_BUY";
    static long ItemAmount(JsonObject o) => Buying(o) ? o.L("filled") : o.L("quantity") - o.L("filled");
    static (long Minimum, long Maximum)? CashRange(JsonObject o, JsonObject config)
    {
        if (!Settled(o)) return null;
        try
        {
            var filled = o.L("filled"); var gross = o.L("spent"); var price = o.L("price");
            if (Buying(o))
            {
                if (gross > checked(filled * price)) return null;
                var refund = checked(o.L("quantity") * price - gross); return (refund, refund);
            }
            if (filled == 0) return gross == 0 ? (0, 0) : null;
            if ((decimal)filled * price > gross) return null;
            if (config.Arr("tax_exempt_ids").Any(n => n.IsInteger() && n.LongValue() == o.L("item_id"))) return (gross, gross);
            var rate = config.L("tax_percent", 2); var cap = config.L("tax_cap_gp", 5_000_000);
            if (rate is < 0 or > 100 || cap < 0) return null;
            var uncappedTax = decimal.Floor((decimal)gross * rate / 100);
            var minTax = (decimal)filled * MarketAnalyzer.Tax(price, (int)o.L("item_id"), config);
            var maxTax = Math.Min(uncappedTax, (decimal)filled * cap);
            // When no individual unit can reach the tax cap, floor-rounding bounds tighten.
            if (uncappedTax <= cap) minTax = Math.Max(minTax, uncappedTax - filled + 1);
            return ((long)(gross - maxTax), (long)(gross - minTax));
        }
        catch (OverflowException) { return null; }
    }
    public static bool CanAttributeOverview(JsonObject state, JsonObject config)
    {
        if (!SafeContext(state)) return false;
        var offers = state.Objects("offers").Where(Settled).ToArray();
        var ranges = offers.Select(o => CashRange(o, config)).ToArray();
        return offers.Length > 0 && ranges.All(r => r.HasValue) && ranges.Count(r => r!.Value.Minimum != r.Value.Maximum) <= 1;
    }
    public static JsonObject? RecoverRequestedOverview(JsonObject before, JsonObject after, int requestedSlot, JsonObject config)
    {
        if (!Consecutive(before, after)) return null;
        var oldOffers = Offers(before); var newOffers = Offers(after);
        if (oldOffers is null || newOffers is null || !oldOffers.TryGetValue(requestedSlot, out var requested)
            || !newOffers.TryGetValue(requestedSlot, out var cleared) || !Settled(requested) || cleared.S("state") != "EMPTY") return null;
        // Collect All may coincide with an already-attributed item movement from
        // another slot. Rebuild an isolated two-snapshot proof for the requested
        // slot while retaining its exact wallet and item deltas.
        var isolated = after.Copy();
        isolated["offers"] = J.A(oldOffers.OrderBy(p => p.Key).Select(p => (JsonNode?)(p.Key == requestedSlot ? cleared.Copy() : p.Value.Copy())));
        var beforeInventory = Inventory(before); var afterInventory = Inventory(after); var itemId = requested.I("item_id");
        var rows = before.Objects("inventory").Where(i => i.L("unnoted_id", i.L("id")) is 995 || i.L("unnoted_id", i.L("id")) == itemId)
            .Select(i => i.Copy()).ToList();
        void SetQuantity(int id, long quantity)
        {
            rows.RemoveAll(i => i.L("unnoted_id", i.L("id")) == id);
            if (quantity <= 0) return;
            var source = after.Objects("inventory").FirstOrDefault(i => i.L("unnoted_id", i.L("id")) == id)
                ?? before.Objects("inventory").FirstOrDefault(i => i.L("unnoted_id", i.L("id")) == id);
            rows.Add(source?.Copy() ?? J.O(("id", id), ("unnoted_id", id), ("name", id == 995 ? "Coins" : requested.S("item_name")), ("quantity", quantity)));
            rows[^1]["quantity"] = quantity;
        }
        SetQuantity(995, after.L("cash_inventory_gp"));
        SetQuantity(itemId, afterInventory.GetValueOrDefault(itemId));
        isolated["inventory"] = J.A(rows.Cast<JsonNode?>());
        var recovered = Build(before, isolated, config);
        return recovered.SingleOrDefault(r => r.Obj("offer").I("slot") == requestedSlot);
    }
    static bool SameOffers(JsonObject before, JsonObject after)
    {
        var a = Offers(before); var b = Offers(after);
        return a is not null && b is not null && a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var row) && ProtocolHelpers.Same(p.Value, row, OfferFields));
    }
    static bool PossiblePartial(JsonObject before, JsonObject after, JsonObject config)
    {
        if (!SameOffers(before, after)) return false;
        var a = Inventory(before); var b = Inventory(after); var settled = before.Objects("offers").Where(Settled).ToArray();
        var cash = after.L("cash_inventory_gp") - before.L("cash_inventory_gp");
        var ranges = settled.Select(o => CashRange(o, config)).ToArray();
        if (cash < 0 || ranges.Any(r => r is null) || cash > ranges.Sum(r => (decimal)r!.Value.Maximum)) return false;
        var expected = settled.GroupBy(o => (int)o.L("item_id")).ToDictionary(g => g.Key, g => g.Sum(ItemAmount));
        var changed = cash > 0;
        foreach (var id in a.Keys.Union(b.Keys).Where(id => id != 995))
        {
            var delta = b.GetValueOrDefault(id) - a.GetValueOrDefault(id);
            if (delta < 0 || delta > expected.GetValueOrDefault(id)) return false;
            changed |= delta > 0;
        }
        return changed;
    }

    static List<JsonObject> Build(JsonObject before, JsonObject after, JsonObject config)
    {
        if (!Consecutive(before, after)) return [];
        var oldOffers = Offers(before)!; var newOffers = Offers(after)!;
        if (oldOffers.Count == 0 || oldOffers.Count != newOffers.Count || oldOffers.Keys.Except(newOffers.Keys).Any()) return [];
        var vanished = new List<JsonObject>();
        foreach (var (slot, old) in oldOffers)
        {
            var next = newOffers[slot];
            if (old.S("state") != "EMPTY" && next.S("state") == "EMPTY")
            { if (!Settled(old)) return []; vanished.Add(old); }
            else if (!ProtocolHelpers.Same(old, next, OfferFields)) return [];
        }
        if (vanished.Count == 0) return [];
        var a = Inventory(before); var b = Inventory(after);
        var itemDeltas = new Dictionary<JsonObject, long>();
        foreach (var group in vanished.GroupBy(o => (int)o.L("item_id")))
        {
            var actual = b.GetValueOrDefault(group.Key) - a.GetValueOrDefault(group.Key); if (actual < 0) return [];
            var flexible = group.Where(o => o.S("state") == "CANCELLED_BUY").ToArray();
            var fixedRows = group.Where(o => o.S("state") != "CANCELLED_BUY").ToArray();
            var fixedTotal = fixedRows.Sum(ItemAmount); var remaining = actual - fixedTotal;
            if (remaining < 0 || remaining > flexible.Sum(o => o.L("filled"))) return [];
            foreach (var o in fixedRows) itemDeltas[o] = ItemAmount(o);
            if (flexible.Length == 0) { if (remaining != 0) return []; }
            else if (flexible.Length == 1) itemDeltas[flexible[0]] = remaining;
            else if (remaining == 0) foreach (var o in flexible) itemDeltas[o] = 0;
            else if (remaining == flexible.Sum(o => o.L("filled"))) foreach (var o in flexible) itemDeltas[o] = o.L("filled");
            else return []; // Multiple partial-buy allocations are not uniquely attributable.
        }
        foreach (var id in a.Keys.Union(b.Keys).Union(vanished.Select(o => (int)o.L("item_id"))).Where(id => id != 995))
            if (!vanished.Any(o => o.L("item_id") == id) && b.GetValueOrDefault(id) != a.GetValueOrDefault(id)) return [];
        var cashDelta = after.L("cash_inventory_gp") - before.L("cash_inventory_gp"); if (cashDelta < 0) return [];
        var ranges = vanished.Select(o => CashRange(o, config)).ToArray(); if (ranges.Any(r => r is null)) return [];
        var minimum = ranges.Sum(r => (decimal)r!.Value.Minimum); var maximum = ranges.Sum(r => (decimal)r!.Value.Maximum);
        if (cashDelta < minimum || cashDelta > maximum) return [];
        var contributions = new JsonArray();
        for (var index = 0; index < vanished.Count; index++)
        {
            var range = ranges[index]!.Value;
            var lo = Math.Max(range.Minimum, cashDelta - (maximum - range.Maximum));
            var hi = Math.Min(range.Maximum, cashDelta - (minimum - range.Minimum));
            if (lo != hi || lo < 0 || hi > long.MaxValue) return []; // Multiple net-attribution solutions are not evidence.
            var o = vanished[index]; contributions.Add(J.O(("offer", o), ("cash_delta", (long)lo), ("item_delta", itemDeltas[o])));
        }
        var taxRules = J.O(("tax_percent", config.L("tax_percent", 2)), ("tax_cap_gp", config.L("tax_cap_gp", 5_000_000)), ("tax_exempt_ids", config.Arr("tax_exempt_ids")));
        var audit = J.O(("schema", 1), ("before", AuditState(before)), ("after", AuditState(after)), ("contributions", contributions), ("tax_rules", taxRules));
        var batchId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(audit.ToJsonString())));
        var result = new List<JsonObject>();
        foreach (var c in contributions.OfType<JsonObject>())
        {
            var o = c.Obj("offer"); var id = (int)o.L("item_id");
            result.Add(J.O(("receipt_id", "observed-" + batchId + "-" + o.L("slot")), ("status", "COMPLETED"), ("source", "OBSERVED_EXTERNAL_COLLECTION"),
                ("balance_evidence_scope", "AGGREGATE_EXACT_ALLOCATION"), ("observation_batch_id", batchId), ("account_key", before["account_key"]), ("session", before["session"]),
                ("offer", o), ("collect_started_at", before["observed_at"]), ("observed_at", after["observed_at"]), ("cash_before_gp", before["cash_inventory_gp"]),
                ("cash_after_gp", after["cash_inventory_gp"]), ("item_before", a.GetValueOrDefault(id)), ("item_after", b.GetValueOrDefault(id)),
                ("cash_delta", c["cash_delta"]), ("item_delta", c["item_delta"]), ("observation_audit", audit)));
        }
        return result;
    }
    static JsonObject AuditState(JsonObject state)
    {
        var audit = new JsonObject();
        foreach (var key in new[] { "account_key", "session", "sequence", "observed_at", "cash_inventory_gp", "logged_in", "ge_open", "bank_open", "input_busy", "inventory", "offers" })
            audit[key] = state[key]?.DeepClone();
        return audit;
    }
    public static bool ValidReceipt(JsonObject receipt)
    {
        try
        {
            if (receipt.S("source") != "OBSERVED_EXTERNAL_COLLECTION" || receipt.S("balance_evidence_scope") != "AGGREGATE_EXACT_ALLOCATION"
                || receipt["observation_audit"] is not JsonObject audit || audit.L("schema") != 1
                || audit["before"] is not JsonObject before || audit["after"] is not JsonObject after || audit["tax_rules"] is not JsonObject taxRules) return false;
            var rebuilt = Build(before, after, taxRules).SingleOrDefault(r => r.Obj("offer").L("slot") == receipt.Obj("offer").L("slot"));
            return rebuilt is not null && ProtocolHelpers.Same(rebuilt, receipt, "status", "source", "balance_evidence_scope", "observation_batch_id", "account_key", "session", "offer",
                "collect_started_at", "observed_at", "cash_before_gp", "cash_after_gp", "item_before", "item_after", "cash_delta", "item_delta", "observation_audit");
        }
        catch (Exception ex) when (ex is InvalidOperationException or OverflowException or ArgumentException) { return false; }
    }
}
