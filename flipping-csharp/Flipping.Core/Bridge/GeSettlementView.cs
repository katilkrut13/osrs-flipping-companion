using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OsrsFlipping.Core;

/// <summary>Checks the displayed GE detail panel against an observed offer before using its controls or amounts.</summary>
public static class GeSettlementView
{
    static JsonObject[] Visible(JsonObject state, string symbol, int index) => state.Objects("widgets")
        .Where(w => w.S("symbol") == symbol && w.L("index") == index && w.L("x", -1) >= 0 && w.L("y", -1) >= 0).ToArray();

    static long? Number(JsonObject state, int index)
    {
        var widgets = Visible(state, "GeOffers.DETAILS", index); if (widgets.Length != 1) return null;
        var match = Regex.Match(ProtocolHelpers.Plain(widgets[0].S("text")), "^([\\d,]+)(?:\\s+coins?)?$");
        return match.Success && long.TryParse(match.Groups[1].Value.Replace(",", ""), out var number) ? number : null;
    }

    public static bool Buying(JsonObject offer) => offer.S("state") is "BUYING" or "BOUGHT" or "CANCELLED_BUY";
    public static bool Settled(JsonObject offer) => offer.S("state") is "BOUGHT" or "SOLD" or "CANCELLED_BUY" or "CANCELLED_SELL";

    public static bool MatchesOffer(JsonObject state, JsonObject offer)
    {
        if (state.B("input_busy") || offer.S("state") is not ("BUYING" or "BOUGHT" or "CANCELLED_BUY" or "SELLING" or "SOLD" or "CANCELLED_SELL")) return false;
        var item = Visible(state, "GeOffers.DETAILS", 7); var side = Visible(state, "GeOffers.DETAILS", 4);
        return Visible(state, "GeOffers.DETAILS", -1).Length == 1 && item.Length == 1 && side.Length == 1
            && item[0].L("item_id", -1) == offer.L("item_id") && offer.L("item_id") > 0
            && Number(state, 18) == offer.L("quantity") && Number(state, 25) == offer.L("price")
            && ProtocolHelpers.Plain(side[0].S("text")).Equals(Buying(offer) ? "Buy offer" : "Sell offer", StringComparison.OrdinalIgnoreCase);
    }

    public static bool SameIdentity(JsonObject left, JsonObject right) => ProtocolHelpers.Same(left, right, "item_id", "quantity", "price") && Buying(left) == Buying(right);

    public static bool TryAmounts(JsonObject state, JsonObject offer, out JsonObject amounts, long? remainingCash = null, long? remainingItems = null)
    {
        amounts = new(); if (!Settled(offer) || !MatchesOffer(state, offer)) return false;
        var icons = state.Objects("widgets").Where(w => w.S("symbol") == "GeOffers.DETAILS_COLLECT" && w.L("item_id", -1) > 0
            && w.L("item_quantity") > 0 && w.L("x", -1) >= 0 && w.L("y", -1) >= 0).GroupBy(w => w.S("key")).Select(g => g.Last()).ToArray();
        try
        {
            var cash = icons.Where(w => w.L("item_id") == 995).Sum(w => w.L("item_quantity"));
            var items = icons.Where(w => w.L("item_id") != 995).Sum(w => w.L("item_quantity"));
            var expectedItems = Buying(offer) ? offer.L("filled") : checked(offer.L("quantity") - offer.L("filled"));
            if (offer.L("filled") < 0 || offer.L("filled") > offer.L("quantity") || offer.L("spent") < 0 || expectedItems < 0) return false;
            if (remainingCash.HasValue != remainingItems.HasValue) return false;
            if (remainingCash is long pendingCash && remainingItems is long pendingItems)
            {
                if (pendingCash < 0 || pendingItems < 0 || cash != pendingCash || items != pendingItems) return false;
            }
            else
            {
                if (icons.Length == 0 && (expectedItems > 0 || offer.L("spent") > 0)) return false;
                if (items != expectedItems) return false;
                if (Buying(offer) && cash != checked(offer.L("quantity") * offer.L("price") - offer.L("spent"))) return false;
            }
            amounts = J.O(("cash_delta", cash), ("item_delta", items)); return true;
        }
        catch (OverflowException) { return false; }
    }
}
