using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public static class UserHandoffRegressionChecks
{
    public static JsonObject Run()
    {
        var checks = new JsonArray();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidDataException("User handoff regression: " + name);
            checks.Add(name);
        }
        JsonObject State(double observed, string account = "account-a", string session = "session-a")
            => J.O(("observed_at", observed), ("account_key", account), ("session", session),
                ("logged_in", true), ("ge_open", true), ("input_busy", false));
        Check(PhysicalActivityRules.Keyboard(true, 0), "physical keyboard input directed at RuneLite is detected");
        Check(!PhysicalActivityRules.Keyboard(false, 0), "typing in another application does not pause trading");
        Check(!PhysicalActivityRules.Keyboard(true, 0x10) && !PhysicalActivityRules.Keyboard(true, 0x02), "injected keyboard events are ignored");
        Check(!PhysicalActivityRules.Mouse(true, true, true, 0), "pointer movement over focused RuneLite does not interrupt trading");
        Check(!PhysicalActivityRules.Mouse(true, false, true, 0), "passing over inactive RuneLite does not pause trading");
        Check(PhysicalActivityRules.Mouse(true, false, false, 0), "first physical click activating RuneLite is detected");
        Check(!PhysicalActivityRules.Mouse(false, true, false, 0), "mouse activity in another application is ignored");
        Check(!PhysicalActivityRules.Mouse(true, true, false, 0x01) && !PhysicalActivityRules.Mouse(true, true, true, 0x02),
            "injected pointer events are ignored");
        AutomaticUserHandoff Active()
        {
            var handoff = new AutomaticUserHandoff();
            handoff.Evaluate(0, 1000, State(1000), true);
            handoff.RecordActivity(1000, 1001);
            return handoff;
        }

        var h = new AutomaticUserHandoff();
        Check(!h.IsHolding && !h.Evaluate(0, 1000, State(1000), true).Holding, "no physical activity leaves cycle unchanged");
        Check(!h.AcknowledgeResume(0), "resume acknowledgement without handoff is rejected");
        h.RecordActivity(1000, 1001);
        Check(h.IsHolding && h.ActivitySequence == 1, "physical activity blocks inputs immediately");
        var d = h.Evaluate(1001, 1001.001, State(1001), true);
        Check(d.Holding && !d.ResumeReady && d.Reason == "USER_ACTIVITY_ACTIVE" && d.QuietRemainingSeconds > 7,
            "active interaction waits without automatic cleanup");
        Check(!h.AcknowledgeResume(1), "quiet and reconciliation cannot be bypassed by acknowledgement");
        Check(h.Evaluate(8999, 1008.999, State(1008), true).Reason == "USER_ACTIVITY_ACTIVE", "quiet interval is measured with monotonic time");
        d = h.Evaluate(9000, 1009, State(1009), true);
        Check(!d.ResumeReady && d.Reason == "USER_ACTIVITY_VERIFYING_STATE", "first fresh state starts resume verification");
        Check(!h.Evaluate(9500, 1009.5, State(1009.5), true).ResumeReady, "verification requires minimum stable interval");
        Check(!h.Evaluate(10000, 1010, State(1009), true).ResumeReady, "repeated snapshot cannot establish stability");
        d = h.Evaluate(10000, 1010, State(1010), true);
        Check(d.Holding && d.ResumeReady && d.ActivitySequence == 1, "two fresh distinct states permit reconciliation");
        Check(!h.AcknowledgeResume(0), "outdated activity sequence cannot release input guard");
        Check(h.AcknowledgeResume(1) && !h.IsHolding, "acknowledgement releases guard after reconciliation");
        Check(!h.AcknowledgeResume(1), "resume acknowledgement is idempotent");

        h = Active();
        Check(h.Evaluate(9000, 1009, State(1009), false).Reason == "USER_ACTIVITY_CYCLE_STOPPED" && h.IsHolding,
            "intentional stop is never converted into an automatic start");
        Check(h.Evaluate(9000, 1009, null, true).Reason == "USER_ACTIVITY_FRESH_STATE_REQUIRED", "missing connection keeps inputs blocked");
        Check(h.Evaluate(9000, 1009, State(1003), true).Reason == "USER_ACTIVITY_FRESH_STATE_REQUIRED", "stale snapshot keeps inputs blocked");
        Check(h.Evaluate(9000, 1009, State(1011), true).Reason == "USER_ACTIVITY_FRESH_STATE_REQUIRED", "future snapshot is rejected");
        Check(h.Evaluate(9000, 1001, State(1001), true).Reason == "USER_ACTIVITY_FRESH_STATE_REQUIRED", "state before latest activity cannot release guard");
        var unavailable = State(1009); unavailable["logged_in"] = false;
        Check(h.Evaluate(9000, 1009, unavailable, true).Reason == "USER_ACTIVITY_LOGIN_REQUIRED", "logged out player is left to user");
        Check(h.Evaluate(9000, 1009, State(1009, "account-b"), true).Reason == "USER_ACTIVITY_IDENTITY_REQUIRES_REVIEW", "account change cannot auto resume");
        Check(h.Evaluate(9000, 1009, State(1009, session: "session-b"), true).Reason == "USER_ACTIVITY_IDENTITY_REQUIRES_REVIEW", "bridge session change cannot auto resume");
        unavailable = State(1009); unavailable["ge_open"] = false;
        Check(h.Evaluate(9000, 1009, unavailable, true).Reason == "USER_ACTIVITY_RETURN_TO_GE", "manual play outside GE is not interrupted by navigation");
        unavailable = State(1009); unavailable["input_busy"] = true;
        Check(h.Evaluate(9000, 1009, unavailable, true).Reason == "USER_ACTIVITY_INPUT_BUSY", "unfinished input is not raced by resume");
        h.Evaluate(9000, 1009, State(1009), true);
        d = h.Evaluate(10000, 1010, State(1010), true);
        Check(d.ResumeReady, "valid state recovers after transient unavailable UI");
        h.RecordActivity(10001, 1010.001);
        Check(!h.AcknowledgeResume(d.ActivitySequence) && h.IsHolding && h.ActivitySequence == 2,
            "new physical activity during reconciliation invalidates resume token");
        Check(h.Evaluate(11000, 1011, State(1011), true).Reason == "USER_ACTIVITY_ACTIVE", "each interaction extends quiet period");

        h = Active();
        h.Evaluate(9000, 1009, State(1009), true);
        unavailable = State(1009.5); unavailable["ge_open"] = false;
        h.Evaluate(9500, 1009.5, unavailable, true);
        Check(!h.Evaluate(10000, 1010, State(1010), true).ResumeReady, "invalid intermediate state resets stability proof");

        h = new AutomaticUserHandoff(); h.RecordActivity(1000, 1001);
        h.Evaluate(9000, 1009, State(1009), true);
        Check(h.Evaluate(10000, 1010, State(1010), true).ResumeReady, "handoff can establish identity when first activity preceded connection");
        return J.O(("passed", checks.Count), ("checks", checks));
    }
}
