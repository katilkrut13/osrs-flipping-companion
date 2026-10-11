using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public interface IOverviewCollector
{
    JsonObject CollectOverview(int slot, long precollectedItems = 0);
}

public sealed record Offer(int Slot, int ItemId, string ItemName, string Side, long Price, long Quantity, long Filled,
                          long OpenedAt, long? LastProgressAt = null);

public sealed class Portfolio
{
    public bool Verified { get; set; }
    public long ObservedAt { get; set; }
    public long FreeCashGp { get; set; }
    public List<Offer> Offers { get; set; } = [];
    public Dictionary<int, long> BoughtLast4h { get; set; } = [];
    public bool BuyLimitsVerified { get; set; }
    public int SlotCapacity { get; set; } = 3;
    public bool? Members { get; set; }
    public long DeployedCostGp { get; set; }
    public Dictionary<string, long> BoughtByLimitGroup { get; set; } = [];
}

public interface IExchange
{
    string? Account { get; }
    bool IsLive { get; }
    JsonObject Observe();
    JsonObject Connection();
    int Submit(string side, int itemId, string itemName, long quantity, long price);
    void Cancel(int slot);
    JsonObject? Collect(int slot);
    JsonObject CollectionAmounts(int slot);
    void Overview();
    void RequestStop();
    void Resume();
}

public interface IBridgeExchange : IExchange
{
    void Send(JsonObject state, JsonObject command);
    void WidgetAction(JsonObject state, JsonObject widget, string action);
    void NpcAction(JsonObject state, string action);
}
