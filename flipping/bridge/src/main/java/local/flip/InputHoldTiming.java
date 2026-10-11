package local.flip;

import java.util.List;

/** Immutable key-hold plan and a release-guaranteed, interruptible hold primitive. */
final class InputHoldTiming {
    interface Action { void run() throws Exception; }
    interface CancellationCheck { void check() throws Exception; }
    interface Sleeper { void sleep(long millis) throws Exception; }

    private final int[] holds;
    private final int enterHold;

    InputHoldTiming(int textLength,List<String> keyTokens,String enterToken,boolean enter) {
        if(textLength<1 || textLength>80) throw new IllegalArgumentException("Invalid text length");
        if(keyTokens!=null && keyTokens.size()!=textLength) throw new IllegalArgumentException("Key hold length mismatch");
        holds=keyTokens==null ? null : new int[textLength];
        if(holds!=null) for(int i=0;i<holds.length;i++) holds[i]=PointerTiming.integerToken(keyTokens.get(i),20,65);
        if(enterToken!=null && !enter) throw new IllegalArgumentException("Enter hold requires Enter");
        enterHold=enterToken==null ? -1 : PointerTiming.integerToken(enterToken,25,75);
    }

    int keyHoldAt(int index) { return holds==null ? -1 : holds[index]; }
    int enterHoldMs() { return enterHold; }
    long totalHoldMs() {
        long total=enterHold<0 ? 0 : enterHold;
        if(holds!=null) for(int hold:holds) total+=hold;
        return total;
    }

    static void pause(int duration,CancellationCheck cancelled,Sleeper sleeper) throws Exception {
        if(duration<0 || duration>2000) throw new IllegalArgumentException("Unbounded hold/pause");
        cancelled.check();
        for(int remaining=duration;remaining>0;) {
            int slice=Math.min(25,remaining);
            sleeper.sleep(slice);remaining-=slice;cancelled.check();
        }
    }

    static void held(Action press,Action release,int duration,CancellationCheck cancelled,Sleeper sleeper) throws Exception {
        if(duration<0 || duration>2000) throw new IllegalArgumentException("Unbounded hold/pause");
        cancelled.check();
        try {
            press.run();
            pause(duration,cancelled,sleeper);
        } finally {
            // A failing press may already have notified some listeners.
            release.run();
        }
    }
}
