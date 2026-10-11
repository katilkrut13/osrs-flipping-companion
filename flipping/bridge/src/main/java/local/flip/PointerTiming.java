package local.flip;

import java.awt.Point;
import java.awt.Rectangle;
import java.util.concurrent.atomic.AtomicInteger;

/** Validated, deterministic motion within the visible canvas, without excursions. */
final class PointerTiming {
    static final class PressGate {
        private final AtomicInteger state=new AtomicInteger(); // 0 pending, 1 attempted, 2 disabled.
        boolean beginPress() {return state.compareAndSet(0,1);}
        void preventFuturePress() {state.compareAndSet(0,2);}
        boolean wasAttempted() {return state.get()==1;}
    }
    static final class Lifetime {
        private final long started;
        Lifetime(long started) {this.started=started;}
        long remainingNanos(long now) {
            long elapsed=now-started;
            if(elapsed<0 || elapsed>=3_000_000_000L) throw new IllegalStateException("Pointer command expired before press");
            return 3_000_000_000L-elapsed;
        }
        void check(long now) {remainingNanos(now);}
    }
    private final double xFraction;
    private final double yFraction;
    private final int durationMs;
    private final int steps;
    private final int holdMs;

    PointerTiming(double xFraction,double yFraction,String duration,String steps,String hold) {
        if(!Double.isFinite(xFraction) || !Double.isFinite(yFraction)
                || xFraction<0.35 || xFraction>0.65 || yFraction<0.35 || yFraction>0.65) {
            throw new IllegalArgumentException("Pointer fraction out of range");
        }
        this.xFraction=xFraction;this.yFraction=yFraction;
        this.durationMs=integerToken(duration,80,450);
        this.steps=integerToken(steps,4,18);
        this.holdMs=integerToken(hold,35,110);
    }

    static int integerToken(String token,int min,int max) {
        if(token==null || !token.matches("0|[1-9][0-9]*")) throw new IllegalArgumentException("Integer timing required");
        int value=Integer.parseInt(token);
        if(value<min || value>max) throw new IllegalArgumentException("Timing out of range");
        return value;
    }

    static void validateBounds(Rectangle bounds,int canvasWidth,int canvasHeight) {
        if(bounds==null || canvasWidth<=0 || canvasHeight<=0 || bounds.width<=0 || bounds.height<=0
                || bounds.x<0 || bounds.y<0 || (long)bounds.x+bounds.width>canvasWidth
                || (long)bounds.y+bounds.height>canvasHeight) {
            throw new IllegalArgumentException("Pointer target outside visible canvas");
        }
    }

    static Point clip(Point point,int width,int height) {
        if(point==null || width<=0 || height<=0) throw new IllegalArgumentException("Pointer unavailable");
        return new Point(Math.max(0,Math.min(width-1,point.x)),Math.max(0,Math.min(height-1,point.y)));
    }

    private static int interior(int origin,int size,double fraction) {
        if(size<9) return origin+size/2;
        return origin+Math.max(4,Math.min(size-5,(int)Math.round((size-1)*fraction)));
    }

    Point endPoint(Rectangle bounds) {
        return new Point(interior(bounds.x,bounds.width,xFraction),interior(bounds.y,bounds.height,yFraction));
    }

    Point[] path(Point start,Rectangle bounds,int width,int height) {
        validateBounds(bounds,width,height);
        Point first=clip(start,width,height),last=endPoint(bounds);
        Point[] result=new Point[steps];
        for(int i=0;i<steps;i++) {
            double phase=(i+1.0)/steps;
            double ease=phase*phase*(3-2*phase);
            result[i]=new Point((int)Math.round(first.x+(last.x-first.x)*ease),
                    (int)Math.round(first.y+(last.y-first.y)*ease));
        }
        return result;
    }

    int steps() { return steps; }
    int durationMs() { return durationMs; }
    int holdMs() { return holdMs; }
    int deadlineMs(int index) { return (index+1)*durationMs/steps; }
}
