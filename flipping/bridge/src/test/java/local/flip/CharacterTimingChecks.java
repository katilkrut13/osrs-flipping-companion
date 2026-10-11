package local.flip;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;

/** Dependency-free checks. Run with javac/java; no RuneLite or input events. */
public final class CharacterTimingChecks {
    private static int checks;

    private static void equal(long expected,long actual) {
        checks++;
        if(expected!=actual) throw new AssertionError("Expected "+expected+", got "+actual);
    }

    private static void rejected(Runnable operation) {
        checks++;
        try { operation.run(); }
        catch(IllegalArgumentException expected) { return; }
        throw new AssertionError("Invalid timing accepted");
    }

    public static void main(String[] args) {
        CharacterTiming scalar=new CharacterTiming("Death rune",90,null);
        equal(90,scalar.scalarDelay());
        equal(0,scalar.overridden()?1:0);
        equal(90,scalar.delayAt(0));
        equal(90,scalar.delayAt(9));
        equal(2600,scalar.scopedDurationMs());
        equal(2780,scalar.robotDurationMs(false));
        equal(2960,scalar.robotDurationMs(true));

        List<String> values=new ArrayList<>(Arrays.asList("50","60","70"));
        CharacterTiming plan=new CharacterTiming("A(a",96,values);
        equal(96,plan.scalarDelay());
        equal(1,plan.overridden()?1:0);
        equal(50,plan.delayAt(0));
        equal(60,plan.delayAt(1));
        equal(70,plan.delayAt(2));
        equal(1160,plan.scopedDurationMs());
        equal(1380,plan.robotDurationMs(false));
        equal(1520,plan.robotDurationMs(true));
        values.set(0,"150");
        equal(50,plan.delayAt(0)); // Validated plans cannot mutate after input begins.

        equal(50,new CharacterTiming("a",50,Collections.singletonList("50")).delayAt(0));
        equal(150,new CharacterTiming("a",150,Collections.singletonList("150")).delayAt(0));
        rejected(() -> new CharacterTiming("a",49,null));
        rejected(() -> new CharacterTiming("a",151,null));
        rejected(() -> new CharacterTiming("ab",90,Collections.singletonList("90")));
        rejected(() -> new CharacterTiming("a",90,Arrays.asList("90","90")));
        rejected(() -> new CharacterTiming("a",90,Collections.emptyList()));
        for(String invalid:Arrays.asList("49","151","50.0","5e1","1e2","NaN","Infinity",
                "1.5","50foo","-50","+50","050","99999999999999999999999",""," 50",null)) {
            rejected(() -> new CharacterTiming("a",90,Collections.singletonList(invalid)));
        }
        rejected(() -> new CharacterTiming("abc",90,Arrays.asList("90","90","150.1")));
        rejected(() -> new CharacterTiming(null,90,null));
        rejected(() -> new CharacterTiming("",90,null));
        rejected(() -> new CharacterTiming("a".repeat(81),90,null));
        rejected(() -> new CharacterTiming("ç",90,null));
        rejected(() -> new CharacterTiming("a\n",90,null));
        equal(24800,new CharacterTiming("a".repeat(80),150,null).scopedDurationMs());
        equal(49100,new CharacterTiming("A".repeat(80),150,null).robotDurationMs(true));
        System.out.println("CharacterTiming: "+checks+" offline checks passed");
    }
}
