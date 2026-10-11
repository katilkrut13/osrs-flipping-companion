using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public sealed record UserHandoffDecision(bool Holding, bool ResumeReady, string? Reason,
    long ActivitySequence, double QuietRemainingSeconds);

public static class PhysicalActivityRules
{
    public static bool Keyboard(bool runeLiteForeground, uint flags)
        => runeLiteForeground && (flags & 0x12) == 0;
    public static bool Mouse(bool pointerOverRuneLite, bool runeLiteForeground, bool movement, uint flags)
        => !movement && (flags & 0x03) == 0 && pointerOverRuneLite;
}

/// <summary>
/// Coordinates automatic handoff after physical user activity. It retains only
/// times, a counter, and the account/session identity needed for safe resumption.
/// Reconciliation must complete before the caller acknowledges a ready resume.
/// </summary>
public sealed class AutomaticUserHandoff
{
    readonly object sync = new();
    readonly long quietMilliseconds, stableGapMilliseconds;
    readonly double maximumSnapshotAgeSeconds;
    bool holding;
    long activitySequence, lastActivityMilliseconds, lastStableMilliseconds, readySequence = -1;
    double lastActivityEpoch, lastStableObservedAt = double.NegativeInfinity;
    int stableSnapshots;
    string? expectedAccount, expectedSession;

    public AutomaticUserHandoff(int quietSeconds = 8, int stableGapMilliseconds = 1000,
        double maximumSnapshotAgeSeconds = 5)
    {
        if (quietSeconds is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(quietSeconds));
        if (stableGapMilliseconds is < 100 or > 10000) throw new ArgumentOutOfRangeException(nameof(stableGapMilliseconds));
        if (!double.IsFinite(maximumSnapshotAgeSeconds) || maximumSnapshotAgeSeconds is <= 0 or > 30)
            throw new ArgumentOutOfRangeException(nameof(maximumSnapshotAgeSeconds));
        quietMilliseconds = quietSeconds * 1000L;
        this.stableGapMilliseconds = stableGapMilliseconds;
        this.maximumSnapshotAgeSeconds = maximumSnapshotAgeSeconds;
    }

    public bool IsHolding { get { lock (sync) return holding; } }
    public long ActivitySequence { get { lock (sync) return activitySequence; } }

    public void RecordActivity(long monotonicMilliseconds, double epochSeconds)
    {
        if (!double.IsFinite(epochSeconds)) throw new ArgumentOutOfRangeException(nameof(epochSeconds));
        lock (sync)
        {
            holding = true;
            activitySequence++;
            lastActivityMilliseconds = monotonicMilliseconds;
            lastActivityEpoch = epochSeconds;
            ResetReadiness();
        }
    }

    public UserHandoffDecision Evaluate(long monotonicMilliseconds, double epochSeconds,
        JsonObject? snapshot, bool cycleRequested)
    {
        lock (sync)
        {
            if (!holding)
            {
                if (Fresh(snapshot, epochSeconds) && snapshot!.B("logged_in")) RememberIdentity(snapshot!);
                return Decision(false, null, 0);
            }
            if (!cycleRequested) return Block("USER_ACTIVITY_CYCLE_STOPPED");
            var elapsed = monotonicMilliseconds - lastActivityMilliseconds;
            if (elapsed < quietMilliseconds)
                return Block("USER_ACTIVITY_ACTIVE", Math.Max(0, (quietMilliseconds - elapsed) / 1000d));
            if (!Fresh(snapshot, epochSeconds) || snapshot!.D("observed_at") <= lastActivityEpoch)
                return Block("USER_ACTIVITY_FRESH_STATE_REQUIRED");
            if (!snapshot.B("logged_in") || string.IsNullOrEmpty(snapshot.S("account_key")) || string.IsNullOrEmpty(snapshot.S("session")))
                return Block("USER_ACTIVITY_LOGIN_REQUIRED");
            if ((!string.IsNullOrEmpty(expectedAccount) && expectedAccount != snapshot.S("account_key"))
                || (!string.IsNullOrEmpty(expectedSession) && expectedSession != snapshot.S("session")))
                return Block("USER_ACTIVITY_IDENTITY_REQUIRES_REVIEW");
            if (!snapshot.B("ge_open")) return Block("USER_ACTIVITY_RETURN_TO_GE");
            if (snapshot.B("input_busy")) return Block("USER_ACTIVITY_INPUT_BUSY");
            RememberIdentity(snapshot!);
            var observedAt = snapshot.D("observed_at");
            if (stableSnapshots == 0 || (observedAt > lastStableObservedAt
                && monotonicMilliseconds - lastStableMilliseconds >= stableGapMilliseconds))
            {
                stableSnapshots++;
                lastStableObservedAt = observedAt;
                lastStableMilliseconds = monotonicMilliseconds;
            }
            if (stableSnapshots < 2) return Decision(false, "USER_ACTIVITY_VERIFYING_STATE", 0);
            readySequence = activitySequence;
            return Decision(true, "USER_ACTIVITY_RECONCILE_BEFORE_RESUME", 0);
        }
    }

    public bool AcknowledgeResume(long expectedActivitySequence)
    {
        lock (sync)
        {
            if (!holding || readySequence != expectedActivitySequence || activitySequence != expectedActivitySequence) return false;
            holding = false;
            ResetReadiness();
            return true;
        }
    }

    bool Fresh(JsonObject? snapshot, double now)
    {
        if (snapshot is null || !double.IsFinite(now) || snapshot["observed_at"] is null) return false;
        var observed = snapshot.D("observed_at");
        return double.IsFinite(observed) && observed > 0 && now - observed >= -1
            && now - observed <= maximumSnapshotAgeSeconds;
    }

    void RememberIdentity(JsonObject snapshot)
    {
        expectedAccount = snapshot.S("account_key"); expectedSession = snapshot.S("session");
    }

    UserHandoffDecision Block(string reason, double remaining = 0)
    {
        ResetReadiness(); return Decision(false, reason, remaining);
    }
    UserHandoffDecision Decision(bool ready, string? reason, double remaining)
        => new(holding, ready, reason, activitySequence, remaining);
    void ResetReadiness()
    {
        stableSnapshots = 0; readySequence = -1; lastStableObservedAt = double.NegativeInfinity; lastStableMilliseconds = 0;
    }
}
