package local.flip;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import java.lang.reflect.Field;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.AtomicReference;
import net.runelite.api.Client;
import net.runelite.api.GameState;
import net.runelite.client.callback.ClientThread;

/** Offline bridge protocol and guard checks; no canvas, input events or game client. */
public final class BridgeCharacterTimingChecks {
    private static int checks;
    private static final Method PARSE;
    private static final Method GUARD;
    private static final Method EDIT;

    static {
        try {
            PARSE=FlipBridgePlugin.class.getDeclaredMethod("characterTiming",JsonObject.class,boolean.class);
            GUARD=FlipBridgePlugin.class.getDeclaredMethod("checkTypingContext",JsonObject.class,long.class,int.class);
            EDIT=FlipBridgePlugin.class.getDeclaredMethod("textEditTiming",JsonObject.class,int.class);
            PARSE.setAccessible(true); GUARD.setAccessible(true);EDIT.setAccessible(true);
        } catch(Exception ex) { throw new ExceptionInInitializerError(ex); }
    }

    private interface CheckedOperation { void run() throws Exception; }

    private static void equal(long expected,long actual) {
        checks++;
        if(expected!=actual) throw new AssertionError("Expected "+expected+", got "+actual);
    }

    private static void rejected(CheckedOperation operation) throws Exception {
        checks++;
        try { operation.run(); }
        catch(InvocationTargetException expected) { return; }
        throw new AssertionError("Invalid command/context accepted");
    }

    private static CharacterTiming parse(FlipBridgePlugin plugin,String fields,boolean scoped) throws Exception {
        JsonObject command=new JsonParser().parse("{\"text\":\"ab\""+fields+"}").getAsJsonObject();
        return (CharacterTiming)PARSE.invoke(plugin,command,scoped);
    }

    private static void set(FlipBridgePlugin plugin,String name,Object value) throws Exception {
        Field field=FlipBridgePlugin.class.getDeclaredField(name);field.setAccessible(true);field.set(plugin,value);
    }

    private static TextEditTiming edit(FlipBridgePlugin plugin,String fields,int observedLength) throws Exception {
        JsonObject command=new JsonParser().parse("{\"type\":\"text\""+fields+"}").getAsJsonObject();
        return (TextEditTiming)EDIT.invoke(plugin,command,observedLength);
    }

    public static void main(String[] args) throws Exception {
        FlipBridgePlugin plugin=new FlipBridgePlugin();
        equal(90,parse(plugin,"",false).scalarDelay()); // Existing foreground fallback.
        equal(96,parse(plugin,",\"key_delay_ms\":96",true).delayAt(1));
        for(boolean scoped:new boolean[]{false,true}) {
            CharacterTiming plan=parse(plugin,",\"key_delay_ms\":96,\"key_delays_ms\":[50,150]",scoped);
            equal(50,plan.delayAt(0)); equal(150,plan.delayAt(1));
            for(String invalid:new String[]{"null","false","50","[]","[50]","[50,90,100]",
                    "[50,49]","[50,151]","[50,50.0]","[50,1e2]","[50,\"90\"]","[50,true]","[50,null]"}) {
                rejected(() -> parse(plugin,",\"key_delay_ms\":96,\"key_delays_ms\":"+invalid,scoped));
            }
            rejected(() -> parse(plugin,",\"key_delays_ms\":[90,90]",scoped));
        }
        rejected(() -> parse(plugin,"",true));
        equal(80,edit(plugin,"",-1).clearKeyCount());
        equal(0,edit(plugin,",\"clear_key_count\":0",0).clearKeyCount());
        equal(80,edit(plugin,",\"clear_key_count\":80",80).clearKeyCount());
        equal(300,edit(plugin,",\"clear_key_count\":2,\"enter\":true,\"enter_pause_ms\":300",2).enterPauseMs());
        for(String invalid:new String[]{"null","false","\"0\"","-1","81","1.0","1e0","[]","{}"}) {
            rejected(() -> edit(plugin,",\"clear_key_count\":"+invalid,0));
        }
        for(String invalid:new String[]{"null","false","\"25\"","-1","301","25.0","2.5e1","[]","{}"}) {
            rejected(() -> edit(plugin,",\"enter\":true,\"enter_pause_ms\":"+invalid,0));
        }
        rejected(() -> edit(plugin,",\"clear_key_count\":0",-1));
        rejected(() -> edit(plugin,",\"clear_key_count\":0",1));
        rejected(() -> edit(plugin,",\"clear_key_count\":80",81));
        rejected(() -> edit(plugin,",\"enter_pause_ms\":0",0));
        rejected(() -> edit(plugin,",\"enter\":false,\"enter_pause_ms\":25",0));
        rejected(() -> edit(plugin,",\"enter\":\"true\",\"enter_pause_ms\":25",0));
        rejected(() -> EDIT.invoke(plugin,new JsonParser().parse("{\"type\":\"click\",\"clear_key_count\":0}").getAsJsonObject(),0));

        String oldAllow=System.getProperty("flip.allowInput");
        System.setProperty("flip.allowInput","true");
        ExecutorService clientExecutor=Executors.newSingleThreadExecutor(r -> new Thread(r,"offline-client-guard"));
        Path folder=Files.createTempDirectory("flip-typing-guard-");
        try {
            AtomicInteger clientReads=new AtomicInteger();
            AtomicLong account=new AtomicLong(42);
            AtomicInteger prompt=new AtomicInteger(7);
            AtomicReference<GameState> gameState=new AtomicReference<>(GameState.LOGGED_IN);
            Client client=(Client)Proxy.newProxyInstance(Client.class.getClassLoader(),new Class<?>[]{Client.class},(object,method,arguments) -> {
                if(!Thread.currentThread().getName().equals("offline-client-guard")) throw new AssertionError("Off-thread client read");
                clientReads.incrementAndGet();
                switch(method.getName()) {
                    case "getGameState": return gameState.get();
                    case "getAccountHash": return account.get();
                    case "getVarcIntValue": return prompt.get();
                    default: throw new AssertionError("Unexpected client method "+method.getName());
                }
            });
            set(plugin,"client",client);
            set(plugin,"gson",new com.google.gson.Gson());
            set(plugin,"clientThread",new ClientThread(){
                @Override public void invokeLater(Runnable operation) { clientExecutor.submit(operation); }
            });
            set(plugin,"folder",folder);
            Field sessionField=FlipBridgePlugin.class.getDeclaredField("session");sessionField.setAccessible(true);
            String session=(String)sessionField.get(plugin);
            JsonObject command=new JsonObject();command.addProperty("id","offline-request");command.addProperty("session",session);
            GUARD.invoke(plugin,command,42L,7);checks++;
            account.set(43);rejected(() -> GUARD.invoke(plugin,command,42L,7));account.set(42);
            prompt.set(0);rejected(() -> GUARD.invoke(plugin,command,42L,7));
            prompt.set(8);rejected(() -> GUARD.invoke(plugin,command,42L,7));prompt.set(7);
            gameState.set(GameState.LOGIN_SCREEN);rejected(() -> GUARD.invoke(plugin,command,42L,7));gameState.set(GameState.LOGGED_IN);
            command.addProperty("session","changed");rejected(() -> GUARD.invoke(plugin,command,42L,7));command.addProperty("session",session);
            System.setProperty("flip.allowInput","false");rejected(() -> GUARD.invoke(plugin,command,42L,7));System.setProperty("flip.allowInput","true");
            Files.writeString(folder.resolve("input-cancel.json"),"{\"id\":\"offline-request\",\"session\":\""+session+"\"}");
            int beforeCancel=clientReads.get();
            rejected(() -> GUARD.invoke(plugin,command,42L,7));
            equal(beforeCancel,clientReads.get()); // Cancel is checked before scheduling client reads.
            command.addProperty("id","other-request");GUARD.invoke(plugin,command,42L,7);checks++;
        } finally {
            clientExecutor.shutdownNow();clientExecutor.awaitTermination(3,TimeUnit.SECONDS);
            Files.deleteIfExists(folder.resolve("input-cancel.json"));Files.deleteIfExists(folder);
            if(oldAllow==null) System.clearProperty("flip.allowInput");else System.setProperty("flip.allowInput",oldAllow);
        }
        System.out.println("BridgeCharacterTiming: "+checks+" offline checks passed");
    }
}
