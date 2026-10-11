package local.flip;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.concurrent.atomic.AtomicInteger;

/** Dependency-free edit-plan checks, using a fake sleeper and no key events. */
public final class TextEditTimingChecks {
    private static int checks;

    private static void equal(long expected,long actual) {
        checks++;
        if(expected!=actual) throw new AssertionError("Expected "+expected+", got "+actual);
    }

    private static void rejected(Runnable operation,Class<? extends RuntimeException> expectedType) {
        checks++;
        try { operation.run(); }
        catch(RuntimeException expected) {
            if(expectedType.isInstance(expected)) return;
            throw new AssertionError("Wrong rejection type",expected);
        }
        throw new AssertionError("Invalid timing accepted");
    }

    public static void main(String[] args) throws Exception {
        equal(-1,TextEditTiming.bufferLength(null));
        equal(0,TextEditTiming.bufferLength(""));
        equal(1,TextEditTiming.bufferLength("0"));
        equal(80,TextEditTiming.bufferLength("a".repeat(80)));
        equal(81,TextEditTiming.bufferLength("a".repeat(81)));
        TextEditTiming legacy=new TextEditTiming(null,null,false,-1);
        equal(80,legacy.clearKeyCount());equal(0,legacy.enterPauseMs());
        equal(0,new TextEditTiming("0",null,false,0).clearKeyCount());
        equal(80,new TextEditTiming("80",null,false,80).clearKeyCount());
        equal(3,new TextEditTiming("3","300",true,3).clearKeyCount());
        for(int length:new int[]{-1,0,2,4,81}) {
            rejected(() -> new TextEditTiming("3",null,false,length),TextEditTiming.BufferLengthChanged.class);
        }
        rejected(() -> new TextEditTiming("0",null,false,-1),TextEditTiming.BufferLengthChanged.class);
        rejected(() -> new TextEditTiming(null,"0",false,0),IllegalArgumentException.class);
        rejected(() -> new TextEditTiming(null,"300",false,0),IllegalArgumentException.class);
        for(String invalid:Arrays.asList("-1","81","1.0","1e0","+1","01",""," 1","99999999999999999999")) {
            rejected(() -> new TextEditTiming(invalid,null,false,0),IllegalArgumentException.class);
        }
        for(String invalid:Arrays.asList("-1","301","25.0","2.5e1","+25","025",""," 25","99999999999999999999")) {
            rejected(() -> new TextEditTiming(null,invalid,true,0),IllegalArgumentException.class);
        }

        for(int duration:new int[]{0,1,24,25,26,300}) {
            TextEditTiming pause=new TextEditTiming("0",Integer.toString(duration),true,0);
            List<Long> slices=new ArrayList<>();AtomicInteger cancellationChecks=new AtomicInteger();
            pause.pauseBeforeEnter(() -> cancellationChecks.incrementAndGet(),slices::add);
            equal(duration,slices.stream().mapToLong(Long::longValue).sum());
            equal((duration+24)/25,slices.size());
            equal(1+slices.size(),cancellationChecks.get());
            for(long slice:slices) {checks++;if(slice<1 || slice>25) throw new AssertionError("Unbounded pause slice");}
        }
        AtomicInteger slept=new AtomicInteger();
        try {
            new TextEditTiming("0","300",true,0).pauseBeforeEnter(() -> {
                if(slept.get()>0) throw new InterruptedException("Stopped");
            },millis -> slept.addAndGet((int)millis));
            throw new AssertionError("Cancellation ignored");
        } catch(InterruptedException expected) {equal(25,slept.get());}
        try {
            new TextEditTiming("0","0",true,0).pauseBeforeEnter(() -> {throw new InterruptedException("Stopped");},millis -> {throw new AssertionError("Sleep after cancellation");});
            throw new AssertionError("Zero-pause cancellation ignored");
        } catch(InterruptedException expected) {checks++;}

        CharacterTiming chars=new CharacterTiming("A(a",96,Arrays.asList("50","60","70"));
        equal(660,chars.scopedDurationMs(0,300));
        equal(690,chars.scopedDurationMs(3,300));
        equal(1020,chars.robotDurationMs(true,0,300));
        equal(1050,chars.robotDurationMs(true,3,300));
        System.out.println("TextEditTiming: "+checks+" offline checks passed");
    }
}
