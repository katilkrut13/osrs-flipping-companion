package local.flip;

import java.util.List;

/** Validated typing timing only; never changes the requested text or key count. */
final class CharacterTiming {
    private final String text;
    private final int scalarDelay;
    private final int[] delays;
    private final boolean overridden;

    CharacterTiming(String text, int scalarDelay, List<String> overrideTokens) {
        if (text == null || !text.matches("[A-Za-z0-9 ()'\\-]{1,80}")) {
            throw new IllegalArgumentException("Unsupported text");
        }
        checkRange(scalarDelay);
        if (overrideTokens != null && overrideTokens.size() != text.length()) {
            throw new IllegalArgumentException("Character timing length mismatch");
        }
        this.text = text;
        this.scalarDelay = scalarDelay;
        this.overridden = overrideTokens != null;
        this.delays = new int[text.length()];
        for (int i = 0; i < delays.length; i++) {
            String token = overridden ? overrideTokens.get(i) : null;
            if (overridden && (token == null || !token.matches("0|[1-9][0-9]*"))) {
                throw new IllegalArgumentException("Character timing must be an integer");
            }
            int value = overridden ? Integer.parseInt(token) : scalarDelay;
            checkRange(value);
            delays[i] = value;
        }
    }

    private static void checkRange(int delay) {
        if (delay < 50 || delay > 150) {
            throw new IllegalArgumentException("Typing delay must be between 50 and 150 ms");
        }
    }

    int scalarDelay() { return scalarDelay; }
    int delayAt(int index) { return delays[index]; }
    boolean overridden() { return overridden; }

    long scopedDurationMs() {
        return scopedDurationMs(80,0);
    }

    long scopedDurationMs(int clearKeyCount,int enterPauseMs) {
        long duration = 10L*clearKeyCount+enterPauseMs;
        for (int delay : delays) duration += 2L * delay;
        return duration;
    }

    long robotDurationMs(boolean enter) {
        return robotDurationMs(enter,80,0);
    }

    long robotDurationMs(boolean enter,int clearKeyCount,int enterPauseMs) {
        long duration = 10L*clearKeyCount+enterPauseMs;
        for (int i = 0; i < delays.length; i++) {
            char ch = text.charAt(i);
            duration += (Character.isUpperCase(ch) || ch == '(' || ch == ')' ? 4L : 2L) * delays[i];
        }
        if (enter) duration += 2L * delays[delays.length - 1];
        return duration;
    }
}
