package local.flip;

import java.awt.Point;
import java.awt.Rectangle;
import java.util.Arrays;

/** Deterministic geometry and schedule checks; no canvas or input dispatch. */
public final class PointerTimingChecks {
    private static int checks;
    private static void equal(long expected,long actual) {checks++;if(expected!=actual) throw new AssertionError(expected+" != "+actual);}
    private static void truth(boolean condition) {checks++;if(!condition) throw new AssertionError("Unsafe geometry/schedule");}
    private static void rejected(Runnable operation) {
        checks++;try {operation.run();}catch(IllegalArgumentException expected){return;}
        throw new AssertionError("Invalid pointer timing accepted");
    }
    public static void main(String[] args) {
        PointerTiming timing=new PointerTiming(.35,.65,"450","18","110");
        Rectangle target=new Rectangle(50,40,80,30);
        equal(450,timing.durationMs());equal(18,timing.steps());equal(110,timing.holdMs());
        Point endpoint=timing.endPoint(target);equal(78,endpoint.x);equal(59,endpoint.y);
        for(Point initial:Arrays.asList(new Point(0,0),new Point(299,199),new Point(-100,1000),new Point(120,5))) {
            Point start=PointerTiming.clip(initial,300,200);Point[] path=timing.path(initial,target,300,200);
            equal(18,path.length);Point previous=start;
            for(int i=0;i<path.length;i++) {
                Point point=path[i];truth(point.x>=0 && point.x<300 && point.y>=0 && point.y<200);
                truth((endpoint.x>=start.x ? point.x>=previous.x && point.x<=endpoint.x : point.x<=previous.x && point.x>=endpoint.x));
                truth((endpoint.y>=start.y ? point.y>=previous.y && point.y<=endpoint.y : point.y<=previous.y && point.y>=endpoint.y));
                truth(timing.deadlineMs(i)>=(i==0 ? 0 : timing.deadlineMs(i-1)));
                previous=point;
            }
            truth(previous.equals(endpoint));equal(450,timing.deadlineMs(17));
        }
        for(int size=1;size<=40;size++) {
            Rectangle tiny=new Rectangle(5,5,size,size);Point end=timing.endPoint(tiny);
            truth(tiny.contains(end));
            if(size<9) {equal(5+size/2,end.x);equal(5+size/2,end.y);}
            else {truth(end.x>=9 && end.y>=9 && end.x<=5+size-5 && end.y<=5+size-5);}
        }
        equal(80,new PointerTiming(.5,.5,"80","4","35").deadlineMs(3));
        for(double invalid:new double[]{Double.NaN,Double.POSITIVE_INFINITY,.349,.651}) {
            rejected(() -> new PointerTiming(invalid,.5,"100","5","50"));
            rejected(() -> new PointerTiming(.5,invalid,"100","5","50"));
        }
        for(String invalid:Arrays.asList(null,"79","451","80.0","8e1","080","-80","999999999999999")) rejected(() -> new PointerTiming(.5,.5,invalid,"5","50"));
        for(String invalid:Arrays.asList(null,"3","19","5.0")) rejected(() -> new PointerTiming(.5,.5,"100",invalid,"50"));
        for(String invalid:Arrays.asList(null,"34","111","50.0")) rejected(() -> new PointerTiming(.5,.5,"100","5",invalid));
        for(Rectangle invalid:Arrays.asList(null,new Rectangle(0,0,0,1),new Rectangle(-1,0,2,2),new Rectangle(299,0,2,2),new Rectangle(0,199,2,2),new Rectangle(Integer.MAX_VALUE,0,10,2))) {
            rejected(() -> PointerTiming.validateBounds(invalid,300,200));
        }
        rejected(() -> PointerTiming.validateBounds(target,0,200));
        rejected(() -> PointerTiming.clip(null,300,200));
        PointerTiming.Lifetime lifetime=new PointerTiming.Lifetime(100);
        equal(3_000_000_000L,lifetime.remainingNanos(100));
        equal(1,lifetime.remainingNanos(3_000_000_099L));
        checks++;try {lifetime.check(3_000_000_100L);throw new AssertionError("Late press lifetime accepted");}catch(IllegalStateException expected){}
        checks++;try {lifetime.check(6_000_000_100L);throw new AssertionError("Stalled guards allowed late press");}catch(IllegalStateException expected){}
        PointerTiming.PressGate queued=new PointerTiming.PressGate();queued.preventFuturePress();
        truth(!queued.beginPress());truth(!queued.wasAttempted());
        PointerTiming.PressGate started=new PointerTiming.PressGate();truth(started.beginPress());started.preventFuturePress();
        truth(started.wasAttempted());truth(!started.beginPress());
        System.out.println("PointerTiming: "+checks+" offline checks passed");
    }
}
