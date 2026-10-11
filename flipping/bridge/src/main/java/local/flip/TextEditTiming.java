package local.flip;

/** Bounded prompt clearing and pre-Enter timing, independent of game/UI APIs. */
final class TextEditTiming {
    static final class BufferLengthChanged extends IllegalStateException {
        BufferLengthChanged() { super("Text buffer length changed"); }
    }

    interface CancellationCheck { void check() throws Exception; }
    interface Sleeper { void sleep(long millis) throws Exception; }

    private final int clearKeyCount;
    private final int enterPauseMs;

    TextEditTiming(String clearToken,String pauseToken,boolean enter,int observedLength) {
        clearKeyCount=clearToken==null ? 80 : integerToken(clearToken,0,80);
        enterPauseMs=pauseToken==null ? 0 : integerToken(pauseToken,0,300);
        if(pauseToken!=null && !enter) throw new IllegalArgumentException("Enter pause requires Enter");
        if(clearToken!=null && (observedLength<0 || observedLength!=clearKeyCount)) {
            throw new BufferLengthChanged();
        }
    }

    private static int integerToken(String token,int min,int max) {
        if(token==null || !token.matches("0|[1-9][0-9]*")) throw new IllegalArgumentException("Integer timing required");
        int value=Integer.parseInt(token);
        if(value<min || value>max) throw new IllegalArgumentException("Edit timing out of range");
        return value;
    }

    static int bufferLength(String input) { return input==null ? -1 : input.length(); }
    int clearKeyCount() { return clearKeyCount; }
    int enterPauseMs() { return enterPauseMs; }

    void pauseBeforeEnter(CancellationCheck cancelled,Sleeper sleeper) throws Exception {
        cancelled.check();
        for(int remaining=enterPauseMs;remaining>0;) {
            int slice=Math.min(25,remaining);
            sleeper.sleep(slice);
            remaining-=slice;
            cancelled.check();
        }
    }
}
