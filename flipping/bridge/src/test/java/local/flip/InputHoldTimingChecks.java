package local.flip;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.atomic.AtomicInteger;

/** Key-plan and guaranteed-release checks using fake actions/sleeps only. */
public final class InputHoldTimingChecks {
    private static int checks;
    private static void equal(long expected,long actual) {checks++;if(expected!=actual) throw new AssertionError(expected+" != "+actual);}
    private static void rejected(Runnable operation) {
        checks++;try {operation.run();}catch(IllegalArgumentException expected){return;}
        throw new AssertionError("Invalid hold timing accepted");
    }
    public static void main(String[] args) throws Exception {
        InputHoldTiming legacy=new InputHoldTiming(2,null,null,false);
        equal(-1,legacy.keyHoldAt(0));equal(-1,legacy.enterHoldMs());equal(0,legacy.totalHoldMs());
        List<String> tokens=new ArrayList<>(Arrays.asList("20","65"));
        InputHoldTiming plan=new InputHoldTiming(2,tokens,"75",true);
        equal(20,plan.keyHoldAt(0));equal(65,plan.keyHoldAt(1));equal(75,plan.enterHoldMs());equal(160,plan.totalHoldMs());
        tokens.set(0,"65");equal(20,plan.keyHoldAt(0));
        equal(1675,new InputHoldTiming(80,Collections.nCopies(80,"20"),"75",true).totalHoldMs());
        rejected(() -> new InputHoldTiming(0,null,null,false));
        rejected(() -> new InputHoldTiming(81,null,null,false));
        rejected(() -> new InputHoldTiming(2,Collections.singletonList("40"),null,false));
        rejected(() -> new InputHoldTiming(2,Collections.emptyList(),null,false));
        for(String invalid:Arrays.asList(null,"19","66","20.0","2e1","020","-20","999999999999999")) {
            rejected(() -> new InputHoldTiming(1,Collections.singletonList(invalid),null,false));
        }
        for(String invalid:Arrays.asList("24","76","25.0","2.5e1","025","-25","999999999999999")) {
            rejected(() -> new InputHoldTiming(1,null,invalid,true));
        }
        rejected(() -> new InputHoldTiming(1,null,"25",false));

        List<String> events=new ArrayList<>();AtomicInteger slept=new AtomicInteger();
        InputHoldTiming.held(() -> events.add("press"),() -> events.add("release"),65,() -> {},millis -> slept.addAndGet((int)millis));
        equal(65,slept.get());equal(2,events.size());checks++;if(!events.equals(Arrays.asList("press","release"))) throw new AssertionError("Wrong key sequence");
        events.clear();slept.set(0);
        try {
            InputHoldTiming.held(() -> events.add("press"),() -> events.add("release"),110,() -> {
                if(slept.get()>0) throw new InterruptedException("STOP");
            },millis -> slept.addAndGet((int)millis));
            throw new AssertionError("STOP ignored");
        } catch(InterruptedException expected) {equal(25,slept.get());equal(2,events.size());}
        events.clear();
        try {
            InputHoldTiming.held(() -> events.add("press"),() -> events.add("release"),65,() -> {throw new InterruptedException("STOP");},millis -> {});
            throw new AssertionError("Prepress STOP ignored");
        } catch(InterruptedException expected) {equal(0,events.size());}
        events.clear();
        try {
            InputHoldTiming.held(() -> {events.add("partial-press");throw new IllegalStateException("Listener failed");},() -> events.add("release"),65,() -> {},millis -> {});
            throw new AssertionError("Press failure ignored");
        } catch(IllegalStateException expected) {equal(2,events.size());checks++;if(!events.get(1).equals("release")) throw new AssertionError("Missing release");}
        events.clear();
        try {InputHoldTiming.held(() -> events.add("press"),() -> events.add("release"),-1,() -> {},millis -> {});throw new AssertionError("Invalid hold accepted");}
        catch(IllegalArgumentException expected) {equal(0,events.size());}
        System.out.println("InputHoldTiming: "+checks+" offline checks passed");
    }
}
