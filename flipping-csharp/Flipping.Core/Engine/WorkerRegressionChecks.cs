using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace OsrsFlipping.Core;

public static class WorkerRegressionChecks
{
    public static JsonObject Run(JsonObject? suppliedConfig = null)
    {
        var config = suppliedConfig?.Copy() ?? new JsonObject();
        config["monetary_limits_enabled"] = true;
        config["max_daily_loss_gp"] = 100000;
        config["max_daily_test_cost_gp"] = 10000;
        var checks = new JsonArray();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidDataException("Worker regression: " + name);
            checks.Add(name);
        }
        var staleEntry = J.O(("generated_at_epoch", 800), ("candidates", J.A(J.O(("id", 7), ("strategy_eligible", true)))), ("flagged_items", new JsonArray()));
        var exit = J.O(("generated_at_epoch", 1000), ("exit_quotes", J.A(J.O(("id", 7), ("indicative_sell_gp", 120), ("sell_time", 1000)))));
        var merged = EngineRules.WithOwnedExitQuotes(staleEntry, exit, 1000);
        Check(merged.L("generated_at_epoch") == 800 && merged.Arr("exit_quotes").Count == 1, "exit refresh preserves entry timestamp");
        Check(EngineRules.WithOwnedExitQuotes(staleEntry, exit, 1091).Arr("exit_quotes").Count == 0, "stale exit refresh is rejected");
        Check(EngineRules.RiskReason(J.O(("net_gp", -100001), ("margin_test_cost_gp", 0)), config) == "DAILY_LOSS_LIMIT", "daily loss hold");
        Check(EngineRules.RiskReason(J.O(("net_gp", 0), ("margin_test_cost_gp", 10000)), config) == "DAILY_TEST_COST_LIMIT", "daily test hold");
        Check(EngineRules.RiskReason(J.O(("net_gp", -100001), ("margin_test_cost_gp", 10000)), J.O(("monetary_limits_enabled", false))) == null, "disabled monetary hold");
        Check(EngineRules.Describe("BUY_LIMIT_HISTORY_UNVERIFIED").Contains("Alış geçmişi"), "Turkish cycle reason");
        var folder = Path.Combine(Path.GetTempPath(), "OsrsOperatorBaseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var state = J.O(("schema", 1), ("observed_at", J.Now), ("account_key", "a"), ("logged_in", true), ("ge_open", true),
                ("offers", new JsonArray()), ("inventory", new JsonArray()));
            Storage.WriteJson(Path.Combine(folder, "live-state.json"), state);
            var command = J.O(("id", "confirmed-history"), ("action", "baseline_untracked"), ("account_key", "a"), ("created_at", J.Now));
            Storage.WriteJson(Path.Combine(folder, "operator-command.json"), command);
            Check(OperatorControl.Read(folder, "a") == "baseline_untracked", "explicit untracked-buy confirmation accepted without zeroing known buys");
            Check(OperatorControl.Read(folder, "a") == null, "same history confirmation never replays");
            command["id"] = "other-account"; command["account_key"] = "other";
            Storage.WriteJson(Path.Combine(folder, "operator-command.json"), command);
            Check(OperatorControl.Read(folder, "a") == null && Storage.ReadJson(Path.Combine(folder, "operator-receipt.json")).S("reason") == "ACCOUNT_UNCONFIRMED", "history confirmation rejects another account");
            command["id"] = "expired"; command["account_key"] = "a"; command["created_at"] = J.Now - 40;
            Storage.WriteJson(Path.Combine(folder, "operator-command.json"), command);
            Check(OperatorControl.Read(folder, "a") == null && Storage.ReadJson(Path.Combine(folder, "operator-receipt.json")).S("reason") == "EXPIRED_COMMAND", "history confirmation rejects expired consent message");
            command["id"] = "logged-out"; command["created_at"] = J.Now; state["logged_in"] = false;
            Storage.WriteJson(Path.Combine(folder, "live-state.json"), state); Storage.WriteJson(Path.Combine(folder, "operator-command.json"), command);
            Check(OperatorControl.Read(folder, "a") == null && Storage.ReadJson(Path.Combine(folder, "operator-receipt.json")).S("reason") == "LOGIN_REQUIRED", "history confirmation requires logged-in observation");
        }
        finally { foreach (var file in Directory.GetFiles(folder)) File.Delete(file); Directory.Delete(folder); }
        var metadataFolder = Path.Combine(Path.GetTempPath(), "OsrsMetadataBatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(metadataFolder);
        var metadataPath = Path.Combine(metadataFolder, "ledger.sqlite3");
        try
        {
            using var ledger = new Ledger(metadataPath);
            var rows = J.A(Enumerable.Range(1, 5000).Select(id => (JsonNode?)J.O(("id", id), ("group", "family:" + id))));
            var watch = Stopwatch.StartNew(); ledger.RegisterLimitMetadata(rows); watch.Stop();
            Check(watch.Elapsed < TimeSpan.FromSeconds(10), "full market metadata batch does not block account worker for shutdown deadline");
            ledger.RegisterLimitMetadata(rows);
            ledger.RegisterLimitMetadata(J.A(J.O(("id", 10), ("group", "updated-family"))));
            using var connection = new SqliteConnection("Data Source=" + metadataPath); connection.Open();
            using var query = connection.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM limit_metadata";
            Check(Convert.ToInt64(query.ExecuteScalar()) == 5000, "repeated metadata scan retains one row per item");
            query.CommandText = "SELECT family FROM limit_metadata WHERE item=10";
            Check(Convert.ToString(query.ExecuteScalar()) == "updated-family", "changed buy-limit metadata updates in batch");
        }
        finally { SqliteConnection.ClearAllPools(); foreach (var file in Directory.GetFiles(metadataFolder)) File.Delete(file); Directory.Delete(metadataFolder); }
        return J.O(("passed", checks.Count), ("checks", checks));
    }
}
