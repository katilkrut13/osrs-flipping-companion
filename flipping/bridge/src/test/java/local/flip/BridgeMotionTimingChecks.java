package local.flip;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import java.awt.Canvas;
import java.awt.Point;
import java.awt.Rectangle;
import java.awt.Window;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.AtomicReference;
import net.runelite.api.Client;
import net.runelite.api.GameState;
import net.runelite.api.gameval.InterfaceID;
import net.runelite.api.widgets.Widget;
import net.runelite.client.callback.ClientThread;

/** Protocol and client-thread target guards only; never dispatches movement/keys. */
public final class BridgeMotionTimingChecks {
    private static int checks;
    private static final Method POINTER,KEYS,GUARD,DISPATCH,BOUNDED_GUARD;
    private static final Constructor<?> TARGET;
    static {
        try {
            Class<?> target=Class.forName("local.flip.FlipBridgePlugin$PointerTarget");
            POINTER=FlipBridgePlugin.class.getDeclaredMethod("pointerTiming",JsonObject.class,Rectangle.class,int.class,int.class);
            KEYS=FlipBridgePlugin.class.getDeclaredMethod("keyHoldTiming",JsonObject.class);
            GUARD=FlipBridgePlugin.class.getDeclaredMethod("checkPointerContext",JsonObject.class,target);
            BOUNDED_GUARD=FlipBridgePlugin.class.getDeclaredMethod("checkPointerContext",JsonObject.class,target,PointerTiming.Lifetime.class);
            DISPATCH=FlipBridgePlugin.class.getDeclaredMethod("dispatchPointer",Canvas.class,Window.class,JsonObject.class,Map.class,PointerTiming.class,target,boolean.class);
            TARGET=target.getDeclaredConstructor(FlipBridgePlugin.class,Canvas.class,Widget.class,long.class,Point.class);
            for(Method method:new Method[]{POINTER,KEYS,GUARD,BOUNDED_GUARD,DISPATCH}) method.setAccessible(true);
            TARGET.setAccessible(true);
        } catch(Exception ex) {throw new ExceptionInInitializerError(ex);}
    }
    private interface Checked {void run() throws Exception;}
    private static JsonObject json(String value) {return new JsonParser().parse(value).getAsJsonObject();}
    private static void equal(long expected,long actual) {checks++;if(expected!=actual)throw new AssertionError(expected+" != "+actual);}
    private static void rejected(Checked operation) throws Exception {
        checks++;try {operation.run();}catch(InvocationTargetException expected){return;}
        throw new AssertionError("Unsafe protocol/context accepted");
    }
    private static void noClick(Checked operation) throws Exception {
        checks++;try {operation.run();}catch(InvocationTargetException expected){
            if(expected.getCause().getClass().getSimpleName().equals("PointerNotDispatched")) return;
            throw new AssertionError("Incorrect no-click classification",expected);
        }
        throw new AssertionError("Unexpected pointer dispatch");
    }
    private static void set(FlipBridgePlugin plugin,String name,Object value) throws Exception {
        Field field=FlipBridgePlugin.class.getDeclaredField(name);field.setAccessible(true);field.set(plugin,value);
    }
    private static final class MockWidget {
        final int id,index;int item=-1;String name="",text="Confirm";String[] actions={"Confirm"};
        Rectangle bounds=new Rectangle(10,10,100,30);boolean hidden;Widget parent;
        final Widget proxy;
        MockWidget(int id,int index) {
            this.id=id;this.index=index;
            proxy=(Widget)Proxy.newProxyInstance(Widget.class.getClassLoader(),new Class<?>[]{Widget.class},(object,method,args) -> {
                switch(method.getName()) {
                    case "getId":return this.id;case "getIndex":return this.index;case "getItemId":return item;
                    case "getName":return name;case "getText":return text;case "getActions":return actions;
                    case "getBounds":return new Rectangle(bounds);case "isHidden":return hidden;case "getParent":return parent;
                    default:throw new AssertionError("Unexpected widget read "+method.getName());
                }
            });
        }
        String key() {return id+":"+index;}
    }
    @SuppressWarnings("unchecked")
    public static void main(String[] args) throws Exception {
        FlipBridgePlugin plugin=new FlipBridgePlugin();
        Rectangle bounds=new Rectangle(10,10,100,30);
        String valid="{\"end_x_fraction\":0.5,\"end_y_fraction\":0.5,\"duration_ms\":120,\"steps\":6,\"hold_ms\":50}";
        JsonObject pointer=json("{\"type\":\"click\",\"pointer_plan\":"+valid+"}");
        PointerTiming timing=(PointerTiming)POINTER.invoke(plugin,pointer,bounds,300,200);
        equal(120,timing.durationMs());equal(6,timing.steps());equal(50,timing.holdMs());
        equal(0,POINTER.invoke(plugin,json("{}"),null,0,0)==null?0:1);
        for(String invalid:new String[]{"null","false","[]","50","\"plan\""}) {
            rejected(() -> POINTER.invoke(plugin,json("{\"pointer_plan\":"+invalid+"}"),bounds,300,200));
        }
        for(String field:new String[]{"end_x_fraction","end_y_fraction","duration_ms","steps","hold_ms"}) {
            JsonObject missing=json(valid);missing.remove(field);JsonObject command=new JsonObject();command.add("pointer_plan",missing);
            rejected(() -> POINTER.invoke(plugin,command,bounds,300,200));
            for(String invalid:new String[]{"null","false","\"50\"","[]","{}"}) {
                JsonObject bad=json(valid);bad.add(field,new JsonParser().parse(invalid));JsonObject badCommand=new JsonObject();badCommand.add("pointer_plan",bad);
                rejected(() -> POINTER.invoke(plugin,badCommand,bounds,300,200));
            }
        }
        for(String field:new String[]{"duration_ms","steps","hold_ms"}) {
            JsonObject bad=json(valid);bad.addProperty(field,50.0);JsonObject command=new JsonObject();command.add("pointer_plan",bad);
            rejected(() -> POINTER.invoke(plugin,command,bounds,300,200));
        }
        rejected(() -> POINTER.invoke(plugin,pointer,new Rectangle(-1,0,2,2),300,200));
        rejected(() -> POINTER.invoke(plugin,pointer,bounds,0,200));
        InputHoldTiming holds=(InputHoldTiming)KEYS.invoke(plugin,json("{\"type\":\"text\",\"text\":\"ab\",\"key_hold_ms\":[28,55],\"enter\":true,\"enter_hold_ms\":65}"));
        equal(148,holds.totalHoldMs());
        equal(0,((InputHoldTiming)KEYS.invoke(plugin,json("{\"type\":\"text\",\"text\":\"ab\"}"))).totalHoldMs());
        for(String invalid:new String[]{"null","false","[]","[20]","[20,50,55]","[20,19]","[20,66]","[20,25.0]","[20,2.5e1]","[20,\"25\"]","[20,null]"}) {
            rejected(() -> KEYS.invoke(plugin,json("{\"type\":\"text\",\"text\":\"ab\",\"key_hold_ms\":"+invalid+"}")));
        }
        for(String invalid:new String[]{"null","false","24","76","25.0","2.5e1","\"25\""}) {
            rejected(() -> KEYS.invoke(plugin,json("{\"type\":\"text\",\"text\":\"ab\",\"enter\":true,\"enter_hold_ms\":"+invalid+"}")));
        }
        rejected(() -> KEYS.invoke(plugin,json("{\"type\":\"text\",\"text\":\"ab\",\"enter_hold_ms\":25}")));
        rejected(() -> KEYS.invoke(plugin,json("{\"type\":\"click\",\"text\":\"ab\",\"key_hold_ms\":[25,25]}")));

        String oldAllow=System.getProperty("flip.allowInput");System.setProperty("flip.allowInput","true");
        ExecutorService executor=Executors.newSingleThreadExecutor(r -> new Thread(r,"offline-pointer-client"));
        Path folder=Files.createTempDirectory("flip-pointer-guard-");
        try {
            Canvas canvas=new Canvas();canvas.setSize(300,200); // Headless, never shown; no listeners/input sent.
            AtomicLong account=new AtomicLong(42);AtomicReference<GameState> game=new AtomicReference<>(GameState.LOGGED_IN);
            AtomicInteger reads=new AtomicInteger();
            Client client=(Client)Proxy.newProxyInstance(Client.class.getClassLoader(),new Class<?>[]{Client.class},(object,method,arguments) -> {
                if(!Thread.currentThread().getName().equals("offline-pointer-client"))throw new AssertionError("Off-thread client read");reads.incrementAndGet();
                switch(method.getName()) {
                    case "getGameState":return game.get();case "getAccountHash":return account.get();case "getCanvas":return canvas;
                    case "getMouseCanvasPosition":return new net.runelite.api.Point(20,20);
                    default:throw new AssertionError("Unexpected client read "+method.getName());
                }
            });
            ClientThread thread=new ClientThread(){@Override public void invokeLater(Runnable task){executor.submit(task);}};
            set(plugin,"client",client);set(plugin,"clientThread",thread);set(plugin,"gson",new Gson());set(plugin,"folder",folder);
            Field mapField=FlipBridgePlugin.class.getDeclaredField("widgets");mapField.setAccessible(true);Map<String,Widget> widgets=(Map<String,Widget>)mapField.get(plugin);
            MockWidget target=new MockWidget(12345,0);widgets.put(target.key(),target.proxy);
            MockWidget quantity=new MockWidget(InterfaceID.GeOffers.SETUP,34);quantity.text="100";widgets.put(quantity.key(),quantity.proxy);
            MockWidget price=new MockWidget(InterfaceID.GeOffers.SETUP,41);price.text="50";widgets.put(price.key(),price.proxy);
            MockWidget item=new MockWidget(InterfaceID.GeOffers.SETUP,21);item.item=565;widgets.put(item.key(),item.proxy);
            Object expected=TARGET.newInstance(plugin,canvas,target.proxy,42L,null);
            Field sessionField=FlipBridgePlugin.class.getDeclaredField("session");sessionField.setAccessible(true);String session=(String)sessionField.get(plugin);
            JsonObject command=json("{\"type\":\"click\",\"id\":\"offline-pointer\",\"session\":\""+session+"\"}");
            Point current=(Point)GUARD.invoke(plugin,command,expected);equal(20,current.x);equal(20,current.y);
            target.bounds=new Rectangle(11,10,100,30);rejected(() -> GUARD.invoke(plugin,command,expected));
            noClick(() -> DISPATCH.invoke(plugin,canvas,null,command,new HashMap<>(),timing,expected,true));target.bounds=new Rectangle(10,10,100,30);
            target.hidden=true;rejected(() -> GUARD.invoke(plugin,command,expected));target.hidden=false;
            target.name="Other";rejected(() -> GUARD.invoke(plugin,command,expected));target.name="";
            target.text="Other";rejected(() -> GUARD.invoke(plugin,command,expected));target.text="Confirm";
            target.item=566;rejected(() -> GUARD.invoke(plugin,command,expected));target.item=-1;
            target.actions[0]="Other";rejected(() -> GUARD.invoke(plugin,command,expected));target.actions[0]="Confirm";
            widgets.put(target.key(),new MockWidget(12345,0).proxy);rejected(() -> GUARD.invoke(plugin,command,expected));widgets.put(target.key(),target.proxy);
            quantity.text="110";rejected(() -> GUARD.invoke(plugin,command,expected));quantity.text="100";
            price.text="60";rejected(() -> GUARD.invoke(plugin,command,expected));price.text="50";
            item.item=566;rejected(() -> GUARD.invoke(plugin,command,expected));item.item=565;
            canvas.setSize(301,200);rejected(() -> GUARD.invoke(plugin,command,expected));canvas.setSize(300,200);
            account.set(43);rejected(() -> GUARD.invoke(plugin,command,expected));account.set(42);
            game.set(GameState.LOGIN_SCREEN);rejected(() -> GUARD.invoke(plugin,command,expected));game.set(GameState.LOGGED_IN);
            command.addProperty("session","changed");rejected(() -> GUARD.invoke(plugin,command,expected));command.addProperty("session",session);
            target.parent=target.proxy;rejected(() -> GUARD.invoke(plugin,command,expected));target.parent=null;
            int before=reads.get();
            rejected(() -> BOUNDED_GUARD.invoke(plugin,command,expected,new PointerTiming.Lifetime(System.nanoTime()-3_000_000_000L)));
            equal(before,reads.get());
            Files.writeString(folder.resolve("input-cancel.json"),"{\"id\":\"offline-pointer\",\"session\":\""+session+"\"}");
            before=reads.get();rejected(() -> GUARD.invoke(plugin,command,expected));equal(before,reads.get());
            noClick(() -> DISPATCH.invoke(plugin,canvas,null,command,new HashMap<>(),timing,expected,true));
            Files.delete(folder.resolve("input-cancel.json"));
            set(plugin,"clientThread",new ClientThread(){@Override public void invokeLater(Runnable task){}});
            long began=System.nanoTime();noClick(() -> DISPATCH.invoke(plugin,canvas,null,command,new HashMap<>(),timing,expected,true));
            checks++;if(TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-began)>4000)throw new AssertionError("Stalled pointer guard exceeded prepress budget");
        } finally {
            executor.shutdownNow();executor.awaitTermination(3,TimeUnit.SECONDS);Files.deleteIfExists(folder.resolve("input-cancel.json"));Files.deleteIfExists(folder);
            if(oldAllow==null)System.clearProperty("flip.allowInput");else System.setProperty("flip.allowInput",oldAllow);
        }
        System.out.println("BridgeMotionTiming: "+checks+" offline checks passed");
    }
}
