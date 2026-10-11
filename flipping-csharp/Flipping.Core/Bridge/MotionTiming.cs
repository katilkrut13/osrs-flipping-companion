using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public sealed class MotionTiming
{
    readonly InputPacing timing;
    readonly bool enabled, keyEnabled;
    JsonObject? lastPlan;
    int[]? lastKeyHoldRange;
    public MotionTiming(JsonObject config, Random? rng = null, bool disabled = false)
    {
        if (config.ContainsKey("pointer_timing_enabled") && !(config["pointer_timing_enabled"] is JsonValue p && p.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            || config.ContainsKey("key_hold_timing_enabled") && !(config["key_hold_timing_enabled"] is JsonValue k && k.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
            throw new ArgumentException("Motion switches must be booleans");
        enabled = config.B("pointer_timing_enabled") && !disabled; keyEnabled = config.B("key_hold_timing_enabled") && !disabled;
        timing = new InputPacing(new JsonObject(), rng);
    }
    static bool Coordinate(JsonNode? node) => node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number && long.TryParse(v.ToJsonString(), out _);
    public JsonObject? PointerPlan(JsonObject state, JsonObject widget)
    {
        if (!enabled || !state.B("pointer_timing_supported")) return null;
        if (!new[] { "x", "y", "width", "height" }.All(k => Coordinate(widget[k])) || widget.L("x") < 0 || widget.L("y") < 0 || widget.L("width") <= 0 || widget.L("height") <= 0)
            throw new ArgumentException("Visible target geometry required before pointer planning");
        // Scroll rows can have bounds larger than the visible canvas. The bridge
        // rejects a motion plan for those rows; retain its ordinary validated click.
        if (state.L("canvas_width") > 0 && widget.L("x") + widget.L("width") > state.L("canvas_width")
            || state.L("canvas_height") > 0 && widget.L("y") + widget.L("height") > state.L("canvas_height")) return null;
        var pointer = state.Obj("canvas_mouse_position");
        double distance = 250;
        if (Coordinate(pointer["x"]) && Coordinate(pointer["y"]) && pointer.L("x") >= 0 && pointer.L("y") >= 0)
        {
            var dx = widget.L("x") + widget.L("width") / 2d - pointer.L("x"); var dy = widget.L("y") + widget.L("height") / 2d - pointer.L("y"); distance = Math.Sqrt(dx * dx + dy * dy);
        }
        var targetSize = Math.Max(4, Math.Min(widget.L("width"), widget.L("height")));
        var duration = Math.Clamp((int)Math.Round(120 + 35 * Math.Log2(1 + distance / targetSize) + timing.Triangular(-12, 12, 0)), 120, 380);
        lastPlan = J.O(("end_x_fraction", widget.L("width") >= 16 ? timing.Triangular(.43, .57, .5) : .5), ("end_y_fraction", widget.L("height") >= 16 ? timing.Triangular(.43, .57, .5) : .5),
            ("duration_ms", duration), ("steps", Math.Clamp((int)Math.Ceiling(duration / 22d), 4, 18)), ("hold_ms", (int)Math.Round(timing.Triangular(45, 90, 65))));
        return lastPlan.Copy();
    }
    public JsonObject KeyHolds(JsonObject state, string text, bool enter = false)
    {
        if (!keyEnabled || !state.B("key_hold_timing_supported")) return new();
        if (text.Length is < 1 or > 80) throw new ArgumentException("Key hold plan requires 1-80 characters");
        var holds = text.Select(_ => (int)Math.Round(timing.Triangular(28, 55, 40))).ToArray(); lastKeyHoldRange = [holds.Min(), holds.Max()];
        var result = J.O(("key_hold_ms", holds)); if (enter) result["enter_hold_ms"] = (int)Math.Round(timing.Triangular(35, 65, 48)); return result;
    }
    public JsonObject Status() => J.O(("pointer_timing_enabled", enabled), ("key_hold_timing_enabled", keyEnabled), ("pointer_duration_range_ms", new[] { 120, 380 }), ("mouse_hold_range_ms", new[] { 45, 90 }),
        ("key_hold_range_ms", new[] { 28, 55 }), ("enter_hold_range_ms", new[] { 35, 65 }), ("last_pointer_plan", lastPlan), ("last_key_hold_range_ms", lastKeyHoldRange));
}
