using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public sealed class InputPacing
{
    public Random Rng { get; }
    public double ClickBase { get; }
    public int KeyBase { get; }
    readonly double clickJitter, breakMin, breakMax, numberSpeed, textStartMin, textStartMax, tempoVariation, tempoDrift;
    readonly int keyJitter, breakEveryMin, breakEveryMax, characterJitter, wordPause, wordPauseJitter, enterPauseMin, enterPauseMax, phaseBreakMin, phaseBreakMax;
    readonly JsonObject contextPauses, phasePauses;
    readonly Queue<string> phaseTokens = new();
    int completedInputs, completedPhases, microbreaksTaken, phaseBreaksTaken;
    int? breakAfter, phaseBreakAfter;
    bool pendingPhaseBreak;
    double tempoMultiplier = 1;
    double? lastClickPause, lastTypingStartPause, lastPhasePause;
    int? lastTypingKeyDelay, lastEnterPause;
    string? lastClickContext, lastTypingContext, lastPhase;
    int[]? lastCharacterRange;

    public InputPacing(JsonObject config, Random? rng = null, double? clickOverride = null)
    {
        Rng = rng ?? new Random();
        ClickBase = clickOverride ?? Number(config, "click_pause_seconds");
        clickJitter = clickOverride.HasValue ? 0 : Number(config, "click_pause_jitter_seconds");
        KeyBase = Integer(config, "typing_key_delay_ms", 90); keyJitter = Integer(config, "typing_key_jitter_ms");
        breakEveryMin = clickOverride.HasValue ? 0 : Integer(config, "input_microbreak_every_min");
        breakEveryMax = clickOverride.HasValue ? 0 : Integer(config, "input_microbreak_every_max", breakEveryMin);
        breakMin = Number(config, "input_microbreak_min_seconds"); breakMax = Number(config, "input_microbreak_max_seconds", breakMin);
        characterJitter = Integer(config, "typing_character_jitter_ms"); wordPause = Integer(config, "typing_word_pause_ms"); wordPauseJitter = Integer(config, "typing_word_pause_jitter_ms");
        numberSpeed = Number(config, "typing_number_delay_multiplier", 1); textStartMin = Number(config, "typing_start_pause_min_seconds"); textStartMax = Number(config, "typing_start_pause_max_seconds", textStartMin);
        enterPauseMin = Integer(config, "typing_enter_pause_min_ms"); enterPauseMax = Integer(config, "typing_enter_pause_max_ms", enterPauseMin);
        tempoVariation = clickOverride.HasValue ? 0 : Number(config, "input_tempo_variation_fraction"); tempoDrift = clickOverride.HasValue ? 0 : Number(config, "input_tempo_drift_fraction");
        contextPauses = clickOverride.HasValue || !config.ContainsKey("click_context_pause_seconds") ? new() : config["click_context_pause_seconds"] as JsonObject ?? throw new ArgumentException("Context pauses must be an object");
        phasePauses = clickOverride.HasValue || !config.ContainsKey("phase_pause_seconds") ? new() : config["phase_pause_seconds"] as JsonObject ?? throw new ArgumentException("Phase pauses must be an object");
        phaseBreakMin = clickOverride.HasValue ? 0 : Integer(config, "phase_microbreak_every_min"); phaseBreakMax = clickOverride.HasValue ? 0 : Integer(config, "phase_microbreak_every_max", phaseBreakMin);
        Require(double.IsFinite(ClickBase) && ClickBase is >= 0 and <= 5, "Click pause must be between 0 and 5 seconds");
        Require(double.IsFinite(clickJitter) && clickJitter is >= 0 and <= .05 && clickJitter <= ClickBase && ClickBase + clickJitter <= 5, "Click jitter must be bounded to at most 50 ms within the click range");
        Require(KeyBase is >= 50 and <= 150 && keyJitter is >= 0 and <= 15 && KeyBase - keyJitter >= 50 && KeyBase + keyJitter <= 150, "Typing delays must stay between 50 and 150 ms");
        Require(breakEveryMin == 0 ? breakEveryMax == 0 : breakEveryMin >= 10 && breakEveryMax >= breakEveryMin && breakEveryMax <= 1000, "Microbreak cadence must be disabled or between 10 and 1000 completed inputs");
        Require(double.IsFinite(breakMin) && double.IsFinite(breakMax) && breakMin >= 0 && breakMax >= breakMin && breakMax <= 1, "Microbreak must be between 0 and 1 second");
        Require(characterJitter is >= 0 and <= 15 && wordPause is >= 0 and <= 25 && wordPauseJitter >= 0 && wordPauseJitter <= Math.Min(10, wordPause) && wordPause + wordPauseJitter <= 25, "Character and word timing outside bounds");
        Require(double.IsFinite(numberSpeed) && numberSpeed is >= .85 and <= 1, "Numeric typing multiplier must be between 0.85 and 1");
        Require(double.IsFinite(textStartMin) && double.IsFinite(textStartMax) && textStartMin >= 0 && textStartMax >= textStartMin && textStartMax <= .3, "Typing start pause must be between 0 and 300 ms");
        Require(enterPauseMin >= 0 && enterPauseMax >= enterPauseMin && enterPauseMax <= 300, "Enter pause must be between 0 and 300 ms");
        Require(double.IsFinite(tempoVariation) && double.IsFinite(tempoDrift) && tempoVariation is >= 0 and <= .1 && tempoDrift >= 0 && tempoDrift <= tempoVariation, "Tempo variation must be at most 10% with smaller bounded steps");
        foreach (var entry in contextPauses)
        {
            Require(new[] { "confirm", "select", "number", "collect", "cancel" }.Contains(entry.Key) && IsNumber(entry.Value, out var n) && n is >= 0 and <= .25, "Context pauses require known actions and 0-250 ms additions");
        }
        foreach (var entry in phasePauses)
        {
            Require(new[] { "offer_prepare", "price_review", "collection_prepare" }.Contains(entry.Key) && entry.Value is JsonArray a && a.Count == 2 && IsNumber(a[0], out var lo) && IsNumber(a[1], out var hi) && lo >= 0 && hi >= lo && hi <= .5, "Phase pauses require known phases and bounded 0-500 ms ranges");
        }
        Require(phaseBreakMin == 0 ? phaseBreakMax == 0 : phaseBreakMin >= 2 && phaseBreakMax >= phaseBreakMin && phaseBreakMax <= 100, "Phase break cadence must be disabled or 2-100 verified phases");
        breakAfter = NextBreak(); phaseBreakAfter = NextPhaseBreak();
    }
    public static bool IsNumber(JsonNode? value, out double number)
    {
        number = 0;
        return value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number && double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    }
    static double Number(JsonObject config, string key, double fallback = 0)
    {
        if (!config.ContainsKey(key)) return fallback;
        if (!IsNumber(config[key], out var value)) throw new ArgumentException(key + " must be a finite number");
        return value;
    }
    static int Integer(JsonObject config, string key, int fallback = 0)
    {
        if (!config.ContainsKey(key)) return fallback;
        if (!config[key].IsInteger() || config.L(key) < int.MinValue || config.L(key) > int.MaxValue) throw new ArgumentException("Input pacing integer settings must be integers");
        return (int)config.L(key);
    }
    static void Require(bool condition, string message) { if (!condition) throw new ArgumentException(message); }
    public double Triangular(double low, double high, double mode)
    {
        if (high <= low) return low;
        var u = Rng.NextDouble(); var split = (mode - low) / (high - low);
        return u < split ? low + Math.Sqrt(u * (high - low) * (mode - low)) : high - Math.Sqrt((1 - u) * (high - low) * (high - mode));
    }
    int? NextBreak() => breakEveryMin > 0 ? Rng.Next(breakEveryMin, breakEveryMax + 1) : null;
    int? NextPhaseBreak() => phaseBreakMin > 0 ? Rng.Next(phaseBreakMin, phaseBreakMax + 1) : null;
    double ConsumePhaseBreak()
    {
        if (!pendingPhaseBreak) return 0;
        pendingPhaseBreak = false; completedPhases = 0; phaseBreakAfter = NextPhaseBreak(); phaseBreaksTaken++;
        return breakMin + Rng.NextDouble() * (breakMax - breakMin);
    }
    public double PhaseDelaySeconds(string phase)
    {
        Require(new[] { "offer_prepare", "price_review", "collection_prepare" }.Contains(phase), "Unknown input phase");
        var range = phasePauses[phase] as JsonArray;
        var low = range?[0].D() ?? 0; var high = range?[1].D() ?? 0;
        lastPhase = phase; lastPhasePause = Triangular(low, high, (low + high) / 2); return lastPhasePause.Value;
    }
    public bool PhaseCompleted(string phase, string token)
    {
        Require(new[] { "offer_submitted", "collection_completed" }.Contains(phase) && !string.IsNullOrEmpty(token), "Verified phase and unique outcome token required");
        var key = phase + ":" + token; if (phaseTokens.Contains(key)) return false;
        phaseTokens.Enqueue(key); if (phaseTokens.Count > 512) phaseTokens.Dequeue();
        completedPhases++; if (phaseBreakAfter.HasValue && completedPhases >= phaseBreakAfter.Value) pendingPhaseBreak = true; return true;
    }
    public double ClickDelaySeconds(string? context = null)
    {
        var delay = Math.Min(5, Triangular(ClickBase - clickJitter, ClickBase + clickJitter, ClickBase) * tempoMultiplier);
        var extra = context is null ? 0 : contextPauses.D(context); if (extra > 0) delay += Triangular(extra * .75, extra * 1.25, extra);
        if (breakAfter.HasValue && completedInputs >= breakAfter.Value)
        {
            delay += breakMin + Rng.NextDouble() * (breakMax - breakMin); completedInputs = 0; breakAfter = NextBreak(); microbreaksTaken++;
        }
        delay += ConsumePhaseBreak(); lastClickPause = delay; lastClickContext = context; return delay;
    }
    public int TypingDelayMs(string? text = null)
    {
        var sampled = keyJitter > 0 ? Rng.Next(KeyBase - keyJitter, KeyBase + keyJitter + 1) : KeyBase;
        var number = !string.IsNullOrEmpty(text) && text.All(c => c is >= '0' and <= '9');
        lastTypingContext = number ? "NUMBER" : "TEXT";
        lastTypingKeyDelay = Math.Clamp((int)Math.Round(sampled * tempoMultiplier * (number ? numberSpeed : 1)), 50, 150);
        return lastTypingKeyDelay.Value;
    }
    public double TypingStartDelaySeconds() { lastTypingStartPause = Triangular(textStartMin, textStartMax, (textStartMin + textStartMax) / 2) + ConsumePhaseBreak(); return lastTypingStartPause.Value; }
    public int EnterDelayMs() { lastEnterPause = (int)Math.Round(Triangular(enterPauseMin, enterPauseMax, (enterPauseMin + enterPauseMax) / 2d)); return lastEnterPause.Value; }
    public int[] TypingDelaysMs(string text, int baseDelayMs)
    {
        Require(text.Length is >= 1 and <= 80 && baseDelayMs is >= 50 and <= 150, "Typing plan requires 1-80 characters and 50-150 ms base");
        var delays = new List<int>(); double offset = 0;
        foreach (var ch in text)
        {
            if (characterJitter > 0) offset = offset * .5 + Triangular(-characterJitter, characterJitter, 0) * .5;
            var extra = ch == ' ' ? (int)Math.Round(Triangular(wordPause - wordPauseJitter, wordPause + wordPauseJitter, wordPause)) : 0;
            delays.Add(Math.Clamp((int)Math.Round(baseDelayMs + offset + extra), 50, 150));
        }
        lastCharacterRange = [delays.Min(), delays.Max()]; return delays.ToArray();
    }
    public void InputCompleted()
    {
        completedInputs++;
        if (tempoDrift > 0) tempoMultiplier = Math.Clamp(tempoMultiplier + Triangular(-tempoDrift, tempoDrift, 0), 1 - tempoVariation, 1 + tempoVariation);
    }
    public JsonObject Status() => J.O(
        ("click_pause_range_seconds", new[] { (ClickBase - clickJitter) * (1 - tempoVariation), Math.Min(5, (ClickBase + clickJitter) * (1 + tempoVariation)) }),
        ("typing_delay_range_ms", new[] { Math.Max(50, (int)Math.Round((KeyBase - keyJitter) * (1 - tempoVariation))), Math.Min(150, (int)Math.Round((KeyBase + keyJitter) * (1 + tempoVariation))) }),
        ("click_context_pause_seconds", contextPauses), ("last_click_context", lastClickContext), ("tempo_multiplier", tempoMultiplier), ("typing_character_jitter_ms", characterJitter),
        ("typing_word_pause_ms", wordPause), ("typing_word_pause_jitter_ms", wordPauseJitter), ("typing_number_delay_multiplier", numberSpeed), ("typing_start_pause_range_seconds", new[] { textStartMin, textStartMax }),
        ("typing_enter_pause_range_ms", new[] { enterPauseMin, enterPauseMax }), ("last_typing_context", lastTypingContext), ("last_typing_start_pause_seconds", lastTypingStartPause),
        ("last_enter_pause_ms", lastEnterPause), ("last_character_delay_range_ms", lastCharacterRange), ("phase_pause_seconds", phasePauses), ("phase_microbreak_every_completed_phases", new[] { phaseBreakMin, phaseBreakMax }),
        ("phase_microbreak_pending", pendingPhaseBreak), ("phase_microbreaks_taken", phaseBreaksTaken), ("last_phase", lastPhase), ("last_phase_pause_seconds", lastPhasePause),
        ("microbreak_range_seconds", new[] { breakMin, breakMax }), ("microbreak_every_inputs", new[] { breakEveryMin, breakEveryMax }), ("last_click_pause_seconds", lastClickPause),
        ("last_typing_key_delay_ms", lastTypingKeyDelay), ("microbreaks_taken", microbreaksTaken));
}
