using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

/// <summary>Offline checks for delayed GE detail and settlement updates. IPC runs only in isolated temporary folders.</summary>
public static class GeSettlementRegressionChecks
{
    public static JsonObject Run()
    {
        var checks = new List<string>();
        void Check(bool result, string name) { if (!result) throw new InvalidOperationException("GE settlement regression: " + name); checks.Add(name); }
        var offer = Offer(); var state = Snapshot(offer);
        Check(GeSettlementView.MatchesOffer(state, offer), "exact displayed item quantity price and buy side match");
        foreach (var (index, key, value) in new (int, string, JsonNode?)[]
        {
            (7, "item_id", JsonValue.Create(2357)), (18, "text", JsonValue.Create("358")),
            (25, "text", JsonValue.Create("173 coins")), (4, "text", JsonValue.Create("Sell offer"))
        })
        {
            var changed = state.Copy(); changed.Objects("widgets").Single(w => w.S("symbol") == "GeOffers.DETAILS" && w.L("index") == index)[key] = value?.DeepClone();
            Check(!GeSettlementView.MatchesOffer(changed, offer), "wrong detail field rejected: " + index);
        }
        var missing = state.Copy(); missing["widgets"] = J.A(missing.Objects("widgets").Where(w => w.L("index") != 7).Cast<JsonNode?>());
        Check(!GeSettlementView.MatchesOffer(missing, offer), "missing displayed item rejects panel");
        var duplicate = state.Copy(); duplicate.Arr("widgets").Add(duplicate.Objects("widgets").Single(w => w.L("index") == 18).Copy());
        Check(!GeSettlementView.MatchesOffer(duplicate, offer), "ambiguous displayed quantity rejects panel");
        var busy = state.Copy(); busy["input_busy"] = true;
        Check(!GeSettlementView.MatchesOffer(busy, offer), "unfinished input rejects detail panel");
        Check(GeSettlementView.TryAmounts(state, offer, out var amount) && amount.L("cash_delta") == 61_748 && amount.L("item_delta") == 0, "cancelled zero-fill buy requires exact full refund");
        var stale = state.Copy(); stale.Objects("widgets").Single(w => w.S("symbol") == "GeOffers.DETAILS_COLLECT")["item_quantity"] = 65;
        Check(!GeSettlementView.TryAmounts(stale, offer, out _), "stale previous sale icon cannot establish a refund");
        stale.Objects("widgets").Single(w => w.S("symbol") == "GeOffers.DETAILS_COLLECT")["item_quantity"] = 61_747;
        Check(!GeSettlementView.TryAmounts(stale, offer, out _), "one GP refund discrepancy remains rejected");
        var active = offer.Copy(); active["state"] = "BUYING";
        Check(!GeSettlementView.TryAmounts(Snapshot(active), active, out _), "active buy cannot be collected");
        var partial = Offer(); partial["filled"] = 10; partial["spent"] = 1_690;
        var partialState = Snapshot(partial); partialState.Objects("widgets").Single(w => w.S("symbol") == "GeOffers.DETAILS_COLLECT")["item_quantity"] = 60_058;
        partialState.Arr("widgets").Add(Icon("item", 1607, 10));
        Check(GeSettlementView.TryAmounts(partialState, partial, out amount) && amount.L("cash_delta") == 60_058 && amount.L("item_delta") == 10, "partial buy preserves actual spend and exact item quantity");
        partialState.Objects("widgets").Last()["item_quantity"] = 9;
        Check(!GeSettlementView.TryAmounts(partialState, partial, out _), "partial item discrepancy remains rejected");
        Check(GeSettlementView.TryAmounts(state, offer, out _, 61_748, 0), "pending settlement reads exact remaining icons");
        Check(!GeSettlementView.TryAmounts(state, offer, out _, 61_747, 0), "pending settlement refuses a changed refund");
        Check(!GeSettlementView.TryAmounts(state, offer, out _, 61_748, null), "pending amounts require both frozen balances");
        var overflow = Offer(); overflow["quantity"] = long.MaxValue; var overflowState = Snapshot(overflow);
        Check(!GeSettlementView.TryAmounts(overflowState, overflow, out _), "overflowing refund arithmetic never produces an amount");
        var sale = Offer(); sale["state"] = "SOLD"; sale["filled"] = sale.L("quantity"); sale["spent"] = 62_000;
        Check(GeSettlementView.MatchesOffer(Snapshot(sale), sale) && GeSettlementView.TryAmounts(Snapshot(sale), sale, out _), "sale uses its verified displayed net cash amount");
        Check(GeSettlementView.SameIdentity(offer, active) && !GeSettlementView.SameIdentity(offer, sale), "fill state changes preserve identity while side changes do not");

        var recovered = IpcRead(synchronize: true);
        Check(recovered.ReadSucceeded && recovered.Commands == 1 && recovered.RepeatedReadCommands == 1 && recovered.CancelCommands == 1 && !recovered.HasReceipt,
            "delayed panel and refund recover after one view click without repeat actions or fabricated receipts");
        var refused = IpcRead(synchronize: false);
        Check(!refused.ReadSucceeded && refused.Commands == 1 && !refused.HasReceipt, "persistent refund mismatch waits boundedly and sends no collect click");
        return J.O(("passed", checks.Count), ("checks", checks));
    }

    static JsonObject Offer() => J.O(("slot", 0), ("item_id", 1607), ("item_name", "Sapphire"), ("quantity", 359), ("price", 172), ("filled", 0), ("spent", 0), ("state", "CANCELLED_BUY"));
    static JsonObject Widget(int index, string text = "", int item = -1) => J.O(("key", "details:" + index), ("symbol", "GeOffers.DETAILS"), ("index", index), ("item_id", item), ("text", text), ("x", 20), ("y", 20));
    static JsonObject Icon(string key, int item, long quantity) => J.O(("key", key), ("symbol", "GeOffers.DETAILS_COLLECT"), ("index", 2), ("item_id", item),
        ("item_quantity", quantity), ("actions", new[] { "Collect", "Bank" }), ("x", 50), ("y", 50));
    static JsonObject Snapshot(JsonObject offer) => J.O(("schema", 1), ("observed_at", J.Now), ("sequence", 1), ("account_key", "ge-settlement-fixture"), ("session", "ge-settlement-session"),
        ("logged_in", true), ("ge_open", true), ("input_enabled", true), ("input_busy", false), ("cash_inventory_gp", 100), ("inventory", new JsonArray()),
        ("offers", J.A(offer)), ("widgets", J.A(Widget(-1), Widget(4, GeSettlementView.Buying(offer) ? "Buy offer" : "Sell offer"), Widget(7, item: (int)offer.L("item_id")),
            Widget(18, offer.L("quantity").ToString("N0", System.Globalization.CultureInfo.InvariantCulture)), Widget(25, offer.L("price") + " coins"), Icon("cash", 995, 61_748))));

    static (bool ReadSucceeded, int Commands, int RepeatedReadCommands, int CancelCommands, bool HasReceipt) IpcRead(bool synchronize)
    {
        var folder = Path.Combine(Path.GetTempPath(), "OsrsGeSettlement-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        using var halt = new CancellationTokenSource();
        try
        {
            Storage.WriteJson(Path.Combine(folder, "config.json"), new JsonObject());
            var initial = Snapshot(Offer()); initial["widgets"] = J.A(J.O(("key", "slot"), ("symbol", "GeOffers.INDEX_0"), ("index", -1), ("id", 1),
                ("item_id", -1), ("x", 10), ("y", 10), ("width", 100), ("height", 100), ("actions", new[] { "View offer" })));
            Storage.WriteJson(Path.Combine(folder, "live-state.json"), initial);
            var commandCount = 0;
            var responder = Task.Run(async () =>
            {
                string? previous = null; var sequence = 1L; DateTimeOffset? viewed = null;
                while (!halt.IsCancellationRequested)
                {
                    var path = Path.Combine(folder, "input-command.json");
                    if (File.Exists(path))
                    {
                        var command = Storage.ReadJson(path).AsObject();
                        if (command.S("id") != previous)
                        {
                            previous = command.S("id"); Interlocked.Increment(ref commandCount); viewed ??= DateTimeOffset.UtcNow;
                            Storage.WriteJson(Path.Combine(folder, "input-receipt.json"), J.O(("id", previous), ("status", "dispatched")));
                        }
                    }
                    if (viewed is not null)
                    {
                        var next = Snapshot(Offer()); next["sequence"] = ++sequence; var elapsed = (DateTimeOffset.UtcNow - viewed.Value).TotalMilliseconds;
                        if (elapsed < 250) next.Objects("widgets").Single(w => w.L("index") == 7)["item_id"] = 2357;
                        if (elapsed < 650 || !synchronize) next.Objects("widgets").Single(w => w.S("symbol") == "GeOffers.DETAILS_COLLECT")["item_quantity"] = 65;
                        Storage.WriteJson(Path.Combine(folder, "live-state.json"), next);
                    }
                    await Task.Delay(30);
                }
            });
            try
            {
                using var exchange = new LiveExchange(folder, clickPauseSeconds: 0);
                var succeeded = false;
                try { var amounts = exchange.CollectionAmounts(0); succeeded = amounts.L("cash_delta") == 61_748 && amounts.L("item_delta") == 0; }
                catch (TransientUIError) { }
                var commands = Volatile.Read(ref commandCount); var repeat = commands; var cancel = commands;
                if (succeeded)
                {
                    _ = exchange.CollectionAmounts(0); repeat = Volatile.Read(ref commandCount);
                    exchange.Cancel(0); cancel = Volatile.Read(ref commandCount);
                }
                return (succeeded, commands, repeat, cancel, File.Exists(Path.Combine(folder, "live-collection-receipts.json")));
            }
            finally { halt.Cancel(); responder.GetAwaiter().GetResult(); }
        }
        finally { foreach (var path in Directory.EnumerateFiles(folder)) File.Delete(path); Directory.Delete(folder); }
    }
}
