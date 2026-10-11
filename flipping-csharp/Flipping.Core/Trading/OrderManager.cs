using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public sealed class OrderManager
{
    readonly IExchange backend; readonly JsonObject config; readonly string? journal;
    public List<JsonObject> Positions { get; } = [];
    public OrderManager(IExchange backend, JsonObject config, string? journal = null) { this.backend = backend; this.config = config; this.journal = journal; }
    static bool ValidPrecollectedBuy(JsonObject p, JsonObject raw, JsonObject state)
    {
        var delta = Lifecycle.CountItem(state, p.I("item_id")) - p.L("inventory_before");
        var filled = raw.L("filled"); var spent = raw.L("spent");
        return raw.S("state") is "BUYING" or "BOUGHT" or "CANCELLED_BUY"
            && delta >= 0 && delta <= filled && filled > 0 && spent > 0
            && spent <= checked(filled * raw.L("price"));
    }
    public void Save()
    {
        foreach (var p in Positions) if (!new[] { "STOPPED", "PAUSED_HELD", "DONE", "VOID" }.Contains(p["stage"].S())) p["recovery_stage"] = p["stage"]?.DeepClone();
        if (journal != null) Storage.WriteJson(journal, J.A(Positions));
    }
    public List<JsonObject> ReviewFailedSubmissions(JsonObject state, JsonObject plan, IEnumerable<JsonObject> events, IEnumerable<JsonObject> receipts)
    {
        var repaired = new List<JsonObject>();
        foreach (var p in Positions)
        {
            var evidence = RecoveryEvidence.UnsubmittedStopEvidence(p, plan, state, events, receipts);
            if (evidence == null) continue;
            p["no_submission_evidence"] = evidence.Copy(); p["stage"] = "VOID"; p["recovery_stage"] = "";
            p["last_risk_reason"] = "PROVED_NOT_SUBMITTED_AT_STOP_CHECKPOINT"; repaired.Add(evidence);
        }
        if (repaired.Count > 0) Save(); return repaired;
    }
    public JsonObject Add(JsonObject candidate, long quantity, Portfolio portfolio, JsonObject state, bool marginVerified = false)
    {
        if (!marginVerified || !portfolio.Verified || !portfolio.BuyLimitsVerified) throw new InvalidOperationException("Fresh verified margin and account limits required");
        if (!candidate["strategy_eligible"].B(true)) throw new InvalidOperationException("Strategy rejected candidate");
        var age = state["observed_at"].L() - portfolio.ObservedAt; if (age < 0 || age > 30) throw new InvalidOperationException("Portfolio is stale");
        if (Positions.Any(p => new[] { "STOPPED", "PAUSED_HELD" }.Contains(p["stage"].S()) && !ManualCompletion.IsIsolatedOwnedSale(p, state))) throw new InvalidOperationException("Stopped positions require reconciliation before new orders");
        var active = Positions.Where(p => !new[] { "DONE", "VOID", "STOPPED" }.Contains(p["stage"].S())).ToList();
        var capSlots = LimitRules.SlotCapacity(config, portfolio); var id = (int)candidate["id"].L(); var name = candidate["name"].S();
        if (active.Count >= capSlots || portfolio.Offers.Count >= capSlots) throw new InvalidOperationException("Active slot cap");
        if (active.Any(p => p["item_id"].L() == id) || portfolio.Offers.Any(o => o.ItemId == id)) throw new InvalidOperationException("Duplicate item position");
        var group = candidate["limit_group"].S(); if (group == "") group = LimitRules.LimitGroup(id, name);
        if (active.Any(p => LimitRules.LimitGroup((int)p["item_id"].L(), p["item_name"].S()) == group)) throw new InvalidOperationException("Shared buy-limit family already active");
        if (portfolio.Members != true && candidate["members"].B()) throw new InvalidOperationException("Members item unavailable");
        int? allocationCount = candidate["funded_allocation_position_count"].IsInteger() ? candidate.I("funded_allocation_position_count") : null;
        var (cap, spendable) = Strategy.AllocationLimits(config, portfolio, allocationCount); var buy = candidate["indicative_buy_gp"].L(); var cost = checked(quantity * buy);
        var reserved = portfolio.Offers.Where(o => o.Side == "BUY").Sum(o => o.Quantity * o.Price);
        var committed = active.Where(p => !new[] { "WAIT_BUY", "BUY_SUBMITTING" }.Contains(p["stage"].S()))
            .Sum(p => (long)Math.Ceiling((decimal)p["buy_spent"].L() * p["owned_quantity"].L() / Math.Max(p["acquired_quantity"].L(), 1)));
        if (quantity <= 0 || cost > Math.Min(Math.Min(cap, spendable), config["budget_gp"].L() - reserved - committed)) throw new InvalidOperationException("Capital allocation exceeded");
        if (quantity > candidate["buy_limit"].L() - LimitRules.UsedLimit(candidate, portfolio)) throw new InvalidOperationException("4-hour buy limit exceeded");
        var free = LimitRules.EmptySlots(state, config).ToList(); if (free.Count == 0) throw new InvalidOperationException("No observed free offer slot");
        var p = NewPosition(id, name, quantity, buy, candidate["indicative_sell_gp"].L(), state["observed_at"].L(), state["account_key"].S(), Lifecycle.CountItem(state, id));
        p["portfolio_tier"] = candidate.S("portfolio_tier", Strategy.PortfolioTier(candidate));
        p["planned_cycle_minutes"] = candidate.D("funded_cycle_minutes_proxy", candidate.D("cycle_minutes_proxy"));
        p["planned_expected_profit_gp"] = candidate.D("funded_expected_stress_profit_after_fill_gp");
        Positions.Add(p); Save(); p["slot"] = free.Min(); p["submission_cash_gp"] = state["cash_inventory_gp"]?.DeepClone(); p["stage"] = "BUY_SUBMITTING"; p["recovery_stage"] = "BUY_SUBMITTING"; Save();
        try { p["slot"] = backend.Submit("BUY", id, name, quantity, buy); }
        catch (OfferNotSubmitted ex)
        {
            if (ex.ItemId == id && ex.Side == "BUY")
            {
                p["no_submission_evidence"] = J.O(("status", "NOT_SUBMITTED"), ("account_key", p["account_key"]), ("trade_id", p["trade_id"]),
                    ("item_id", id), ("quantity", quantity), ("price", buy), ("phase", ex.Phase), ("details", ex.Details));
                p["stage"] = "VOID"; p["recovery_stage"] = ""; Save();
            }
            throw;
        }
        p["stage"] = "WAIT_BUY"; Save(); return p;
    }
    public static JsonObject NewPosition(int id, string name, long quantity, long buy, long sell, long now, string account, long before = 0) => J.O(
        ("item_id", id), ("item_name", name), ("quantity", quantity), ("buy_price", buy), ("sell_price", sell), ("created_at", now), ("slot", null),
        ("stage", "NEW"), ("buy_spent", 0), ("owned_quantity", 0), ("acquired_quantity", 0), ("inventory_before", before), ("cash_before_collect", 0),
        ("realized_profit_gp", null), ("last_risk_reason", ""), ("trade_id", Guid.NewGuid().ToString()), ("started_at", now), ("completed_at", null),
        ("account_key", account), ("recovery_stage", ""), ("sale_proceeds_gp", 0), ("storage_location", "INVENTORY"), ("submission_cash_gp", null),
        ("collection_expected_gp", null), ("collection_remaining_quantity", 0), ("shutdown_entries", null), ("last_progress_at", now), ("last_filled_quantity", 0),
        ("reprice_count", 0), ("pending_sell_price", null), ("collection_offer", null), ("collection_inventory_before", null), ("collection_receipt", null),
        ("buy_collection_receipt", null), ("no_submission_evidence", null));
    void Collect(JsonObject raw, JsonObject p, string stage)
    {
        if (backend is IOverviewCollector overview)
        {
            var actual = overview.CollectOverview(raw.I("slot"), stage == "BUY_COLLECTING" ? p.L("buy_precollected_quantity") : 0);
            p["collection_offer"] = raw.Copy(); p["stage"] = stage;
            p["cash_before_collect"] = actual["cash_before_gp"]?.DeepClone();
            p["collection_inventory_before"] = actual["item_before"]?.DeepClone();
            p["collection_expected_gp"] = actual["cash_delta"]?.DeepClone(); p["collection_receipt"] = actual.Copy();
            if (stage == "BUY_COLLECTING") p["buy_collection_receipt"] = actual.Copy(); Save(); return;
        }
        var slot = (int)raw["slot"].L(); var amounts = backend.CollectionAmounts(slot); var before = backend.Observe();
        var check = before.Objects("offers").FirstOrDefault(o => o["slot"].L() == slot);
        if (check == null || !Lifecycle.SameFields(check, raw, "state", "item_id", "quantity", "filled", "price", "spent")) throw new InvalidOperationException("Position changed while reading collection amounts");
        p["cash_before_collect"] = before["cash_inventory_gp"]?.DeepClone(); p["collection_inventory_before"] = Lifecycle.CountItem(before, (int)p["item_id"].L());
        p["collection_offer"] = raw.Copy(); p["collection_expected_gp"] = amounts["cash_delta"]?.DeepClone(); p["collection_receipt"] = null; p["stage"] = stage; Save();
        var receipt = Lifecycle.CollectionReceipt(backend, before, raw, amounts, backend.Collect(slot)); p["collection_receipt"] = receipt;
        if (stage == "BUY_COLLECTING") p["buy_collection_receipt"] = receipt.Copy(); Save();
    }
    void ApplySaleCollection(JsonObject p, long proceeds, long remaining, JsonObject state)
    {
        p["sale_proceeds_gp"] = p["sale_proceeds_gp"].L() + proceeds; p["owned_quantity"] = remaining; p["collection_expected_gp"] = null;
        p["collection_receipt"] = null; p["collection_offer"] = null;
        if (remaining > 0)
        {
            if (p["pending_sell_price"].L() > 0) { p["sell_price"] = p["pending_sell_price"]?.DeepClone(); p["pending_sell_price"] = null; p["reprice_count"] = p["reprice_count"].L() + 1; p["stage"] = "BUY_COLLECTING"; }
            else p["stage"] = "HELD";
        }
        else { p["realized_profit_gp"] = p["sale_proceeds_gp"].L() - p["buy_spent"].L(); p["stage"] = "DONE"; p["completed_at"] = state["observed_at"]?.DeepClone(); }
        Save();
    }
    public long? ExitSellQuote(JsonObject position, JsonObject? report, long? now = null)
    {
        if (report == null) return null;
        var independent = report.ContainsKey("exit_quotes"); var quotes = report[independent ? "exit_quotes" : "candidates"] as JsonArray; if (quotes == null) return null;
        var matches = quotes.OfType<JsonObject>().Where(q => q["id"].IsInteger() && q["id"].L() == position["item_id"].L()).ToList(); if (matches.Count != 1) return null;
        var q = matches[0]; var target = q["indicative_sell_gp"]; if (!target.IsInteger() || target.L() <= 0 || target.L() > int.MaxValue) return null;
        if (independent)
        {
            var stamp = q["sell_time"]; var check = now ?? (report["generated_at_epoch"].IsInteger() ? report["generated_at_epoch"].L() : (long?)null);
            if (!stamp.IsInteger() || stamp.L() <= 0 || check == null || check - stamp.L() < -60 || check - stamp.L() > config["max_price_age_seconds"].L(300)) return null;
        }
        else if (now != null && report["generated_at_epoch"] != null)
        {
            var generated = report["generated_at_epoch"]; if (!generated.IsInteger() || now - generated.L() < 0 || now - generated.L() > 90) return null;
            var age = q["price_age_seconds"]; if (age != null && (!age.IsInteger() || age.L() < 0 || age.L() + now - generated.L() > config["max_price_age_seconds"].L(300))) return null;
        }
        return target.L();
    }
    public (long? Target, string Reason) HeldSellTarget(JsonObject p, JsonObject? report, long? now = null)
    {
        if (p["storage_location"].S() != "INVENTORY") return (null, "HELD_STOCK_IN_BANK");
        var target = ExitSellQuote(p, report, now); if (target == null) return (null, "HELD_WAITING_FOR_FRESH_QUOTE");
        var net = p["owned_quantity"].L() * (target.Value - MarketAnalyzer.Tax(target.Value, (int)p["item_id"].L(), config)) + p["sale_proceeds_gp"].L() - p["buy_spent"].L();
        var stopLoss = config.L("max_position_loss_gp", 0);
        if (stopLoss > 0 && net <= -stopLoss) return (target, "HELD_STOP_LOSS_EXIT_READY");
        if (net < config["min_reprice_position_profit_gp"].L(1)) return (null, "HELD_QUOTE_BELOW_COST");
        if (target < p["sell_price"].L())
        {
            if (p["reprice_count"].L() >= config["max_sell_reprices"].L(2)) return (null, "HELD_REPRICE_LIMIT_REACHED");
            target = Math.Max(target.Value, (long)Math.Ceiling(p["sell_price"].L() * (1 - config["max_reprice_fraction"].D(.01))));
        }
        return (target, "HELD_PROFITABLE_SELL_READY");
    }
    public void ReviewManualChanges(JsonObject state)
    {
        if (!state.B("logged_in") || !state.B("ge_open") || state.B("input_busy")) return;
        var changed = false;
        foreach (var p in Positions.Where(p => p.S("stage") is not ("DONE" or "VOID") && p.S("account_key") == state.S("account_key")))
        {
            var stage = p.S("stage") == "STOPPED" ? p.S("recovery_stage") : p.S("stage");
            string? reason = null; JsonObject? raw = null;
            if (stage is "WAIT_BUY" or "WAIT_SELL")
            {
                raw = state.Objects("offers").FirstOrDefault(o => Lifecycle.Slot(o) == Lifecycle.Slot(p) && o.S("state") != "EMPTY");
                var buying = stage == "WAIT_BUY";
                if (raw == null) reason = "OFFER_MISSING";
                else if (!Lifecycle.MatchingOffer(raw, Lifecycle.Slot(p), p.I("item_id"), p.L(buying ? "quantity" : "owned_quantity"), p.L(buying ? "buy_price" : "sell_price"), buying)) reason = "OFFER_CHANGED";
                else if (Lifecycle.CountItem(state, p.I("item_id")) != p.L("inventory_before"))
                {
                    if (buying && ValidPrecollectedBuy(p, raw, state))
                    {
                        p["buy_precollected_quantity"] = Lifecycle.CountItem(state, p.I("item_id")) - p.L("inventory_before");
                        if (p.S("stage") == "STOPPED") { p["stage"] = "WAIT_BUY"; p.Remove("manual_review"); p.Remove("buy_limit_review_recorded"); }
                        changed = true; continue;
                    }
                    reason = "PARTIAL_COLLECTION_OR_STOCK_CHANGE";
                }
            }
            else if (stage is "HELD" or "PAUSED_HELD" or "BUY_COLLECTING" && p.S("storage_location", "INVENTORY") == "INVENTORY")
            {
                if (Lifecycle.HasOffer(state, p.L("item_id"))) reason = "UNEXPECTED_ITEM_OFFER";
                else if (Lifecycle.CountItem(state, p.I("item_id")) != p.L("inventory_before") + p.L("owned_quantity")) reason = "INVENTORY_CHANGED";
            }
            else if (stage is "SELL_COLLECTING" or "PARTIAL_SELL_COLLECTING")
            {
                var receipt = p["collection_receipt"] as JsonObject;
                var proved = Lifecycle.ValidCollectionReceipt(receipt, p.S("account_key"), Lifecycle.Slot(p), p.I("item_id"), p.L("owned_quantity"), p.L("sell_price"), false, p.L("created_at"));
                if (Lifecycle.HasOffer(state, p.L("item_id")) || Lifecycle.CountItem(state, p.I("item_id")) != p.L("inventory_before") + p.L("collection_remaining_quantity")
                    || p["collection_expected_gp"] is null || !proved && state.L("cash_inventory_gp") != p.L("cash_before_collect") + p.L("collection_expected_gp")) reason = "COLLECTION_CHANGED";
            }
            if (reason == null || p.Obj("manual_review").S("reason") == reason && p.S("stage") == "STOPPED") continue;
            p["manual_review"] = J.O(("reason", reason), ("observed_at", state.L("observed_at")), ("account_key", state.S("account_key")),
                ("previous_stage", stage), ("observed_offer", raw));
            p["recovery_stage"] = stage; p["stage"] = "STOPPED"; p["last_risk_reason"] = "MANUAL_" + reason; changed = true;
        }
        if (changed) Save();
    }
    bool TryResolveExactStoppedStock(JsonObject p, JsonObject state)
    {
        if (p.S("stage") != "STOPPED" || p.S("account_key") != state.S("account_key")
            || !state.B("logged_in") || !state.B("ge_open") || state.B("input_busy")) return false;
        var recovery = p.S("recovery_stage"); var reason = p.Obj("manual_review").S("reason");
        var id = p.I("item_id"); var before = p.L("inventory_before"); var count = Lifecycle.CountItem(state, id);
        if (Lifecycle.HasOffer(state, id)) return false;
        if (recovery == "WAIT_SELL" && reason is "OFFER_MISSING" or "OFFER_CHANGED"
            && p.L("owned_quantity") > 0 && p.L("buy_spent") > 0
            && count == before + p.L("owned_quantity"))
        {
            p["automatic_recovery_evidence"] = J.O(("status", "OWNED_STOCK_EXACT_AFTER_OFFER_LOSS"),
                ("item_id", id), ("quantity", p.L("owned_quantity")), ("observed_at", state.L("observed_at")));
            p["slot"] = null; p["stage"] = "HELD"; p["recovery_stage"] = "HELD";
            p["last_risk_reason"] = "AUTO_RECOVERED_EXACT_OWNED_STOCK"; p.Remove("manual_review");
            return true;
        }
        if (recovery == "WAIT_BUY" && reason == "OFFER_MISSING" && p.L("quantity") > 0
            && p.L("last_filled_quantity") > 0 && p.L("buy_precollected_quantity") > 0
            && count == before + p.L("quantity"))
        {
            // The full requested quantity is present after an observed partial fill.
            // The exact price improvement is unavailable, so use the maximum possible
            // order cost. This keeps profit accounting conservative.
            var maximumCost = checked(p.L("quantity") * p.L("buy_price"));
            p["buy_spent"] = maximumCost; p["owned_quantity"] = p["quantity"]?.DeepClone();
            p["acquired_quantity"] = p["quantity"]?.DeepClone();
            p["automatic_recovery_evidence"] = J.O(("status", "FULL_BUY_EXACT_STOCK_CONSERVATIVE_COST"),
                ("item_id", id), ("quantity", p.L("quantity")), ("recorded_cost_gp", maximumCost),
                ("observed_partial_fill", p.L("last_filled_quantity")), ("observed_at", state.L("observed_at")));
            p["slot"] = null; p["stage"] = "BUY_COLLECTING"; p["recovery_stage"] = "BUY_COLLECTING";
            p["last_risk_reason"] = "AUTO_RECOVERED_FULL_BUY_CONSERVATIVE_COST";
            p.Remove("manual_review"); p.Remove("buy_precollected_quantity"); p.Remove("buy_limit_review_recorded");
            return true;
        }
        return false;
    }
    public long? RepriceTarget(JsonObject p, JsonObject? report, long? now = null)
    {
        if (report == null || p["reprice_count"].L() >= config["max_sell_reprices"].L(2)) return null;
        var quote = ExitSellQuote(p, report, now); if (quote == null) return null;
        var target = Math.Max(Math.Min(p["sell_price"].L() - 1, quote.Value), (long)Math.Ceiling(p["sell_price"].L() * (1 - config["max_reprice_fraction"].D(.01)))); var remaining = p["owned_quantity"].L();
        if (target <= 0 || target >= p["sell_price"].L() || remaining <= 0) return null;
        var net = remaining * (target - MarketAnalyzer.Tax(target, (int)p["item_id"].L(), config)) + p["sale_proceeds_gp"].L() - p["buy_spent"].L();
        var stopLoss = config.L("max_position_loss_gp", 0);
        if (stopLoss > 0 && net <= -stopLoss) return quote;
        return net >= config["min_reprice_position_profit_gp"].L(1) ? target : null;
    }
    public bool Step(JsonObject state, JsonObject? report = null)
    {
        if (state["input_busy"].B()) return false;
        ReviewManualChanges(state);
        foreach (var p in Positions)
        {
            var stage = p["stage"].S(); var id = (int)p["item_id"].L(); var slot = Lifecycle.Slot(p);
            if (stage == "STOPPED") continue;
            if (!new[] { "DONE", "VOID" }.Contains(stage) && p["account_key"].S() != state["account_key"].S()) throw new InvalidOperationException("Position account changed");
            var raw = state.Objects("offers").FirstOrDefault(o => o["slot"].L(-1) == slot && o["item_id"].L() == id);
            if (stage == "HELD")
            {
                var (target, reason) = HeldSellTarget(p, report, state["observed_at"].L()); p["last_risk_reason"] = reason;
                if (p["storage_location"].S() != "INVENTORY") { Save(); continue; }
                if (Lifecycle.CountItem(state, id) != p["inventory_before"].L() + p["owned_quantity"].L()) throw new InvalidOperationException("Held stock quantity changed");
                if (target == null) { Save(); continue; }
                if (!LimitRules.EmptySlots(state, config).Any()) { p["last_risk_reason"] = "HELD_WAITING_FOR_FREE_SLOT"; Save(); continue; }
                if (target < p["sell_price"].L()) p["reprice_count"] = p["reprice_count"].L() + 1;
                p["sell_price"] = target; p["stage"] = "BUY_COLLECTING"; p["last_risk_reason"] = "HELD_PROFITABLE_SELL_READY"; Save(); return true;
            }
            if (raw != null && new[] { "WAIT_BUY", "WAIT_SELL" }.Contains(stage))
            {
                var buying = stage == "WAIT_BUY";
                if (!Lifecycle.MatchingOffer(raw, slot, id, p[buying ? "quantity" : "owned_quantity"].L(), p[buying ? "buy_price" : "sell_price"].L(), buying))
                { p["stage"] = "STOPPED"; Save(); throw new InvalidOperationException("Observed offer does not match the owned position"); }
            }
            if (stage == "WAIT_BUY" && raw?["state"].S() == "BUYING" && report != null)
            {
                var flags = report.Objects("flagged_items").Where(f => f["id"].L() == id).Select(f => f["flag"].S()).ToList();
                var candidate = report.Objects("candidates").FirstOrDefault(c => c["id"].L() == id);
                var reasons = candidate?.Arr("strategy_reasons").Select(r => r.S()).Where(r => new[] { "RECENT_DOWNTREND", "UNSTABLE_PRICE", "UNUSUAL_SPREAD", "FRAGILE_MARGIN", "ONE_SIDED_FLOW" }.Contains(r));
                if (reasons != null) flags.AddRange(reasons);
                if (flags.Count > 0) { p["last_risk_reason"] = string.Join(", ", flags); Save(); backend.Cancel(slot!.Value); return true; }
            }
            if (new[] { "WAIT_BUY", "WAIT_SELL" }.Contains(stage) && raw != null)
            {
                if (raw["filled"].L() > p["last_filled_quantity"].L()) { p["last_filled_quantity"] = raw["filled"]?.DeepClone(); p["last_progress_at"] = state["observed_at"]?.DeepClone(); Save(); }
                if (LimitRules.IsStagnant(state["observed_at"].L(), p["created_at"].L(), p["last_progress_at"] == null ? null : p["last_progress_at"].L(), config, p) && new[] { "BUYING", "SELLING" }.Contains(raw["state"].S()))
                {
                    if (stage == "WAIT_SELL")
                    {
                        var target = RepriceTarget(p, report, state["observed_at"].L()); p["pending_sell_price"] = target;
                        if (target == null) { p["last_risk_reason"] = "STAGNANT_WAITING_FOR_EXIT_PRICE"; Save(); continue; }
                        p["last_risk_reason"] = "STAGNANT_REPRICE";
                    }
                    backend.Cancel(slot!.Value); Save(); return true;
                }
            }
            if (stage == "WAIT_BUY" && raw != null && new[] { "BOUGHT", "CANCELLED_BUY" }.Contains(raw["state"].S()))
            { p["buy_spent"] = raw["spent"]?.DeepClone(); p["owned_quantity"] = raw["filled"]?.DeepClone(); p["acquired_quantity"] = raw["filled"]?.DeepClone(); Collect(raw, p, "BUY_COLLECTING"); return true; }
            if (stage == "BUY_COLLECTING" && raw == null && Lifecycle.CountItem(state, id) == p["inventory_before"].L() + p["owned_quantity"].L())
            {
                if (p["owned_quantity"].L() == 0) { p["stage"] = "DONE"; p["realized_profit_gp"] = 0; p["completed_at"] = state["observed_at"]?.DeepClone(); Save(); return true; }
                var slots = LimitRules.EmptySlots(state, config).ToList(); if (slots.Count == 0) return false;
                p["slot"] = slots.Min(); p["submission_cash_gp"] = state["cash_inventory_gp"]?.DeepClone(); p["stage"] = "SELL_SUBMITTING"; p["recovery_stage"] = "SELL_SUBMITTING"; Save();
                p["slot"] = backend.Submit("SELL", id, p["item_name"].S(), p["owned_quantity"].L(), p["sell_price"].L()); p["stage"] = "WAIT_SELL";
                p["created_at"] = state["observed_at"]?.DeepClone(); p["last_progress_at"] = state["observed_at"]?.DeepClone(); p["last_filled_quantity"] = 0; Save(); return true;
            }
            if (stage == "WAIT_SELL" && raw != null && new[] { "SOLD", "CANCELLED_SELL" }.Contains(raw["state"].S()))
            { p["collection_remaining_quantity"] = raw["quantity"].L() - raw["filled"].L(); Collect(raw, p, raw["state"].S() == "SOLD" ? "SELL_COLLECTING" : "PARTIAL_SELL_COLLECTING"); return true; }
            if (new[] { "SELL_COLLECTING", "PARTIAL_SELL_COLLECTING" }.Contains(stage) && raw == null)
            {
                var receiptOk = Lifecycle.ValidCollectionReceipt(p["collection_receipt"] as JsonObject, p["account_key"].S(), slot, id, p["owned_quantity"].L(), p["sell_price"].L(), false, p["created_at"].L())
                    && p["collection_receipt"]?["cash_delta"].L() == p["collection_expected_gp"].L();
                if (p["collection_expected_gp"] == null || !receiptOk && state["cash_inventory_gp"].L() != p["cash_before_collect"].L() + p["collection_expected_gp"].L()
                    || Lifecycle.CountItem(state, id) != p["inventory_before"].L() + p["collection_remaining_quantity"].L()) throw new InvalidOperationException("Sale collection balance mismatch");
                ApplySaleCollection(p, p["collection_expected_gp"].L(), p["collection_remaining_quantity"].L(), state); return true;
            }
        }
        return false;
    }
    public bool Reconcile(JsonObject state, bool allowIsolatedOwnedSales = false)
    {
        if (state["input_busy"].B()) return false; if (backend is IReceiptExchange receipts) receipts.ReconcilePendingCollections(state); var safe = true;
        foreach (var p in Positions)
        {
            if (new[] { "DONE", "VOID" }.Contains(p["stage"].S())) continue;
            if (allowIsolatedOwnedSales && ManualCompletion.IsIsolatedOwnedSale(p, state))
            {
                var observed = state.Objects("offers").Single(o => o.I("item_id") == p.I("item_id") && o.S("state") != "EMPTY");
                var firstSeen = p.Obj("external_owned_sale").L("first_observed_at", state.L("observed_at"));
                p["external_owned_sale"] = J.O(("offer", observed.Copy()), ("first_observed_at", firstSeen), ("session", state["session"]));
                continue;
            }
            if (p["account_key"].S() == "" || p["account_key"].S() != state["account_key"].S()) { safe = false; continue; }
            if (TryResolveExactStoppedStock(p, state)) continue;
            var stage = p["stage"].S() == "STOPPED" ? p["recovery_stage"].S() : p["stage"].S(); var id = (int)p["item_id"].L(); var slot = Lifecycle.Slot(p);
            var count = Lifecycle.CountItem(state, id); var hasOffer = Lifecycle.HasOffer(state, id); var owned = p["owned_quantity"].L(); var before = p["inventory_before"].L();
            if (stage is "HELD" or "PAUSED_HELD" && !hasOffer && p["external_owned_sale"] is JsonObject externalSale)
            {
                var externalOffer = externalSale.Obj("offer");
                var externalReceipt = Lifecycle.HistoricalCollection(backend, p.S("account_key"), Lifecycle.Slot(externalOffer), id,
                    owned, externalOffer.L("price"), false, externalSale.L("first_observed_at"));
                if (externalReceipt != null && count == before + externalReceipt.L("item_delta"))
                {
                    p["external_sale_collection_receipt"] = externalReceipt.Copy();
                    ApplySaleCollection(p, externalReceipt.L("cash_delta"), externalReceipt.L("item_delta"), state);
                    p.Remove("external_owned_sale"); continue;
                }
            }
            var interruption = p["user_interrupted_submission"];
            var absenceProved = interruption is null || interruption is JsonObject interrupted
                && interrupted.B("before_confirm") && interrupted.S("account_key") == p.S("account_key")
                && interrupted.S("session") != "" && interrupted.S("session") == state.S("session");
            if (stage == "NEW" && p["submission_cash_gp"] == null) { p["stage"] = "VOID"; continue; }
            if (stage == "HELD") { if (hasOffer || count != before + owned && p["storage_location"].S() == "INVENTORY") safe = false; else { p["stage"] = "HELD"; p.Remove("manual_review"); } continue; }
            if (stage == "PAUSED_HELD") { if (!hasOffer && p["storage_location"].S() == "INVENTORY" && count == before + owned) { p["stage"] = "BUY_COLLECTING"; p.Remove("manual_review"); } else safe = false; continue; }
            var buying = new[] { "WAIT_BUY", "BUY_SUBMITTING" }.Contains(stage); var selling = new[] { "WAIT_SELL", "SELL_SUBMITTING" }.Contains(stage);
            var matching = state.Objects("offers").Where(o => Lifecycle.MatchingOffer(o, slot, id, p[buying ? "quantity" : "owned_quantity"].L(), p[buying ? "buy_price" : "sell_price"].L(), buying)).ToList();
            if ((buying || selling) && matching.Count == 1)
            {
                if (count != before && !(buying && ValidPrecollectedBuy(p, matching[0], state))) { safe = false; continue; }
                if (buying && count != before) p["buy_precollected_quantity"] = count - before;
                p["stage"] = buying ? "WAIT_BUY" : "WAIT_SELL"; p.Remove("user_interrupted_submission");
            }
            else if ((buying || selling) && !hasOffer)
            {
                var receipt = Lifecycle.HistoricalCollection(backend, p["account_key"].S(), slot, id, p[buying ? "quantity" : "owned_quantity"].L(), p[buying ? "buy_price" : "sell_price"].L(), buying, p["created_at"].L());
                if (receipt == null && buying && Lifecycle.ValidCollectionReceipt(p["buy_collection_receipt"] as JsonObject, p["account_key"].S(), slot, id, p["quantity"].L(), p["buy_price"].L(), true, p["created_at"].L())) receipt = (p["buy_collection_receipt"] as JsonObject)?.Copy();
                if (receipt == null)
                {
                    if (new[] { "BUY_SUBMITTING", "SELL_SUBMITTING" }.Contains(stage) && absenceProved && p["submission_cash_gp"] != null
                        && state["cash_inventory_gp"].L() == p["submission_cash_gp"].L() && count == before + (buying ? 0 : owned))
                    { p["stage"] = buying ? "VOID" : "BUY_COLLECTING"; p.Remove("user_interrupted_submission"); }
                    else safe = false;
                }
                else if (buying && count == before + receipt["offer"]?["filled"].L())
                { p["buy_spent"] = receipt["offer"]?["spent"]?.DeepClone(); p["owned_quantity"] = receipt["offer"]?["filled"]?.DeepClone(); p["acquired_quantity"] = receipt["offer"]?["filled"]?.DeepClone(); p["buy_collection_receipt"] = receipt; p["stage"] = "BUY_COLLECTING"; p.Remove("user_interrupted_submission"); }
                else if (!buying && count == before + receipt["item_delta"].L()) { ApplySaleCollection(p, receipt["cash_delta"].L(), receipt["item_delta"].L(), state); p.Remove("user_interrupted_submission"); } else safe = false;
            }
            else if (stage == "BUY_COLLECTING" && !hasOffer && count == before + owned) p["stage"] = "BUY_COLLECTING";
            else if (new[] { "SELL_COLLECTING", "PARTIAL_SELL_COLLECTING" }.Contains(stage) && p["collection_expected_gp"] != null && !hasOffer && count == before + p["collection_remaining_quantity"].L())
            {
                var receipt = p["collection_receipt"] as JsonObject ?? Lifecycle.HistoricalCollection(backend, p["account_key"].S(), slot, id, owned, p["sell_price"].L(), false, p["created_at"].L());
                var ok = Lifecycle.ValidCollectionReceipt(receipt, p["account_key"].S(), slot, id, owned, p["sell_price"].L(), false, p["created_at"].L()) && receipt?["cash_delta"].L() == p["collection_expected_gp"].L();
                if (ok || state["cash_inventory_gp"].L() == p["cash_before_collect"].L() + p["collection_expected_gp"].L()) ApplySaleCollection(p, p["collection_expected_gp"].L(), p["collection_remaining_quantity"].L(), state); else safe = false;
            }
            else if (new[] { "BUY_COLLECTING", "SELL_COLLECTING", "PARTIAL_SELL_COLLECTING" }.Contains(stage) && state.Objects("offers").Any(o => Lifecycle.MatchingOffer(o, slot, id, stage == "BUY_COLLECTING" ? p["quantity"].L() : owned, p[stage == "BUY_COLLECTING" ? "buy_price" : "sell_price"].L(), stage == "BUY_COLLECTING", true))) p["stage"] = stage == "BUY_COLLECTING" ? "WAIT_BUY" : "WAIT_SELL";
            else safe = false;
        }
        foreach (var p in Positions.Where(p => p.S("stage") != "STOPPED")) { p.Remove("manual_review"); p.Remove("buy_limit_review_recorded"); }
        Save(); return safe;
    }
    public void ApplyShutdownCollection(JsonObject entry, JsonObject state)
    {
        var o = entry["offer"] as JsonObject ?? throw new InvalidOperationException("Shutdown offer missing");
        foreach (var p in Positions)
        {
            if ((p["shutdown_entries"] as JsonArray)?.Any(v => JsonNode.DeepEquals(v, entry["id"])) == true) continue;
            if (p["stage"].S() is "DONE" or "VOID" || p["account_key"].S() != state["account_key"].S() || Lifecycle.Slot(p) != Lifecycle.Slot(o) || p["item_id"].L() != o["item_id"].L()) continue;
            var buying = new[] { "BOUGHT", "CANCELLED_BUY" }.Contains(o["state"].S());
            if (entry["collection_receipt"]?["prior_proceeds_unverified"].B() == true) p["profit_unknown"] = true;
            if (!buying && entry.L("cash_delta") == 0 && p.L("owned_quantity") == o.L("quantity") - o.L("filled")
                && p.S("stage") is "HELD" or "PAUSED_HELD" && p.L("sell_price") == o.L("price")
                && Lifecycle.ValidCollectionReceipt(entry["collection_receipt"] as JsonObject, p.S("account_key"), Lifecycle.Slot(o), o.I("item_id"), o.L("quantity"), o.L("price"), false, p.L("started_at")))
            { p["stage"] = "PAUSED_HELD"; Lifecycle.AddShutdownEntry(p, entry); continue; }
            if (o["quantity"].L() != p[buying ? "quantity" : "owned_quantity"].L() || o["price"].L() != p[buying ? "buy_price" : "sell_price"].L()) throw new InvalidOperationException("Shutdown offer does not match position");
            if (buying) { p["buy_spent"] = o["spent"]?.DeepClone(); p["owned_quantity"] = o["filled"]?.DeepClone(); p["acquired_quantity"] = o["filled"]?.DeepClone(); }
            else { p["sale_proceeds_gp"] = p["sale_proceeds_gp"].L() + entry["cash_delta"].L(); p["owned_quantity"] = o["quantity"].L() - o["filled"].L(); }
            var held = p["owned_quantity"].L() > 0; p["stage"] = held ? "PAUSED_HELD" : "DONE"; p["recovery_stage"] = held ? "BUY_COLLECTING" : "";
            if (!held) { p["realized_profit_gp"] = p["sale_proceeds_gp"].L() - p["buy_spent"].L(); p["completed_at"] = state["observed_at"]?.DeepClone(); }
            Lifecycle.AddShutdownEntry(p, entry);
        }
        Save();
    }
    public void Recover(JsonObject? observedState = null)
    {
        if (journal == null || !File.Exists(journal)) return; var stored = Storage.ReadJson(journal) as JsonArray ?? throw new InvalidOperationException("Position journal must be an array");
        Positions.Clear(); Positions.AddRange(stored.OfType<JsonObject>().Select(p => p.Copy()));
        foreach (var p in Positions)
        {
            var defaults = NewPosition((int)p["item_id"].L(), p["item_name"].S(), p["quantity"].L(), p["buy_price"].L(), p["sell_price"].L(), p["created_at"].L(), p["account_key"].S());
            defaults["trade_id"] = ""; defaults["started_at"] = null; defaults["last_progress_at"] = null;
            foreach (var pair in defaults) if (!p.ContainsKey(pair.Key)) p[pair.Key] = pair.Value?.DeepClone();
            if (new[] { "DONE", "VOID", "STOPPED", "PAUSED_HELD", "HELD" }.Contains(p["stage"].S())) continue;
            p["recovery_stage"] = p["stage"]?.DeepClone(); var raw = observedState?.Objects("offers").FirstOrDefault(o => Lifecycle.Slot(o) == Lifecycle.Slot(p)); var buying = p["stage"].S() == "WAIT_BUY";
            var account = observedState != null && p["account_key"].S() != "" && p["account_key"].S() == observedState["account_key"].S();
            var matches = account && new[] { "WAIT_BUY", "WAIT_SELL" }.Contains(p["stage"].S()) && Lifecycle.MatchingOffer(raw, Lifecycle.Slot(p), (int)p["item_id"].L(), p[buying ? "quantity" : "owned_quantity"].L(), p[buying ? "buy_price" : "sell_price"].L(), buying);
            var collected = account && p["stage"].S() == "BUY_COLLECTING" && raw?["state"].S() == "EMPTY" && p["owned_quantity"].L() >= 0 && p["owned_quantity"].L() <= p["quantity"].L() && p["buy_spent"].L() >= 0
                && Lifecycle.CountItem(observedState!, (int)p["item_id"].L()) == p["inventory_before"].L() + p["owned_quantity"].L() && !Lifecycle.HasOffer(observedState!, p["item_id"].L());
            if (!matches && !collected) p["stage"] = "STOPPED";
        }
        Save(); if (observedState != null) Reconcile(observedState);
    }
}
