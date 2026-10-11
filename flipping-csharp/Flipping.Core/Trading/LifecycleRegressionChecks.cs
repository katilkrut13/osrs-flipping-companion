using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public static class LifecycleRegressionChecks
{
    public static JsonObject Run(JsonObject? suppliedConfig = null)
    {
        var config = suppliedConfig?.Copy() ?? new JsonObject(); config["tax_percent"] = 2; config["tax_cap_gp"] = 5_000_000; config["tax_exempt_ids"] = new JsonArray();
        config["max_price_age_seconds"] = 300; config["max_sell_reprices"] = 4; config["max_reprice_fraction"] = .02; config["min_reprice_position_profit_gp"] = 1;
        config["stagnation_timeout_minutes"] = 12; config["simulation_slot_capacity"] = 8; config["target_slots"] = 8; config["budget_gp"] = 2_000_000;
        config["max_slot_allocation_gp"] = 500_000; config["max_item_budget_fraction"] = .25; config["cash_reserve_fraction"] = 0; config["target_fill_slots"] = false;
        config["monetary_limits_enabled"] = true; config["max_test_loss_gp"] = 10000;
        var checks = new JsonArray(); void Check(bool condition, string name) { if (!condition) throw new InvalidDataException("Lifecycle regression: " + name); checks.Add(name); }
        void Throws(Action action, string name) { try { action(); } catch (InvalidOperationException) { Check(true, name); return; } throw new InvalidDataException("Expected guarded rejection: " + name); }
        (SimExchange Exchange, OrderManager Manager, JsonObject Position) Setup()
        {
            var e = new SimExchange(config); e.Inventory[28991] = 1058; var m = new OrderManager(e, config);
            var p = OrderManager.NewPosition(28991, "Atlatl dart", 1058, 121, 128, e.Now, e.Account!); p["stage"] = "HELD"; p["buy_spent"] = 128018; p["owned_quantity"] = 1058; p["acquired_quantity"] = 1058; m.Positions.Add(p); return (e, m, p);
        }
        JsonObject Quote(SimExchange e, long price = 128, int id = 28991) => J.O(("generated_at_epoch", e.Now), ("candidates", new JsonArray()),
            ("exit_quotes", new JsonArray(J.O(("id", id), ("indicative_sell_gp", price), ("sell_time", e.Now)))));
        var (exchange, manager, position) = Setup(); var report = Quote(exchange); report["candidates"] = new JsonArray(J.O(("id", 28991), ("strategy_eligible", false), ("risk", "Elevated")));
        Check(manager.Step(exchange.Observe(), report) && manager.Step(exchange.Observe(), report), "entry rejection cannot block owned sale");
        var slot = (int)position["slot"].L(); Check(exchange.Offers[slot]["quantity"].L() == 1058 && exchange.Offers[slot]["price"].L() == 128, "exact owned quantity submitted");
        exchange.Fill(slot); Check(manager.Step(exchange.Observe(), report) && manager.Step(exchange.Observe(), report), "sale collect transitions finish");
        Check(position["stage"].S() == "DONE" && position["realized_profit_gp"].L() == 5290 && position["sale_proceeds_gp"].L() == 133308, "after-tax observed Atlatl profit is 5290");
        var (sameE, sameM, sameP) = Setup(); sameP["reprice_count"] = 4; sameM.Step(sameE.Observe(), Quote(sameE)); sameM.Step(sameE.Observe(), Quote(sameE));
        Check(sameP["stage"].S() == "WAIT_SELL" && sameP["reprice_count"].L() == 4, "same ask resumes at downward-cut cap");
        var (cutE, cutM, cutP) = Setup(); cutM.Step(cutE.Observe(), Quote(cutE, 127)); cutM.Step(cutE.Observe(), Quote(cutE, 127));
        Check(cutP["sell_price"].L() == 127 && cutP["reprice_count"].L() == 1, "actual price cut consumes one adjustment");
        cutP["stage"] = "HELD"; cutP["reprice_count"] = 4; Check(cutM.HeldSellTarget(cutP, Quote(cutE, 126)).Reason == "HELD_REPRICE_LIMIT_REACHED", "fifth downward cut blocked");
        var air = OrderManager.NewPosition(573, "Air orb", 198, 1219, 1263, exchange.Now, exchange.Account!); air["stage"] = "HELD"; air["buy_spent"] = 241362; air["owned_quantity"] = 198;
        Check(manager.HeldSellTarget(air, Quote(exchange, 1175, 573)).Reason == "HELD_QUOTE_BELOW_COST", "below-cost quote holds owned Air orb");
        Check(198 * (1175 - MarketAnalyzer.Tax(1175, 573, config)) - 241362 == -13266, "Air orb loss not fabricated as profit");
        foreach (var bad in new JsonNode?[] { JsonValue.Create(true), JsonValue.Create("100000"), JsonValue.Create(100000d), JsonValue.Create(exchange.Now - 301), JsonValue.Create(exchange.Now + 61), null })
        { var invalid = Quote(exchange); invalid.Objects("exit_quotes").Single()["sell_time"] = bad?.DeepClone(); Check(manager.ExitSellQuote(position, invalid, exchange.Now) == null, "strict exit timestamp " + (bad?.ToJsonString() ?? "null")); }
        foreach (var bad in new JsonNode?[] { JsonValue.Create(true), JsonValue.Create("128"), JsonValue.Create(128d), JsonValue.Create(0), JsonValue.Create(2147483648L), null })
        { var invalid = Quote(exchange); invalid.Objects("exit_quotes").Single()["indicative_sell_gp"] = bad?.DeepClone(); Check(manager.ExitSellQuote(position, invalid, exchange.Now) == null, "strict exit price " + (bad?.ToJsonString() ?? "null")); }
        var duplicate = Quote(exchange); duplicate.Arr("exit_quotes").Add(duplicate.Arr("exit_quotes")[0]?.DeepClone()); Check(manager.ExitSellQuote(position, duplicate, exchange.Now) == null, "duplicate quote is ambiguous");
        var emptyExit = Quote(exchange); emptyExit["exit_quotes"] = new JsonArray(); emptyExit["candidates"] = new JsonArray(J.O(("id", 28991), ("indicative_sell_gp", 128)));
        Check(manager.ExitSellQuote(position, emptyExit, exchange.Now) == null, "missing independent quote never falls back to entry");
        var aged = Quote(exchange); exchange.Now += 301; Check(manager.ExitSellQuote(position, aged, exchange.Now) == null, "new clock does not rejuvenate stale price");
        var (stockE, stockM, stockP) = Setup(); stockE.Inventory[21944] = 96; stockM.Step(stockE.Observe(), Quote(stockE)); stockM.Step(stockE.Observe(), Quote(stockE));
        Check(stockE.Inventory[21944] == 96 && stockE.Offers.Values.All(o => o["item_id"].L() == 28991), "unmanaged inventory remains untouched");
        stockP["stage"] = "HELD"; stockP["storage_location"] = "BANK"; stockE.Inventory.Remove(28991); Check(!stockM.Step(stockE.Observe(), Quote(stockE)) && stockP["last_risk_reason"].S() == "HELD_STOCK_IN_BANK", "bank stock waits for withdrawal");
        var (wrongE, wrongM, wrongP) = Setup(); var wrong = wrongE.Observe(); wrong["account_key"] = "other"; Throws(() => wrongM.Step(wrong, Quote(wrongE)), "changed account rejects owned sale");
        wrong = wrongE.Observe(); wrong["inventory"] = new JsonArray(); Check(!wrongM.Step(wrong, Quote(wrongE)) && wrongP.S("stage") == "STOPPED" && wrongP.Obj("manual_review").S("reason") == "INVENTORY_CHANGED", "changed owned quantity quarantines only its sale"); Check(wrongE.Offers.Count == 0, "invalid ownership submits no offer");
        wrongP["owned_quantity"] = 500; wrongP["sale_proceeds_gp"] = 70000; Check(wrongM.HeldSellTarget(wrongP, Quote(wrongE)).Target == 128, "partial prior net proceeds applied once");
        wrongP["sale_proceeds_gp"] = 60000; Check(wrongM.HeldSellTarget(wrongP, Quote(wrongE)).Reason == "HELD_QUOTE_BELOW_COST", "partial sale below total basis held");
        var (staleE, staleM, staleP) = Setup(); staleM.Step(staleE.Observe(), Quote(staleE)); staleM.Step(staleE.Observe(), Quote(staleE)); staleE.Now += 721;
        Check(!staleM.Step(staleE.Observe(), J.O(("exit_quotes", new JsonArray()))) && staleE.Offers.Values.Single()["state"].S() == "SELLING", "stagnant sale stays open without fresh profitable exit");
        Check(staleP["last_risk_reason"].S() == "STAGNANT_WAITING_FOR_EXIT_PRICE", "stagnant wait cause recorded");
        Check(staleM.Step(staleE.Observe(), Quote(staleE, 127)), "stagnant sale uses independent quote");
        Check(staleP["pending_sell_price"].L() == 127 && staleE.Offers.Values.Single()["state"].S() == "CANCELLED_SELL", "stagnant cancel freezes intended reprice");
        staleM.Step(staleE.Observe(), Quote(staleE, 127)); staleM.Step(staleE.Observe(), Quote(staleE, 127)); staleM.Step(staleE.Observe(), Quote(staleE, 127));
        Check(staleP["reprice_count"].L() == 1 && staleE.Offers.Values.Single()["price"].L() == 127, "cancel collect repost counts cut once");
        var temp = Path.Combine(Path.GetTempPath(), "osrs-csharp-lifecycle-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        try
        {
            var path = Path.Combine(temp, "positions.json"); var recoverP = wrongP.Copy(); recoverP["stage"] = "HELD"; recoverP["custom_future_field"] = J.O(("preserve", true)); recoverP.Remove("storage_location"); Storage.WriteJson(path, new JsonArray(recoverP));
            var recovered = new OrderManager(wrongE, config, path); recovered.Recover(); Check(recovered.Positions.Single().Obj("custom_future_field")["preserve"].B() && recovered.Positions.Single()["storage_location"].S() == "INVENTORY", "journal recovery preserves unknown fields and legacy defaults");
            var ledgerE = new SimExchange(config); using var ledger = new Ledger(Path.Combine(temp, "ledger.sqlite")); var portfolio = ledger.Ingest(ledgerE.Observe()); Check(!portfolio.BuyLimitsVerified, "new account history starts unverified");
            ledger.ConfirmNoUntrackedBuys(ledgerE.Account!, ledgerE.Now); Check(ledger.Ingest(ledgerE.Observe()).BuyLimitsVerified, "explicit no-untracked confirmation unlocks entries");
            var buySlot = ledgerE.Submit("BUY", 2434, "Prayer potion(4)", 10, 100); ledger.Ingest(ledgerE.Observe()); ledgerE.Fill(buySlot, 3); var filled = ledger.Ingest(ledgerE.Observe());
            Check(filled.BoughtLast4h.GetValueOrDefault(2434) == 3 && filled.BoughtByLimitGroup.GetValueOrDefault(LimitRules.LimitGroup(2434, "Prayer potion(4)")) == 3, "actual buy fills counted in item and family history");
            Check(ledger.Ingest(ledgerE.Observe()).BoughtLast4h[2434] == 3, "repeated snapshot does not duplicate purchases");
            ledgerE.Now += 31; Check(!ledger.Ingest(ledgerE.Observe()).BuyLimitsVerified, "31-second observation gap invalidates buy baseline");
            var candidate = J.O(("id", 1), ("name", "Fixture"), ("indicative_buy_gp", 100), ("indicative_sell_gp", 120), ("estimated_test_loss_gp", 22), ("buy_limit", 100));
            var marginE = new SimExchange(config); var margin = new MarginRunner(marginE, config, Path.Combine(temp, "margin.json")); var mp = new Portfolio { Verified = true, BuyLimitsVerified = true, ObservedAt = marginE.Now, FreeCashGp = marginE.Cash, Members = true, SlotCapacity = 8 };
            margin.Start(candidate, mp, marginE.Observe()); Check(margin.Step(marginE.Observe()), "margin test submits one reverse buy"); var marginSlot = (int)margin.Test!["slot"].L();
            Check(marginE.Offers[marginSlot]["quantity"].L() == 1 && marginE.Offers[marginSlot]["price"].L() == 120, "margin buy target is high price and quantity one");
            marginE.Fill(marginSlot); margin.Step(marginE.Observe()); margin.Step(marginE.Observe()); marginSlot = (int)margin.Test["slot"].L(); Check(marginE.Offers[marginSlot]["price"].L() == 100, "margin sale target is low price");
            marginE.Fill(marginSlot); margin.Step(marginE.Observe()); margin.Step(marginE.Observe()); var result = margin.Test.Obj("result");
            Check(margin.Test["stage"].S() == "DONE" && result["actual_buy_gp"].L() == 120 && result["actual_net_sale_gp"].L() == 98 && result["actual_test_loss_gp"].L() == 22, "margin actual net outcome verified");
            var receipt = marginE.CollectionReceipts().Last(); Check(Lifecycle.ValidCollectionReceipt(receipt, marginE.Account!, marginSlot, 1, 1, 100, false, 100000), "exact completed collection receipt accepted");
            var changed = receipt.Copy(); changed["cash_delta"] = 99; Check(!Lifecycle.ValidCollectionReceipt(changed, marginE.Account!, marginSlot, 1, 1, 100, false, 100000), "changed receipt amount rejected");
            Check(!Lifecycle.ValidCollectionReceipt(receipt, "other", marginSlot, 1, 1, 100, false), "receipt cannot cross accounts");
            Check(ledger.RecordMarginResult(ledgerE.Account!, result, ledgerE.Now) && !ledger.RecordMarginResult(ledgerE.Account!, result, ledgerE.Now), "margin result ledger idempotent");
            Check(ledger.TotalTestLoss(ledgerE.Account!) == 22, "margin cost recorded once");
            var done = position.Copy(); done["account_key"] = ledgerE.Account; done["started_at"] = ledgerE.Now - 30; done["completed_at"] = ledgerE.Now; ledger.RecordPosition(ledgerE.Account!, done); ledger.RecordPosition(ledgerE.Account!, done);
            Check(ledger.ProfitHistory(ledgerE.Account!, ledgerE.Now).Objects("closed_positions").Count() == 1 && ledger.Performance(ledgerE.Account!, ledgerE.Now)["total_position_profit_gp"].L() == 5290, "completed positions ledger idempotent");
            Check(ledger.ProfitHistory("other", ledgerE.Now).Objects("closed_positions").Count() == 0, "profit history separated by account");
            Check(ledger.DailyRisk(ledgerE.Account!, ledgerE.Now)["net_gp"].L() == 5268, "daily risk uses net margin test cost");
            var shutE = new SimExchange(config); var shutM = new OrderManager(shutE, config); var shutP = OrderManager.NewPosition(2, "Other", 10, 100, 120, shutE.Now, shutE.Account!);
            shutP["slot"] = shutE.Submit("BUY", 2, "Other", 10, 100); shutP["stage"] = "WAIT_BUY"; shutM.Positions.Add(shutP); shutE.Fill((int)shutP["slot"].L(), 3);
            var shutdown = new ShutdownCoordinator(shutE, Path.Combine(temp, "shutdown.json")); shutdown.Start(shutE.Observe());
            for (var i = 0; i < 10 && shutdown.Pending; i++) { var state = shutE.Observe(); var collected = shutdown.Step(state); if (collected != null) { shutM.ApplyShutdownCollection(collected, shutE.Observe()); shutdown.Acknowledge(collected); } }
            Check(!shutdown.Pending && shutE.Offers.Count == 0 && shutE.Inventory[2] == 3 && shutE.Cash == 1999700, "stop cancels and exactly collects partial purchase");
            Check(shutP["stage"].S() == "PAUSED_HELD" && shutP["buy_spent"].L() == 300 && shutP["owned_quantity"].L() == 3, "stop retains owned stock without liquidation");
            var stopEntry = shutdown.Plan!.Objects("entries").Single(); shutM.ApplyShutdownCollection(stopEntry, shutE.Observe()); Check(shutP.Arr("shutdown_entries").Count == 1, "stop collection application idempotent");
            var interruptedE = new SimExchange(config); var interruptedSlot = interruptedE.Submit("BUY", 2, "Other", 10, 100);
            var interruptedStop = new ShutdownCoordinator(interruptedE); interruptedStop.Start(interruptedE.Observe());
            interruptedE.Cancel(interruptedSlot); interruptedE.Collect(interruptedSlot);
            var wrongStopState = interruptedE.Observe(); wrongStopState["account_key"] = "other";
            Throws(() => interruptedStop.Step(wrongStopState), "interrupted stop enters review for changed account");
            Check(!interruptedStop.ResumeReview(wrongStopState), "restart cannot resume stop for another account");
            Check(interruptedStop.ResumeReview(interruptedE.Observe()), "restart resumes reviewed stop with exact collection receipt");
            var recoveredStop = interruptedStop.Step(interruptedE.Observe());
            Check(recoveredStop?.S("stage") == "DONE" && recoveredStop["collection_receipt"] is JsonObject && interruptedE.Offers.Count == 0,
                "collection completed before stop journal update recovers without duplicate input");
            interruptedStop.Acknowledge(recoveredStop!); interruptedStop.Step(interruptedE.Observe());
            Check(!interruptedStop.Pending && interruptedE.Cash == 2_000_000, "recovered stop completes and releases restart");
            var unknownE = new SimExchange(config); unknownE.Inventory[2] = 1;
            var unknownSlot = unknownE.Submit("SELL", 2, "Unknown sale", 1, 100); unknownE.Fill(unknownSlot);
            var unknownStop = new ShutdownCoordinator(unknownE); unknownStop.Start(unknownE.Observe());
            unknownE.Offers.Remove(unknownSlot); var otherAccount = unknownE.Observe(); otherAccount["account_key"] = "other";
            Throws(() => unknownStop.Step(otherAccount), "missing sale review fixture");
            Check(unknownStop.ResumeReview(unknownE.Observe()), "missing fully filled sale does not block other shutdown work");
            var unknownEntry = unknownStop.Plan!.Objects("entries").Single();
            Check(unknownEntry.S("stage") == "UNRESOLVED" && unknownEntry["cash_delta"] == null && unknownEntry["collection_receipt"] == null,
                "missing sale preserves unknown proceeds without fabricating cash or receipt");
            unknownStop.Step(unknownE.Observe()); Check(!unknownStop.Pending, "isolated missing sale releases stop start transition");
        }
        finally { Directory.Delete(temp, true); }
        JsonObject ProfitPosition() { var p = OrderManager.NewPosition(1, "Fixture", 10, 101, 120, 100, "account-A"); p["slot"] = 2; p["stage"] = "WAIT_SELL"; p["buy_spent"] = 1001; p["acquired_quantity"] = 10; p["owned_quantity"] = 7; p["sale_proceeds_gp"] = 354; return p; }
        var profitP = ProfitPosition(); var profitBefore = profitP.ToJsonString(); var profit = ProfitReport.Build("account-A", 200, [profitP]); var row = profit.Objects("rows").Single();
        Check(row["sold_quantity"].L() == 3 && row["realized_cost_gp"].L() == 300 && row["remaining_cost_gp"].L() == 701 && row["realized_profit_gp"].L() == 54, "partial cost prorata floors exact coin basis");
        Check(profitP.ToJsonString() == profitBefore, "profit report never mutates journal");
        profitP["stage"] = "DONE"; profitP["owned_quantity"] = 0; profitP["sale_proceeds_gp"] = 1180; profitP["completed_at"] = 160; profitP["realized_profit_gp"] = 179;
        row = ProfitReport.Build("account-A", 200, [profitP]).Objects("rows").Single(); Check(row["realized_cost_gp"].L() == 1001 && row["realized_profit_gp"].L() == 179 && row["remaining_cost_gp"].L() == 0, "last sale realizes every cost-basis coin");
        var other = profitP.Copy(); other["account_key"] = "other"; other["trade_id"] = "other"; var future = profitP.Copy(); future["trade_id"] = "future"; future["completed_at"] = 201;
        var history = ProfitReport.Build("account-A", 200, [profitP], closedPositions: [profitP.Copy(), other, future]); Check(history.Objects("rows").Count() == 1, "current journal overrides history and filters future or other account");
        var voidP = profitP.Copy(); voidP["stage"] = "VOID"; Check(!ProfitReport.Build("account-A", 200, [voidP], closedPositions: [profitP]).Objects("rows").Any(), "audited VOID suppresses stale profit history");
        profitP = ProfitPosition(); profitP["sale_proceeds_gp"] = 0; row = ProfitReport.Build("account-A", 200, [profitP]).Objects("rows").Single(); Check(row["unverified_sale_quantity"].L() == 3 && row["realized_profit_gp"].L() == 0 && row["remaining_cost_gp"].L() == 1001, "quantity disappearance does not invent loss");
        profitP["owned_quantity"] = 10; var soldOffer = J.O(("slot", 2), ("item_id", 1), ("state", "SELLING"), ("quantity", 10), ("price", 120), ("filled", 3), ("spent", 360));
        row = ProfitReport.Build("account-A", 200, [profitP], offers: [soldOffer]).Objects("rows").Single(); Check(row["sale_uncollected_quantity"].L() == 3 && row["realized_profit_gp"].L() == 0 && row["net_sale_proceeds_gp"].L() == 0, "uncollected sale not counted as profit");
        profitP["stage"] = "HELD"; profitP["last_risk_reason"] = "HELD_QUOTE_BELOW_COST"; Check(ProfitReport.Build("account-A", 200, [profitP]).Objects("rows").Single()["status_label"].S().Contains("maliyeti"), "held exit reason displayed");
        profitP["stage"] = "WAIT_SELL"; profitP["last_risk_reason"] = "STAGNANT_WAITING_FOR_EXIT_PRICE"; Check(ProfitReport.Build("account-A", 200, [profitP]).Objects("rows").Single()["status_label"].S().StartsWith("Satış açık"), "stagnant open sale reason displayed");
        var marginLoss = J.O(("account_key", "account-A"), ("item_id", 1), ("actual_buy_gp", 120), ("actual_net_sale_gp", 99), ("actual_test_loss_gp", 21), ("completed_at", 150), ("verified", true));
        var marginGain = marginLoss.Copy(); marginGain["item_id"] = 2; marginGain["actual_buy_gp"] = 100; marginGain["actual_net_sale_gp"] = 104; marginGain["actual_test_loss_gp"] = -4; marginGain["completed_at"] = 160;
        Check(ProfitReport.Build("account-A", 200, [], marginResults: [marginLoss, marginLoss.Copy(), marginGain]).Obj("totals")["net_profit_gp"].L() == -17, "signed test gains and losses deduplicated");
        return J.O(("passed", checks.Count), ("checks", checks));
    }
}
