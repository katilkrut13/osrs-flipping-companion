using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public sealed class Ledger : IDisposable
{
    readonly SqliteConnection db;
    public Ledger(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 15, Pooling = false }.ToString()); db.Open();
        Execute("CREATE TABLE IF NOT EXISTS accounts (account TEXT PRIMARY KEY, last_seen INTEGER, continuous_since INTEGER, baseline_verified INTEGER DEFAULT 0)");
        Execute("CREATE TABLE IF NOT EXISTS observed (account TEXT, slot INTEGER, fingerprint TEXT, filled INTEGER, opened_at INTEGER, PRIMARY KEY(account,slot))");
        if (!Query("PRAGMA table_info(observed)").Any(r => Convert.ToString(r[1]) == "last_progress_at")) Execute("ALTER TABLE observed ADD COLUMN last_progress_at INTEGER");
        Execute("CREATE TABLE IF NOT EXISTS buys (account TEXT, item INTEGER, quantity INTEGER, at INTEGER)");
        Execute("CREATE TABLE IF NOT EXISTS limit_metadata (item INTEGER PRIMARY KEY, family TEXT)");
        Execute("CREATE TABLE IF NOT EXISTS events (at INTEGER, account TEXT, kind TEXT, payload TEXT)");
        Execute("CREATE TABLE IF NOT EXISTS position_results (account TEXT, trade_id TEXT, item INTEGER, at INTEGER, payload TEXT, PRIMARY KEY(account,trade_id))");
    }
    SqliteCommand Command(string sql, params object?[] args)
    {
        var command = db.CreateCommand(); command.CommandText = sql;
        for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("$p" + i, args[i] ?? DBNull.Value);
        return command;
    }
    void Execute(string sql, params object?[] args) { using var command = Command(sql, args); command.ExecuteNonQuery(); }
    List<object[]> Query(string sql, params object?[] args)
    {
        using var command = Command(sql, args); using var reader = command.ExecuteReader(); var rows = new List<object[]>();
        while (reader.Read()) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row); } return rows;
    }
    List<JsonObject> Payloads(string sql, params object?[] args) => Query(sql, args).Select(r => JsonNode.Parse(Convert.ToString(r[0])!) as JsonObject ?? throw new InvalidOperationException("Invalid ledger payload")).ToList();
    public void RegisterLimitMetadata(JsonArray metadata)
    {
        // One market scan supplies thousands of rows. Per-row commits blocked
        // the account worker and could exceed the graceful shutdown deadline.
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO limit_metadata(item,family) VALUES ($item,$family) ON CONFLICT(item) DO UPDATE SET family=excluded.family WHERE limit_metadata.family<>excluded.family";
        var item = command.Parameters.Add("$item", SqliteType.Integer); var family = command.Parameters.Add("$family", SqliteType.Text);
        command.Prepare();
        foreach (var entry in metadata.OfType<JsonObject>())
        { item.Value = entry.L("id"); family.Value = entry.S("group"); command.ExecuteNonQuery(); }
        transaction.Commit();
    }
    public void Event(string account, string kind, JsonObject payload, long now) => Execute("INSERT INTO events(at,account,kind,payload) VALUES ($p0,$p1,$p2,$p3)", now, account, kind, payload.ToJsonString());
    public void ConfirmNoPriorBuys(string account, long now) { Execute("UPDATE accounts SET baseline_verified=1 WHERE account=$p0", account); Event(account, "USER_ZERO_BUY_BASELINE", new(), now); }
    public void ConfirmNoUntrackedBuys(string account, long now)
    {
        if (Query("SELECT 1 FROM accounts WHERE account=$p0", account).Count == 0) throw new InvalidOperationException("Observe the account before confirming its history");
        Execute("UPDATE accounts SET baseline_verified=1 WHERE account=$p0", account); Event(account, "USER_NO_UNTRACKED_BUYS", new(), now);
    }
    public void InvalidateBuyHistory(string account, long now, JsonObject evidence)
    {
        Execute("UPDATE accounts SET baseline_verified=0,continuous_since=$p1 WHERE account=$p0", account, now);
        Event(account, "UNOBSERVED_BUY_REQUIRES_REVIEW", evidence, now);
    }
    public long TotalTestLoss(string account) => Payloads("SELECT payload FROM events WHERE account=$p0 AND kind='MARGIN_RESULT'", account).Sum(r => Math.Max(0, r["actual_test_loss_gp"].L()));
    public bool RecordMarginResult(string account, JsonObject result, long now)
    {
        if (result["account_key"].S() != "" && result["account_key"].S() != account) return false;
        if (Query("SELECT 1 FROM events WHERE account=$p0 AND kind='MARGIN_RESULT' AND json_extract(payload,'$.item_id')=$p1 AND json_extract(payload,'$.completed_at')=$p2 LIMIT 1", account, result["item_id"].L(), result["completed_at"].L()).Count != 0) return false;
        Event(account, "MARGIN_RESULT", result, now); return true;
    }
    public void RecordPosition(string account, JsonObject result)
    {
        if (result["account_key"].S() != "" && result["account_key"].S() != account) return;
        if (result["stage"].S() != "DONE" || result["trade_id"].S() == "" || result["started_at"] == null || result["completed_at"] == null || result["realized_profit_gp"] == null) return;
        Execute("INSERT OR IGNORE INTO position_results(account,trade_id,item,at,payload) VALUES ($p0,$p1,$p2,$p3,$p4)", account, result["trade_id"].S(), result["item_id"].L(), result["completed_at"].L(), result.ToJsonString());
    }
    public JsonObject ProfitHistory(string account, long now)
    {
        IEnumerable<JsonObject> Scoped(List<JsonObject> rows) => rows.Where(r => r["account_key"].S() == "" || r["account_key"].S() == account).Select(r => { r["account_key"] = account; return r; });
        return J.O(("closed_positions", J.A(Scoped(Payloads("SELECT payload FROM position_results WHERE account=$p0 AND at<=$p1 ORDER BY at DESC", account, now)))),
            ("margin_results", J.A(Scoped(Payloads("SELECT payload FROM events WHERE account=$p0 AND kind='MARGIN_RESULT' AND at<=$p1 ORDER BY at DESC", account, now)))));
    }
    public List<JsonObject> Outcomes(string account, long now) => Payloads("SELECT payload FROM position_results WHERE account=$p0 AND at>$p1 AND at<=$p2", account, now - 604800, now);
    public JsonObject Performance(string account, long now)
    {
        var items = Outcomes(account, now).GroupBy(p => p["item_id"].L()).Select(group =>
        {
            var records = group.ToList(); var durations = records.Where(p => p["external_collection_evidence"] is null && p["completed_at"].L() > p["started_at"].L() && Math.Max(p["acquired_quantity"].L(), p["owned_quantity"].L()) > 0).Select(p => (double)(p["completed_at"].L() - p["started_at"].L())).ToList();
            return J.O(("item_id", group.Key), ("closed_positions", records.Count), ("realized_profit_gp", records.Sum(p => p["realized_profit_gp"].L())),
                ("median_cycle_seconds", durations.Count != 0 ? J.Median(durations) : null), ("profitable_fraction", (double)records.Count(p => p["realized_profit_gp"].L() > 0) / records.Count),
                ("sample_status", durations.Count >= 5 ? "DESCRIPTIVE" : "INSUFFICIENT"));
        }).ToList();
        var profit = items.Sum(i => i["realized_profit_gp"].L()); var tests = Payloads("SELECT payload FROM events WHERE account=$p0 AND kind='MARGIN_RESULT' AND at>$p1 AND at<=$p2", account, now - 604800, now).Sum(p => Math.Max(0, p["actual_test_loss_gp"].L()));
        return J.O(("account_scope", "current_account_only"), ("window_days", 7), ("items", J.A(items)), ("total_position_profit_gp", profit), ("total_margin_test_loss_gp", tests),
            ("net_after_test_cost_gp", profit - tests), ("lifetime_margin_test_loss_gp", TotalTestLoss(account)), ("note", "Observed results; no automatic risk increase or future profit prediction"));
    }
    public JsonObject DailyRisk(string account, long now)
    {
        var day = now - now % 86400;
        var profit = Payloads("SELECT payload FROM position_results WHERE account=$p0 AND at>=$p1 AND at<=$p2", account, day, now).Sum(p => p["realized_profit_gp"].L());
        var costs = Payloads("SELECT payload FROM events WHERE account=$p0 AND kind='MARGIN_RESULT' AND at>=$p1 AND at<=$p2", account, day, now).Sum(p => Math.Max(0, p["actual_test_loss_gp"].L()));
        return J.O(("utc_day", day), ("position_profit_gp", profit), ("margin_test_cost_gp", costs), ("net_gp", profit - costs));
    }
    public Portfolio Ingest(JsonObject state)
    {
        var account = state["account_key"].S(); var now = state["observed_at"].L();
        if (account == "") throw new InvalidOperationException("Verified account identity required for ledger");
        var record = Query("SELECT last_seen,continuous_since,baseline_verified FROM accounts WHERE account=$p0", account).FirstOrDefault();
        var continuous = record == null ? now : Convert.ToInt64(record[1]); var baseline = record == null ? 0L : Convert.ToInt64(record[2]);
        if (record != null && (now < Convert.ToInt64(record[0]) || now - Convert.ToInt64(record[0]) > 30)) { continuous = now; baseline = 0; }
        Execute("INSERT OR REPLACE INTO accounts(account,last_seen,continuous_since,baseline_verified) VALUES ($p0,$p1,$p2,$p3)", account, now, continuous, baseline);
        var offers = new List<Offer>();
        foreach (var raw in state.Objects("offers"))
        {
            var slot = (int)raw["slot"].L(); var old = Query("SELECT fingerprint,filled,opened_at,last_progress_at FROM observed WHERE account=$p0 AND slot=$p1", account, slot).FirstOrDefault();
            if (raw["state"].S() == "EMPTY") { Execute("INSERT OR REPLACE INTO observed(account,slot,fingerprint,filled,opened_at,last_progress_at) VALUES ($p0,$p1,$p2,$p3,$p4,$p5)", account, slot, "EMPTY", 0, now, now); continue; }
            var id = (int)raw["item_id"].L(); var name = raw["item_name"].S(); var buying = raw["state"].S() is "BUYING" or "BOUGHT" or "CANCELLED_BUY";
            Execute("INSERT OR REPLACE INTO limit_metadata(item,family) VALUES ($p0,$p1)", id, LimitRules.LimitGroup(id, name));
            var fingerprint = $"{id}:{(buying ? "BUY" : "SELL")}:{raw["price"].L()}:{raw["quantity"].L()}";
            var same = old != null && Convert.ToString(old[0]) == fingerprint && raw["filled"].L() >= Convert.ToInt64(old[1]);
            var opened = same ? Convert.ToInt64(old![2]) : now; var previous = same ? Convert.ToInt64(old![1]) : old == null ? raw["filled"].L() : 0;
            var delta = raw["filled"].L() - previous;
            if (delta > 0 && buying) Execute("INSERT INTO buys(account,item,quantity,at) VALUES ($p0,$p1,$p2,$p3)", account, id, delta, now);
            var progress = delta > 0 || !same ? now : old![3] is DBNull ? opened : Convert.ToInt64(old[3]);
            Execute("INSERT OR REPLACE INTO observed(account,slot,fingerprint,filled,opened_at,last_progress_at) VALUES ($p0,$p1,$p2,$p3,$p4,$p5)", account, slot, fingerprint, raw["filled"].L(), opened, progress);
            if (raw["quantity"].L() > 0) offers.Add(new(slot, id, name, buying ? "BUY" : "SELL", raw["price"].L(), raw["quantity"].L(), raw["filled"].L(), opened, progress));
        }
        var counts = Query("SELECT item,SUM(quantity) FROM buys WHERE account=$p0 AND at>$p1 GROUP BY item", account, now - 14400).ToDictionary(r => Convert.ToInt32(r[0]), r => Convert.ToInt64(r[1]));
        var groups = Query("SELECT m.family,SUM(b.quantity) FROM buys b JOIN limit_metadata m ON m.item=b.item WHERE b.account=$p0 AND b.at>$p1 GROUP BY m.family", account, now - 14400).ToDictionary(r => Convert.ToString(r[0])!, r => Convert.ToInt64(r[1]));
        return new Portfolio { Verified = true, ObservedAt = now, FreeCashGp = state["cash_inventory_gp"].L(), Offers = offers, BoughtLast4h = counts,
            BuyLimitsVerified = baseline != 0 || now - continuous >= 14400, SlotCapacity = (int)state["ge_slot_capacity"].L(3),
            Members = state["account_members"] == null ? null : state["account_members"].B(), BoughtByLimitGroup = groups };
    }
    public void Dispose() => db.Dispose();
    public void Close() => Dispose();
}
