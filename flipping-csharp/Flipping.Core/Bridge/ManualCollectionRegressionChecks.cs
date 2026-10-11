using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

/// <summary>Only synthetic snapshots and temporary directories are used. No game input is sent.</summary>
public static class ManualCollectionRegressionChecks
{
    public static JsonObject Run()
    {
        var config = J.O(("tax_percent", 2), ("tax_cap_gp", 5_000_000)); var checks = new List<string>();
        void Check(string name, Action test) { test(); checks.Add(name); }
        static void Assert(bool result, string message) { if (!result) throw new InvalidOperationException("Manual collection regression: " + message); }
        static JsonObject Offer(int slot, int id, string state, long quantity, long filled, long spent, long price = 100) => J.O(
            ("slot", slot), ("item_id", id), ("state", state), ("quantity", quantity), ("filled", filled), ("spent", spent), ("price", price), ("item_name", "Fixture " + id));
        static JsonObject State(long sequence, long cash, IEnumerable<JsonObject> offers, params (int Id, long Quantity)[] items) => J.O(
            ("schema", 1), ("account_key", "manual-fixture"), ("session", "manual-session"), ("sequence", sequence), ("observed_at", 100000 + sequence),
            ("logged_in", true), ("ge_open", true), ("bank_open", false), ("input_busy", false), ("input_enabled", true), ("cash_inventory_gp", cash),
            ("inventory", J.A(items.Append((995, cash)).Where(i => i.Item2 > 0).Select(i => J.O(("id", i.Item1), ("unnoted_id", i.Item1), ("quantity", i.Item2))))),
            ("offers", J.A(Enumerable.Range(0, 8).Select(slot => offers.FirstOrDefault(o => o.L("slot") == slot)?.Copy()
                ?? J.O(("slot", slot), ("state", "EMPTY"), ("item_id", 0), ("quantity", 0), ("price", 0), ("filled", 0), ("spent", 0))))));
        static IReadOnlyList<JsonObject> Observe(JsonObject before, JsonObject after, JsonObject config)
        { var observer = new ManualCollectionObserver(config); observer.Observe(before); return observer.Observe(after); }
        static bool Valid(JsonObject r) => Lifecycle.ValidCollectionReceipt(r, "manual-fixture", (int)r.Obj("offer").L("slot"), (int)r.Obj("offer").L("item_id"),
            r.Obj("offer").L("quantity"), r.Obj("offer").L("price"), r.Obj("offer").S("state") is "BOUGHT" or "CANCELLED_BUY", 100000);

        Check("fully bought items and price-improvement refund are observed", () =>
        {
            var offer = Offer(0, 100, "BOUGHT", 4, 4, 390); var before = State(1, 1000, [offer]); var after = State(2, 1010, [], (100, 4));
            var receipt = Observe(before, after, config).Single(); Assert(Valid(receipt) && receipt.L("cash_delta") == 10 && receipt.L("item_delta") == 4, "actual buy refund and items");
        });
        Check("cancelled partial buy reconciles its exact refund and fill cost", () =>
        {
            var offer = Offer(0, 100, "CANCELLED_BUY", 10, 3, 294); var before = State(1, 1000, [offer]); var after = State(2, 1706, [], (100, 3));
            var receipt = Observe(before, after, config).Single(); Assert(Valid(receipt) && receipt.L("cash_delta") == 706 && receipt.Obj("offer").L("spent") == 294, "partial cost retained");
        });
        Check("cancelled partial buy accepts items collected before its final refund", () =>
        {
            var offer = Offer(0, 100, "CANCELLED_BUY", 10, 3, 294);
            var receipt = Observe(State(1, 1000, [offer], (100, 3)), State(2, 1706, [], (100, 3)), config).Single();
            Assert(Valid(receipt) && receipt.L("cash_delta") == 706 && receipt.L("item_delta") == 0,
                "prior partial item collection is not required a second time");
        });
        Check("separate refund and item clicks keep one complete observation", () =>
        {
            var offer = Offer(0, 100, "CANCELLED_BUY", 10, 3, 294); var observer = new ManualCollectionObserver(config);
            observer.Observe(State(1, 1000, [offer])); Assert(observer.Observe(State(2, 1706, [offer])).Count == 0, "partial is not completion");
            var receipt = observer.Observe(State(3, 1706, [], (100, 3))).Single(); Assert(Valid(receipt) && receipt.L("cash_before_gp") == 1000, "original observed balance retained");
        });
        Check("sale uses actual net cash instead of gross turnover", () =>
        {
            var offer = Offer(0, 100, "SOLD", 4, 4, 410); var receipt = Observe(State(1, 1000, [offer]), State(2, 1402, []), config).Single();
            Assert(Valid(receipt) && receipt.L("cash_delta") == 402 && receipt.L("cash_delta") != offer.L("spent"), "observed tax-adjusted cash");
        });
        Check("cancelled partial sale preserves unsold inventory and actual proceeds", () =>
        {
            var offer = Offer(0, 100, "CANCELLED_SELL", 10, 3, 300); var receipt = Observe(State(1, 1000, [offer]), State(2, 1294, [], (100, 7)), config).Single();
            Assert(Valid(receipt) && receipt.L("cash_delta") == 294 && receipt.L("item_delta") == 7, "unsold returns retained");
        });
        Check("collect all keeps actual aggregate balances and unique contributions", () =>
        {
            var offers = new[] { Offer(0, 100, "CANCELLED_BUY", 10, 3, 294), Offer(1, 200, "BOUGHT", 4, 4, 396), Offer(2, 300, "SOLD", 4, 4, 410) };
            var receipts = Observe(State(1, 1000, offers), State(2, 2112, [], (100, 3), (200, 4)), config);
            Assert(receipts.Count == 3 && receipts.All(Valid) && receipts.Sum(r => r.L("cash_delta")) == 1112, "unique per-offer actual contribution");
            Assert(receipts.All(r => r.L("cash_before_gp") == 1000 && r.L("cash_after_gp") == 2112), "all receipt balances are actual aggregate snapshots");
            Assert(receipts.Select(r => r.S("observation_batch_id")).Distinct().Count() == 1, "shared batch audit");
        });
        Check("same item in several slots validates against aggregate inventory", () =>
        {
            var offers = new[] { Offer(0, 100, "BOUGHT", 3, 3, 300), Offer(1, 100, "BOUGHT", 4, 4, 400) };
            var receipts = Observe(State(1, 1000, offers, (100, 2)), State(2, 1000, [], (100, 9)), config);
            Assert(receipts.Count == 2 && receipts.All(Valid) && receipts.All(r => r.L("item_before") == 2 && r.L("item_after") == 9), "no invented intermediate balance");
        });
        Check("ambiguous simultaneous sale attribution remains unresolved", () =>
        {
            var offers = new[] { Offer(0, 100, "SOLD", 2, 2, 202, 99), Offer(1, 200, "SOLD", 2, 2, 202, 99) };
            // Total tax is 7 GP: either slot may have contributed the extra rounding GP.
            Assert(Observe(State(1, 1000, offers), State(2, 1397, []), config).Count == 0, "ambiguous net split refused");
        });
        Check("simultaneous sales with uniquely determined rounding are accepted", () =>
        {
            var offers = new[] { Offer(0, 100, "SOLD", 2, 2, 202, 99), Offer(1, 200, "SOLD", 2, 2, 202, 99) };
            var receipts = Observe(State(1, 1000, offers), State(2, 1396, []), config);
            Assert(receipts.Count == 2 && receipts.All(Valid) && receipts.All(r => r.L("cash_delta") == 198), "unique total-rounding allocation");
        });
        Check("vanishing active offer is never treated as a completed fill", () =>
        {
            Assert(Observe(State(1, 1000, [Offer(0, 100, "BUYING", 10, 3, 300)]), State(2, 1700, [], (100, 3)), config).Count == 0, "unknown final fill refused");
            Assert(Observe(State(1, 1000, [Offer(0, 100, "SELLING", 10, 3, 300)]), State(2, 1294, [], (100, 7)), config).Count == 0, "unknown final sale refused");
        });
        Check("missing slot snapshot is not an empty slot proof", () =>
        {
            var after = State(2, 1000, [], (100, 4)); after.Arr("offers").RemoveAt(0);
            Assert(Observe(State(1, 1000, [Offer(0, 100, "BOUGHT", 4, 4, 400)]), after, config).Count == 0, "incomplete snapshot refused");
        });
        Check("extra inventory movement or wallet mismatch refuses attribution", () =>
        {
            var before = State(1, 1000, [Offer(0, 100, "BOUGHT", 4, 4, 400)]);
            Assert(Observe(before, State(2, 1000, [], (100, 4), (200, 1)), config).Count == 0, "unrelated inventory change refused");
            Assert(Observe(before, State(2, 1001, [], (100, 4)), config).Count == 0, "unrelated cash change refused");
            Assert(Observe(before, State(2, 1000, [], (100, 3)), config).Count == 0, "incomplete item count refused");
        });
        Check("unrelated offer progress or replacement refuses the observation", () =>
        {
            var buy = Offer(0, 100, "BOUGHT", 4, 4, 400); var unrelated = Offer(1, 200, "BUYING", 10, 0, 0);
            var progressed = unrelated.Copy(); progressed["filled"] = 1; progressed["spent"] = 100;
            Assert(Observe(State(1, 1000, [buy, unrelated]), State(2, 1000, [progressed], (100, 4)), config).Count == 0, "unrelated offer delta refused");
        });
        foreach (var field in new[] { "account_key", "session", "bank_open", "input_busy", "ge_open", "logged_in", "observed_at" })
            Check("context change refuses collection: " + field, () =>
            {
                var before = State(1, 1000, [Offer(0, 100, "BOUGHT", 4, 4, 400)]); var after = State(2, 1000, [], (100, 4));
                if (field is "account_key" or "session") after[field] = "different";
                else if (field == "observed_at") after[field] = 100007;
                else after[field] = field is "bank_open" or "input_busy";
                Assert(Observe(before, after, config).Count == 0, "context isolation");
            });
        Check("stale duplicate and out-of-order snapshots cannot duplicate receipts", () =>
        {
            var before = State(1, 1000, [Offer(0, 100, "BOUGHT", 4, 4, 400)]); var after = State(2, 1000, [], (100, 4)); var observer = new ManualCollectionObserver(config);
            observer.Observe(before); Assert(observer.Observe(after).Count == 1, "one completion");
            Assert(observer.Observe(after).Count == 0 && observer.Observe(before).Count == 0 && observer.Observe(after).Count == 0, "no replay completion");
        });
        Check("tampered receipt balances, contributions and audit are rejected", () =>
        {
            var original = Observe(State(1, 1000, [Offer(0, 100, "SOLD", 4, 4, 410)]), State(2, 1402, []), config).Single();
            foreach (var field in new[] { "cash_delta", "cash_after_gp", "item_after", "observation_batch_id" })
            {
                var changed = original.Copy(); changed[field] = field == "observation_batch_id" ? JsonValue.Create("changed") : JsonValue.Create(changed.L(field) + 1);
                Assert(!Valid(changed), "modified receipt refused: " + field);
            }
            var auditChanged = original.Copy(); auditChanged.Obj("observation_audit").Objects("contributions").Single()["cash_delta"] = 403;
            Assert(!Valid(auditChanged), "modified batch proof refused");
        });
        Check("tax exempt item and impossible net proceeds are distinguished", () =>
        {
            var exempt = J.O(("tax_exempt_ids", new[] { 100 })); var before = State(1, 1000, [Offer(0, 100, "SOLD", 4, 4, 410)]);
            Assert(Observe(before, State(2, 1410, []), exempt).Count == 1, "exempt exact gross received");
            Assert(Observe(before, State(2, 1411, []), config).Count == 0 && Observe(before, State(2, 1390, []), config).Count == 0, "impossible proceeds refused");
        });
        Check("tax cap is applied per sold unit and never to combined turnover", () =>
        {
            var offer = Offer(0, 100, "SOLD", 3, 3, 1_210_000_000, 400_000_000);
            var receipt = Observe(State(1, 1000, [offer]), State(2, 1_195_001_000, []), config).Single();
            Assert(Valid(receipt) && receipt.L("cash_delta") == 1_195_000_000, "three separately capped unit taxes");
        });
        Check("zero-fill cancellations return capital or inventory without invented fills", () =>
        {
            var buy = Observe(State(1, 1000, [Offer(0, 100, "CANCELLED_BUY", 4, 0, 0)]), State(2, 1400, []), config).Single();
            var sell = Observe(State(1, 1000, [Offer(0, 100, "CANCELLED_SELL", 4, 0, 0)]), State(2, 1000, [], (100, 4)), config).Single();
            Assert(Valid(buy) && Valid(sell) && buy.L("item_delta") == 0 && sell.L("cash_delta") == 0, "zero-fill settlement retained");
        });
        Check("requested overview slot recovers despite unrelated item arrival", () =>
        {
            var sale = Offer(4, 563, "SOLD", 1, 1, 120, 120);
            var before = State(1, 1000, [sale]);
            var after = State(2, 1118, [], (11230, 57));
            var receipt = ManualCollectionObserver.RecoverRequestedOverview(before, after, 4, config);
            Assert(receipt is not null && Valid(receipt) && receipt.L("cash_delta") == 118 && receipt.L("item_delta") == 0,
                "requested sale cash is isolated from another slot's item collection");
        });
        Check("passive LiveExchange persists receipts without sending game input", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "OsrsManualCollection-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try
            {
                var before = State(1, 1000, [Offer(0, 100, "BOUGHT", 4, 4, 400)]); var after = State(2, 1000, [], (100, 4));
                before["observed_at"] = J.Now; after["observed_at"] = J.Now; Storage.WriteJson(Path.Combine(folder, "live-state.json"), before);
                using var exchange = new LiveExchange(folder); exchange.Observe(); Storage.WriteJson(Path.Combine(folder, "live-state.json"), after); exchange.Observe(); exchange.Observe();
                Assert(exchange.CollectionReceipts().Count() == 1 && Valid(exchange.CollectionReceipts().Single()), "one persisted passive receipt");
                Assert(!File.Exists(Path.Combine(folder, "input-command.json")), "no game command created");
            }
            finally { Directory.Delete(folder, true); }
        });
        Check("prepared collection is completed in place without a duplicate receipt", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "OsrsManualPrepared-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try
            {
                var offer = Offer(0, 100, "BOUGHT", 4, 4, 400); var before = State(1, 1000, [offer]); var after = State(2, 1000, [], (100, 4));
                before["observed_at"] = J.Now; after["observed_at"] = J.Now;
                Storage.WriteJson(Path.Combine(folder, "live-collection-receipts.json"), J.O(("schema", 1), ("receipts", J.A(J.O(
                    ("receipt_id", "prepared-original"), ("status", "PREPARED"), ("account_key", "manual-fixture"), ("session", "manual-session"), ("offer", offer),
                    ("cash_before_gp", 1000), ("item_before", 0), ("expected_cash_delta", 0), ("expected_item_delta", 4), ("collect_started_at", before["observed_at"]))))));
                Storage.WriteJson(Path.Combine(folder, "live-state.json"), before); using var exchange = new LiveExchange(folder); exchange.Observe();
                Storage.WriteJson(Path.Combine(folder, "live-state.json"), after); exchange.Observe(); var receipt = exchange.CollectionReceipts(true).Single();
                Assert(receipt.S("receipt_id") == "prepared-original" && receipt.S("status") == "COMPLETED" && Valid(receipt), "same receipt identity settled");
                Assert(!File.Exists(Path.Combine(folder, "input-command.json")), "no game command created");
            }
            finally { Directory.Delete(folder, true); }
        });
        Check("overview collects a buy and refund with one Collect command and no detail view", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "OverviewCollect-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            using var halt = new CancellationTokenSource(); var commands = new List<JsonObject>();
            var offer = Offer(0, 100, "BOUGHT", 4, 4, 390);
            var before = State(1, 1000, [offer]); before["observed_at"] = J.Now; before["canvas_focused"] = true;
            before["widgets"] = J.A(J.O(("key", "collect"), ("symbol", "GeOffers.COLLECTALL"), ("index", 0), ("x", 20), ("y", 20), ("actions", new[] { "Collect to inventory", "Collect to bank" })));
            Storage.WriteJson(Path.Combine(folder, "live-state.json"), before);
            var server = Task.Run(async () =>
            {
                while (!halt.IsCancellationRequested)
                {
                    var path = Path.Combine(folder, "input-command.json");
                    if (File.Exists(path))
                    {
                        var cmd = (JsonObject)Storage.ReadJson(path);
                        if (!commands.Any(c => c.S("id") == cmd.S("id")))
                        {
                            commands.Add(cmd);
                            var after = State(2, 1010, [], (100, 4)); after["observed_at"] = J.Now; after["canvas_focused"] = true; after["widgets"] = before["widgets"]?.DeepClone();
                            Storage.WriteJson(Path.Combine(folder, "live-state.json"), after);
                            Storage.WriteJson(Path.Combine(folder, "input-receipt.json"), J.O(("id", cmd["id"]), ("status", "dispatched")));
                        }
                    }
                    if (commands.Count > 0)
                    {
                        var fresh = (JsonObject)Storage.ReadJson(Path.Combine(folder, "live-state.json"));
                        fresh["sequence"] = fresh.L("sequence") + 1; fresh["observed_at"] = J.Now;
                        Storage.WriteJson(Path.Combine(folder, "live-state.json"), fresh);
                    }
                    await Task.Delay(20);
                }
            });
            try
            {
                using var exchange = new LiveExchange(folder, clickPauseSeconds: 0);
                var receipt = exchange.CollectOverview(0);
                Assert(Valid(receipt) && receipt.L("cash_delta") == 10 && receipt.L("item_delta") == 4, "actual overview receipt");
                Assert(commands.Count == 1 && commands[0].S("type") == "click" && commands[0].S("widget_key") == "collect", "only overview inventory Collect click dispatched");
                Storage.WriteJson(Path.Combine(folder, "overview-collection-pending.json"), J.O(("status", "PREPARED"), ("before", before)));
                exchange.ReconcilePendingCollections(exchange.Observe());
                Assert(((JsonObject)Storage.ReadJson(Path.Combine(folder, "overview-collection-pending.json"))).S("status") == "COMPLETED"
                    && commands.Count == 1, "completed receipt clears stale overview marker without another game input");
            }
            finally { halt.Cancel(); server.GetAwaiter().GetResult(); Directory.Delete(folder, true); }
        });
        return J.O(("passed", checks.Count), ("checks", checks));
    }
}
