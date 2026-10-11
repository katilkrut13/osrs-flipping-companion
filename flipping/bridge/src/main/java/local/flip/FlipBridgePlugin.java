package local.flip;

import com.google.gson.Gson;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import java.awt.Canvas;
import java.awt.Point;
import java.awt.Rectangle;
import java.awt.Robot;
import java.awt.Window;
import java.awt.MouseInfo;
import java.awt.event.InputEvent;
import java.awt.event.KeyEvent;
import java.awt.event.KeyListener;
import java.awt.event.MouseEvent;
import java.awt.event.MouseListener;
import java.awt.event.MouseMotionListener;
import java.util.concurrent.atomic.AtomicBoolean;
import java.lang.reflect.Field;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.StandardCopyOption;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.Arrays;
import java.util.Objects;
import java.util.concurrent.TimeoutException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.FutureTask;
import javax.inject.Inject;
import javax.swing.SwingUtilities;
import net.runelite.api.Client;
import net.runelite.api.GameState;
import net.runelite.api.GrandExchangeOffer;
import net.runelite.api.InventoryID;
import net.runelite.api.Item;
import net.runelite.api.ItemContainer;
import net.runelite.api.VarClientInt;
import net.runelite.api.VarClientStr;
import net.runelite.api.events.GameTick;
import net.runelite.api.events.ClientTick;
import net.runelite.api.NPC;
import net.runelite.api.NPCComposition;
import net.runelite.api.MenuAction;
import net.runelite.api.gameval.InterfaceID;
import net.runelite.api.widgets.Widget;
import net.runelite.client.eventbus.Subscribe;
import net.runelite.client.game.ItemManager;
import net.runelite.client.callback.ClientThread;
import net.runelite.client.plugins.Plugin;
import net.runelite.client.plugins.PluginDescriptor;

@PluginDescriptor(name = "Local Flip Bridge", description = "Local GE telemetry and opt-in input bridge", enabledByDefault = true)
public class FlipBridgePlugin extends Plugin {
    @Inject private Client client;
    @Inject private ItemManager itemManager;
    @Inject private Gson gson;
    @Inject private ClientThread clientThread;
    private final String session = UUID.randomUUID().toString();
    private final Map<Integer, String> names = new HashMap<>();
    private final Map<String, Widget> widgets = new HashMap<>();
    private ExecutorService io;
    private ExecutorService inputIo;
    private ScheduledExecutorService heartbeat;
    private Path folder;
    private long sequence;
    private String lastCommand = "";
    private final AtomicBoolean inputBusy = new AtomicBoolean();
    private long lastSnapshotMillis;

    @Override protected void startUp() throws Exception {
        folder = Paths.get(System.getProperty("flip.directory", "outputs/flipping")).toAbsolutePath();
        Files.createDirectories(folder);
        io = Executors.newSingleThreadExecutor();
        inputIo = Executors.newSingleThreadExecutor();
        heartbeat=Executors.newSingleThreadScheduledExecutor();
        heartbeat.scheduleAtFixedRate(() -> clientThread.invokeLater((Runnable)() -> onClientTick(null)),600,600,TimeUnit.MILLISECONDS);
        for (Class<?> type : new Class<?>[]{InterfaceID.GeOffers.class, InterfaceID.GeOffersSide.class, InterfaceID.Chatbox.class}) {
            for (Field field : type.getFields()) {
                names.put(field.getInt(null), type.getSimpleName() + "." + field.getName());
            }
        }
        for (String simple : new String[]{"Bankmain", "Bankside", "BankpinKeypad", "WelcomeScreen", "Inventory", "Equipment", "Chatmenu"}) {
            try {
                Class<?> type = Class.forName("net.runelite.api.gameval.InterfaceID$" + simple);
                for (Field field : type.getFields()) names.put(field.getInt(null), simple + "." + field.getName());
            } catch (ClassNotFoundException ex) { /* Not available in this client revision. */ }
        }
    }

    @Override protected void shutDown() {
        if (heartbeat != null) heartbeat.shutdownNow();
        if (io != null) io.shutdownNow();
        if (inputIo != null) inputIo.shutdownNow();
    }

    private void atomic(String filename, Object value) {
        try {
            Path tmp = folder.resolve(filename + ".tmp");
            Files.write(tmp, gson.toJson(value).getBytes(StandardCharsets.UTF_8));
            Files.move(tmp, folder.resolve(filename), StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
        } catch (Exception ex) { System.err.println("Flip bridge file write: " + ex.getClass().getSimpleName()); }
    }

    private String accountKey() throws Exception {
        byte[] bytes = MessageDigest.getInstance("SHA-256").digest(Long.toString(client.getAccountHash()).getBytes(StandardCharsets.UTF_8));
        StringBuilder result = new StringBuilder();
        for (byte b : bytes) result.append(String.format("%02x", b));
        return result.toString();
    }

    @Subscribe public void onClientTick(ClientTick tick) {
        if (io == null || io.isShutdown()) return;
        long nowMillis = System.currentTimeMillis();
        if (nowMillis - lastSnapshotMillis < 600) return;
        lastSnapshotMillis = nowMillis;
        try {
            Map<String, Object> result = new HashMap<>();
            result.put("schema", 1); result.put("session", session); result.put("sequence", ++sequence);
            result.put("observed_at", System.currentTimeMillis() / 1000);
            boolean loggedIn = client.getGameState() == GameState.LOGGED_IN;
            result.put("logged_in", loggedIn);
            result.put("game_state", client.getGameState().name());
            result.put("login_index", client.getLoginIndex());
            result.put("account_key", loggedIn ? accountKey() : "");
            Widget ge = client.getWidget(InterfaceID.GeOffers.UNIVERSE);
            result.put("ge_open", loggedIn && ge != null && !ge.isHidden());
            result.put("input_enabled", Boolean.getBoolean("flip.allowInput"));
            result.put("paced_text_supported", true);
            result.put("character_timing_supported", true);
            result.put("text_edit_timing_supported", true);
            result.put("pointer_timing_supported",true);
            result.put("key_hold_timing_supported",true);
            result.put("background_input_supported", true);
            result.put("session_control_supported", true);
            result.put("input_busy", inputBusy.get());
            Canvas focusedCanvas = client.getCanvas();
            result.put("canvas_width",focusedCanvas.getWidth());result.put("canvas_height",focusedCanvas.getHeight());
            net.runelite.api.Point mouse=client.getMouseCanvasPosition();
            Map<String,Object> mousePosition=new HashMap<>();
            boolean inside=mouse!=null && mouse.getX()>=0 && mouse.getY()>=0
                    && mouse.getX()<focusedCanvas.getWidth() && mouse.getY()<focusedCanvas.getHeight();
            mousePosition.put("x",inside ? mouse.getX() : -1);mousePosition.put("y",inside ? mouse.getY() : -1);
            result.put("canvas_mouse_position",mousePosition);
            Window focusedWindow = SwingUtilities.getWindowAncestor(focusedCanvas);
            result.put("canvas_focused", focusedWindow != null && focusedWindow.isActive() && focusedCanvas.isFocusOwner());
            if (!loggedIn) {
                widgets.clear();
                result.put("input_type",0);result.put("cash_inventory_gp",0);
                result.put("inventory",new ArrayList<>());result.put("offers",new ArrayList<>());result.put("widgets",new ArrayList<>());
                result.put("bank_open",false);result.put("bank_pin_open",false);result.put("security_prompt_open",false);result.put("welcome_open",false);
                io.submit(() -> atomic("live-state.json",result));processCommand();return;
            }
            int inputType=client.getVarcIntValue(VarClientInt.INPUT_TYPE);
            result.put("input_type",inputType);
            if(inputType!=0) {
                // Expose only the buffer length, never prompt contents.
                result.put("input_text_length",TextEditTiming.bufferLength(client.getVarcStrValue(VarClientStr.INPUT_TEXT)));
            }
            List<Map<String, Object>> inventory = new ArrayList<>();
            long cash = 0;
            ItemContainer items = client.getItemContainer(InventoryID.INVENTORY);
            if (loggedIn && items != null) {
                for (Item item : items.getItems()) {
                    if (item.getId() <= 0 || item.getQuantity() <= 0) continue;
                    Map<String, Object> entry = new HashMap<>();
                    entry.put("id", item.getId()); entry.put("quantity", item.getQuantity());
                    entry.put("name",itemManager.getItemComposition(item.getId()).getName());
                    // Normalize banknotes for inventory-to-sell matching.
                    entry.put("unnoted_id", itemManager.canonicalize(item.getId()));
                    inventory.add(entry);
                    if (item.getId() == 995) cash += item.getQuantity();
                }
            }
            result.put("cash_inventory_gp", cash); result.put("inventory", inventory);
            Widget bank = client.getWidget(InterfaceID.Bankmain.UNIVERSE);
            boolean bankOpen = loggedIn && bank != null && !bank.isHidden();
            result.put("bank_open", bankOpen);
            List<Map<String, Object>> bankItems = new ArrayList<>();
            ItemContainer bankContainer = client.getItemContainer(InventoryID.BANK);
            if (bankOpen && bankContainer != null) for (Item item : bankContainer.getItems()) {
                if (item.getId() <= 0 || item.getQuantity() <= 0) continue;
                if (itemManager.getItemComposition(item.getId()).getPlaceholderTemplateId() != -1) continue;
                Map<String, Object> b = new HashMap<>(); b.put("id", item.getId()); b.put("quantity", item.getQuantity());
                b.put("unnoted_id", itemManager.canonicalize(item.getId())); bankItems.add(b);
            }
            result.put("bank_inventory", bankItems);
            result.put("bank_note_mode",client.getVarbitValue(3958)==1);
            // ACCOUNT_CREDIT (varp 1780) is account membership, not world membership.
            boolean accountMembers = loggedIn && client.getVarpValue(1780) > 0;
            result.put("account_members", accountMembers);
            result.put("ge_slot_capacity", accountMembers ? 8 : 3);
            result.put("in_wilderness",client.getVarbitValue(5963)>0);
            List<Map<String, Object>> npcs = new ArrayList<>();
            if (loggedIn && client.getLocalPlayer() != null) {
                net.runelite.api.coords.WorldPoint loc = client.getLocalPlayer().getWorldLocation();
                Map<String, Object> p = new HashMap<>(); p.put("x",loc.getX()); p.put("y",loc.getY()); p.put("plane",loc.getPlane());
                result.put("player_location",p);
                for (NPC npc : client.getNpcs()) {
                    NPCComposition comp = npc.getTransformedComposition();
                    if (comp == null || !("Banker".equalsIgnoreCase(comp.getName()) || "Grand Exchange Clerk".equalsIgnoreCase(comp.getName()))) continue;
                    Map<String,Object> n = new HashMap<>(); n.put("index",npc.getIndex()); n.put("name",comp.getName()); n.put("actions",comp.getActions());
                    n.put("distance",loc.distanceTo(npc.getWorldLocation())); npcs.add(n);
                }
            }
            result.put("npc_targets",npcs);
            List<Map<String, Object>> offers = new ArrayList<>();
            GrandExchangeOffer[] all = client.getGrandExchangeOffers();
            if (loggedIn && all != null) for (int slot = 0; slot < all.length; slot++) {
                GrandExchangeOffer offer = all[slot];
                if (offer == null) continue;
                Map<String, Object> entry = new HashMap<>();
                entry.put("slot", slot); entry.put("state", offer.getState().name());
                entry.put("item_id", offer.getItemId());
                entry.put("item_name", offer.getItemId() > 0 ? itemManager.getItemComposition(offer.getItemId()).getName() : "");
                entry.put("price", offer.getPrice()); entry.put("quantity", offer.getTotalQuantity());
                entry.put("filled", offer.getQuantitySold()); entry.put("spent", offer.getSpent());
                offers.add(entry);
            }
            result.put("offers", offers);
            widgets.clear();
            List<Map<String, Object>> visible = new ArrayList<>();
            IdentityHashMap<Widget, Boolean> seen = new IdentityHashMap<>();
            for (int group : new int[]{465, 467, 12, 15, 14, 213, 378, 149, 187, 219}) {
                for (int child = 0; child < 40; child++) visit(client.getWidget(group, child), visible, seen, 0);
            }
            // Export only numeric input prompts and GE search results, never chat history.
            for (int id : new int[]{InterfaceID.Chatbox.MES_TEXT, InterfaceID.Chatbox.MES_TEXT2,
                                   InterfaceID.Chatbox.MES_LAYER_SCROLLCONTENTS}) visit(client.getWidget(id), visible, seen, 0);
            result.put("widgets", visible);
            result.put("bank_pin_open", visible.stream().anyMatch(w -> (((Integer)w.get("id")) >>> 16) == 213));
            result.put("security_prompt_open", visible.stream().anyMatch(w -> (((Integer)w.get("id")) >>> 16) == 14));
            result.put("welcome_open", visible.stream().anyMatch(w -> (((Integer)w.get("id")) >>> 16) == 378));
            String payload = gson.toJson(result);
            io.submit(() -> {
                try {
                    Path tmp = folder.resolve("live-state.json.tmp");
                    Files.write(tmp, payload.getBytes(StandardCharsets.UTF_8));
                    Files.move(tmp, folder.resolve("live-state.json"), StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
                } catch (Exception ex) { System.err.println("Snapshot failed: " + ex.getClass().getSimpleName()); }
            });
            processCommand();
        } catch (Exception ex) { System.err.println("Flip bridge snapshot: " + ex.getClass().getSimpleName()); }
    }

    private void visit(Widget widget, List<Map<String, Object>> output, IdentityHashMap<Widget, Boolean> seen, int depth) {
        if (widget == null || depth > 12 || seen.put(widget, true) != null || widget.isHidden()) return;
        Rectangle bounds = widget.getBounds();
        String key = widget.getId() + ":" + widget.getIndex();
        if (bounds.width > 0 && bounds.height > 0) {
            widgets.put(key, widget);
            Map<String, Object> value = new HashMap<>();
            value.put("key", key); value.put("id", widget.getId()); value.put("index", widget.getIndex());
            value.put("symbol", names.getOrDefault(widget.getId(), ""));
            value.put("text", widget.getText()); value.put("name", widget.getName());
            value.put("item_id", widget.getItemId()); value.put("item_quantity", widget.getItemQuantity());
            value.put("actions", widget.getActions()); value.put("parent_id", widget.getParentId());
            if(widget.getItemId()>0 && (widget.getId() >>> 16)==149) value.put("inventory_actions",itemManager.getItemComposition(widget.getItemId()).getInventoryActions());
            value.put("x", bounds.x); value.put("y", bounds.y); value.put("width", bounds.width); value.put("height", bounds.height);
            output.add(value);
        }
        for (Widget[] children : new Widget[][]{widget.getDynamicChildren(), widget.getStaticChildren(), widget.getNestedChildren()})
            if (children != null) for (Widget child : children) visit(child, output, seen, depth + 1);
    }

    private void processCommand() throws Exception {
        Path source = folder.resolve("input-command.json");
        if (!Files.exists(source)) return;
        JsonObject command = gson.fromJson(Files.readString(source), JsonObject.class);
        String id = command.get("id").getAsString();
        if (id.equals(lastCommand)) return;
        lastCommand = id;
        Map<String, Object> receipt = new HashMap<>(); receipt.put("id", id);
        String reason = null;
        Canvas canvas = client.getCanvas();
        Window window = SwingUtilities.getWindowAncestor(canvas);
        String type = command.get("type").getAsString();
        boolean scoped = command.has("input_mode") && "SCOPED_CANVAS".equals(command.get("input_mode").getAsString());
        if (!Boolean.getBoolean("flip.allowInput")) reason = "Input bridge disabled";
        else if (!session.equals(command.get("session").getAsString()) || sequence - command.get("sequence").getAsLong() > 3 || command.get("sequence").getAsLong() > sequence) reason = "Stale command";
        else if (Math.abs(System.currentTimeMillis() / 1000 - command.get("created_at").getAsLong()) > 3) reason = "Expired command";
        else if (inputBusy.get()) reason = "Previous input still busy";
        else if (!type.equals("login_saved") && client.getGameState() != GameState.LOGGED_IN) reason = "Not logged in";
        else if (!scoped && (window == null || !window.isActive() || !canvas.isFocusOwner())) reason = "RuneLite canvas not focused";
        else if (type.equals("login_saved") && (client.getGameState() != GameState.LOGIN_SCREEN || client.getLoginIndex() != 10)) reason = "Saved-account login screen required";
        Widget target = command.has("widget_key") ? widgets.get(command.get("widget_key").getAsString()) : null;
        if (reason == null && (type.equals("click") || type.equals("widget_action") || type.equals("bank_pin")) && target == null) reason = "Widget not visible";
        if (reason == null && type.equals("bank_pin")) {
            boolean directDigit = (target.getId() >>> 16) == 213
                    && target.getText().replaceAll("<[^>]*>", "").trim().matches("[0-9]");
            boolean button = (target.getId() >>> 16) == 213 && target.getIndex() == 0
                    && target.getActions() != null;
            if (button) {
                boolean selectable = false;
                for (String action : target.getActions()) if ("Select".equals(action)) selectable = true;
                button = selectable;
            }
            if (!directDigit && !button) reason="Observed bank keypad digit/button required";
        }
        if (reason == null && !type.equals("bank_pin") && target != null && ((target.getId() >>> 16)==213 || (target.getId() >>> 16)==14)) reason="Security prompt requires user";
        if (reason == null && type.equals("text") && client.getVarcIntValue(VarClientInt.INPUT_TYPE) == 0) reason = "No game input prompt";
        if (reason == null && !type.equals("text") && (command.has("clear_key_count") || command.has("enter_pause_ms"))) reason="Unsupported text edit timing";
        if(reason==null && command.has("pointer_plan") && !type.equals("click") && !type.equals("bank_pin")) reason="Unsupported pointer timing";
        if(reason==null && !type.equals("text") && (command.has("key_hold_ms") || command.has("enter_hold_ms"))) reason="Unsupported key hold timing";
        if (reason != null) {
            receipt.put("status", "rejected"); receipt.put("reason", reason);
            io.submit(() -> atomic("input-receipt.json", receipt)); return;
        }
        final CharacterTiming characterTiming;
        if (type.equals("text")) {
            try {
                // Parse the entire plan before clearing or dispatching any key.
                characterTiming = characterTiming(command, scoped);
            } catch (RuntimeException ex) {
                receipt.put("status", "rejected"); receipt.put("reason", "Unsupported paced text");
                io.submit(() -> atomic("input-receipt.json", receipt)); return;
            }
        } else characterTiming = null;
        final TextEditTiming textEditTiming;
        if(type.equals("text")) {
            try {
                textEditTiming=textEditTiming(command,TextEditTiming.bufferLength(client.getVarcStrValue(VarClientStr.INPUT_TEXT)));
            } catch(TextEditTiming.BufferLengthChanged ex) {
                receipt.put("status","rejected");receipt.put("reason","Text buffer length changed");
                io.submit(() -> atomic("input-receipt.json",receipt));return;
            } catch(RuntimeException ex) {
                receipt.put("status","rejected");receipt.put("reason","Unsupported text edit timing");
                io.submit(() -> atomic("input-receipt.json",receipt));return;
            }
        } else textEditTiming=null;
        final InputHoldTiming keyHoldTiming;
        try { keyHoldTiming=type.equals("text") ? keyHoldTiming(command) : null; }
        catch(RuntimeException ex) {
            receipt.put("status","rejected");receipt.put("reason","Unsupported key hold timing");
            io.submit(() -> atomic("input-receipt.json",receipt));return;
        }
        final PointerTiming pointerTiming;
        try { pointerTiming=pointerTiming(command,target==null ? null : target.getBounds(),canvas.getWidth(),canvas.getHeight()); }
        catch(RuntimeException ex) {
            receipt.put("status","rejected");receipt.put("reason","Unsupported pointer timing");
            io.submit(() -> atomic("input-receipt.json",receipt));return;
        }
        final PointerTarget pointerTarget=pointerTiming==null ? null : new PointerTarget(canvas,target,client.getAccountHash(),scoped ? null : canvas.getLocationOnScreen());
        final long textAccount = type.equals("text") ? client.getAccountHash() : 0;
        final int textPrompt = type.equals("text") ? client.getVarcIntValue(VarClientInt.INPUT_TYPE) : 0;
        if (type.equals("login_saved")) {
            // Reuse only the native client's already authenticated saved-account
            // screen. No credentials, tokens or authentication dialogs are read.
            inputBusy.set(true);
            inputIo.submit(() -> {
                try {
                    // The native Enter handler selects the Jagex token login
                    // method; changing GameState alone selects password login.
                    scopedKey(canvas,KeyEvent.VK_ENTER,'\n');
                    receipt.put("status","dispatched");
                } catch(Exception ex) {receipt.put("status","rejected");receipt.put("reason",ex.getClass().getSimpleName());}
                finally {atomic("input-receipt.json",receipt);inputBusy.set(false);}
            });
            return;
        }
        if (type.equals("npc")) {
            int index = command.get("npc_index").getAsInt(); String action = command.get("action").getAsString();
            NPC chosen = null;
            for (NPC npc : client.getNpcs()) if (npc.getIndex() == index) chosen = npc;
            if (chosen == null || chosen.getTransformedComposition() == null) throw new IllegalArgumentException("NPC unavailable");
            NPCComposition comp = chosen.getTransformedComposition();
            int option = -1;
            for (int i=0;i<comp.getActions().length;i++) if (action.equals(comp.getActions()[i])) option=i;
            if (option < 0 || !(action.equals("Exchange") && comp.getName().equalsIgnoreCase("Grand Exchange Clerk")
                    || action.equals("Bank") && comp.getName().equalsIgnoreCase("Banker"))) throw new IllegalArgumentException("Unsupported NPC action");
            MenuAction[] options = {MenuAction.NPC_FIRST_OPTION,MenuAction.NPC_SECOND_OPTION,MenuAction.NPC_THIRD_OPTION,MenuAction.NPC_FOURTH_OPTION,MenuAction.NPC_FIFTH_OPTION};
            client.menuAction(0,0,options[option],index,-1,action,comp.getName());
            receipt.put("status","dispatched"); io.submit(() -> atomic("input-receipt.json",receipt)); return;
        }
        if (type.equals("widget_action")) {
            String action = command.get("action").getAsString(); int option = -1;
            boolean inventoryAction=(target.getId() >>> 16)==149 && target.getItemId()>0;
            String[] actions=inventoryAction ? itemManager.getItemComposition(target.getItemId()).getInventoryActions() : target.getActions();
            if (actions != null) for(int i=0;i<actions.length;i++) if(action.equals(actions[i])) option=i;
            if (option < 0 || !(action.equals("Withdraw-X") || action.equals("Deposit-X") || action.equals("Rub") || action.equals("Grand Exchange"))) throw new IllegalArgumentException("Unsupported widget action");
            MenuAction menuAction=MenuAction.CC_OP;
            int identifier=option+1;
            if(inventoryAction) {
                MenuAction[] invOptions={MenuAction.WIDGET_FIRST_OPTION,MenuAction.WIDGET_SECOND_OPTION,MenuAction.WIDGET_THIRD_OPTION,MenuAction.WIDGET_FOURTH_OPTION,MenuAction.WIDGET_FIFTH_OPTION};
                menuAction=invOptions[option];identifier=target.getItemId();
            }
            client.menuAction(target.getIndex(),target.getId(),menuAction,identifier,target.getItemId(),action,target.getName());
            receipt.put("status","dispatched"); io.submit(() -> atomic("input-receipt.json",receipt)); return;
        }
        Rectangle bounds = target == null ? null : target.getBounds();
        Point origin = scoped ? null : canvas.getLocationOnScreen();
        inputBusy.set(true);
        inputIo.submit(() -> {
            try {
                if(pointerTiming!=null) {
                    dispatchPointer(canvas,window,command,receipt,pointerTiming,pointerTarget,scoped);
                    receipt.put("status","dispatched");return;
                }
                if (scoped) {
                    dispatchScoped(canvas,bounds,command,receipt,characterTiming,textEditTiming,keyHoldTiming,textAccount,textPrompt);
                    receipt.put("status","dispatched"); return;
                }
                if (!window.isActive() || !canvas.isFocusOwner()) throw new IllegalStateException("Focus changed");
                Robot robot = new Robot();
                robot.setAutoDelay(35);
                if (type.equals("click") || type.equals("bank_pin")) {
                    robot.mouseMove(origin.x + bounds.x + bounds.width / 2, origin.y + bounds.y + bounds.height / 2);
                    try {robot.mousePress(InputEvent.BUTTON1_DOWN_MASK);}
                    finally {robot.mouseRelease(InputEvent.BUTTON1_DOWN_MASK);}
                } else if (type.equals("text")) {
                    String text = command.get("text").getAsString();
                    // Game prompts do not consistently support desktop Ctrl+A selection.
                    robot.setAutoDelay(5);
                    for (int clear = 0; clear < textEditTiming.clearKeyCount(); clear++) {
                        checkTypingContext(command,textAccount,textPrompt);
                        if (!window.isActive() || !canvas.isFocusOwner()) throw new IllegalStateException("Focus changed during clearing");
                        robotTap(robot,KeyEvent.VK_BACK_SPACE);
                    }
                    for (int i=0; i<text.length(); i++) {
                        checkTypingContext(command,textAccount,textPrompt);
                        if (!window.isActive() || !canvas.isFocusOwner()) throw new IllegalStateException("Focus changed during typing");
                        int keyHold=keyHoldTiming.keyHoldAt(i);
                        robot.setAutoDelay(keyHold<0 ? characterTiming.delayAt(i) : 0);
                        char ch = text.charAt(i);
                        int key = KeyEvent.getExtendedKeyCodeForChar(Character.toUpperCase(ch));
                        boolean shift = Character.isUpperCase(ch) || ch == '(' || ch == ')';
                        if (ch == '(') key = KeyEvent.VK_9;
                        if (ch == ')') key = KeyEvent.VK_0;
                        try {
                            if (shift) robot.keyPress(KeyEvent.VK_SHIFT);
                            if(keyHold<0) robotTap(robot,key);
                            else {
                                final int heldKey=key;
                                InputHoldTiming.held(() -> robot.keyPress(heldKey),() -> robot.keyRelease(heldKey),keyHold,
                                        () -> checkCancelled(command),Thread::sleep);
                            }
                        } finally {
                            if (shift) robot.keyRelease(KeyEvent.VK_SHIFT);
                        }
                        if(keyHold>=0) InputHoldTiming.pause((shift ? 4 : 2)*characterTiming.delayAt(i),() -> checkCancelled(command),Thread::sleep);
                    }
                    if (command.has("enter") && command.get("enter").getAsBoolean()) {
                        pauseBeforeEnter(command,textAccount,textPrompt,textEditTiming);
                        if (!window.isActive() || !canvas.isFocusOwner()) throw new IllegalStateException("Focus changed before Enter");
                        if(keyHoldTiming.enterHoldMs()<0) {
                            robot.setAutoDelay(characterTiming.delayAt(text.length()-1));robotTap(robot,KeyEvent.VK_ENTER);
                        } else {
                            robot.setAutoDelay(0);
                            InputHoldTiming.held(() -> robot.keyPress(KeyEvent.VK_ENTER),() -> robot.keyRelease(KeyEvent.VK_ENTER),keyHoldTiming.enterHoldMs(),
                                    () -> checkCancelled(command),Thread::sleep);
                            InputHoldTiming.pause(2*characterTiming.delayAt(text.length()-1),() -> checkCancelled(command),Thread::sleep);
                        }
                    }
                    receipt.put("key_delay_ms", characterTiming.scalarDelay());
                    receipt.put("clear_key_count",textEditTiming.clearKeyCount());
                    receipt.put("enter_pause_ms",textEditTiming.enterPauseMs());
                    receipt.put("key_hold_total_ms",keyHoldTiming.totalHoldMs());
                    receipt.put("typing_planned_delay_ms", characterTiming.robotDurationMs(command.has("enter") && command.get("enter").getAsBoolean(),textEditTiming.clearKeyCount(),textEditTiming.enterPauseMs())+keyHoldTiming.totalHoldMs());
                } else throw new IllegalArgumentException("Unsupported command type");
                receipt.put("status", "dispatched");
            } catch (Exception ex) {
                receipt.put("status", "rejected"); receipt.put("reason", ex instanceof PointerNotDispatched ? "Pointer click not dispatched" : ex.getClass().getSimpleName());
            } finally {
                atomic("input-receipt.json", receipt);
                inputBusy.set(false);
            }
        });
    }

    private boolean cancelled(String id) {
        try {
            JsonObject c=gson.fromJson(Files.readString(folder.resolve("input-cancel.json")),JsonObject.class);
            return session.equals(c.get("session").getAsString()) && id.equals(c.get("id").getAsString());
        } catch(Exception ex) { return false; }
    }

    private CharacterTiming characterTiming(JsonObject command,boolean scoped) {
        String text=command.get("text").getAsString();
        if ((scoped || command.has("key_delays_ms")) && !command.has("key_delay_ms")) {
            throw new IllegalArgumentException("Scalar typing delay required");
        }
        int scalar=command.has("key_delay_ms") ? command.get("key_delay_ms").getAsInt() : 90;
        List<String> delays=null;
        if(command.has("key_delays_ms")) {
            if(!command.get("key_delays_ms").isJsonArray()) throw new IllegalArgumentException("Character timing array required");
            delays=new ArrayList<>();
            for(JsonElement value:command.getAsJsonArray("key_delays_ms")) {
                if(!value.isJsonPrimitive() || !value.getAsJsonPrimitive().isNumber()) throw new IllegalArgumentException("Character timing number required");
                delays.add(value.getAsString());
            }
        }
        return new CharacterTiming(text,scalar,delays);
    }

    private String optionalIntegerToken(JsonObject command,String key) {
        if(!command.has(key)) return null;
        JsonElement value=command.get(key);
        if(!value.isJsonPrimitive() || !value.getAsJsonPrimitive().isNumber()) throw new IllegalArgumentException("Integer timing required");
        return value.getAsString();
    }

    private InputHoldTiming keyHoldTiming(JsonObject command) {
        if(!command.get("type").getAsString().equals("text")) throw new IllegalArgumentException("Text command required");
        String text=command.get("text").getAsString();
        List<String> keys=null;
        if(command.has("key_hold_ms")) {
            if(!command.get("key_hold_ms").isJsonArray()) throw new IllegalArgumentException("Key hold array required");
            keys=new ArrayList<>();
            for(JsonElement value:command.getAsJsonArray("key_hold_ms")) {
                if(!value.isJsonPrimitive() || !value.getAsJsonPrimitive().isNumber()) throw new IllegalArgumentException("Integer key hold required");
                keys.add(value.getAsString());
            }
        }
        boolean enter=command.has("enter") && command.get("enter").isJsonPrimitive()
                && command.get("enter").getAsJsonPrimitive().isBoolean() && command.get("enter").getAsBoolean();
        return new InputHoldTiming(text.length(),keys,optionalIntegerToken(command,"enter_hold_ms"),enter);
    }

    private PointerTiming pointerTiming(JsonObject command,Rectangle bounds,int width,int height) {
        if(!command.has("pointer_plan")) return null;
        if(!command.get("pointer_plan").isJsonObject()) throw new IllegalArgumentException("Pointer object required");
        JsonObject plan=command.getAsJsonObject("pointer_plan");
        JsonElement x=plan.get("end_x_fraction"),y=plan.get("end_y_fraction");
        if(x==null || y==null || !x.isJsonPrimitive() || !y.isJsonPrimitive()
                || !x.getAsJsonPrimitive().isNumber() || !y.getAsJsonPrimitive().isNumber()) throw new IllegalArgumentException("Numeric pointer fractions required");
        PointerTiming result=new PointerTiming(x.getAsDouble(),y.getAsDouble(),optionalIntegerToken(plan,"duration_ms"),
                optionalIntegerToken(plan,"steps"),optionalIntegerToken(plan,"hold_ms"));
        PointerTiming.validateBounds(bounds,width,height);
        return result;
    }

    private void checkCancelled(JsonObject command) throws InterruptedException {
        if(cancelled(command.get("id").getAsString()) || Thread.currentThread().isInterrupted()) throw new InterruptedException("Input cancelled");
    }

    private static final class PointerNotDispatched extends Exception {
        PointerNotDispatched(Exception cause) { super("Pointer click not dispatched",cause); }
    }

    private Map<String,String> setupSignature() {
        Map<String,String> signature=new HashMap<>();
        for(Widget widget:widgets.values()) {
            if(widget.getId()==InterfaceID.GeOffers.SETUP && (widget.getIndex()==34 || widget.getIndex()==41 || widget.getItemId()>0)) {
                signature.put(widget.getId()+":"+widget.getIndex(),widget.getItemId()+"|"+widget.getText()+"|"+widget.getName());
            }
        }
        return signature;
    }

    private final class PointerTarget {
        final Canvas canvas;
        final Widget widget;
        final Rectangle bounds;
        final int width,height,id,index,itemId;
        final String key,name,text;
        final String[] actions;
        final long account;
        final Point origin;
        final Map<String,String> setup;
        PointerTarget(Canvas canvas,Widget widget,long account,Point origin) {
            this.canvas=canvas;this.widget=widget;this.bounds=new Rectangle(widget.getBounds());
            width=canvas.getWidth();height=canvas.getHeight();id=widget.getId();index=widget.getIndex();itemId=widget.getItemId();
            key=id+":"+index;name=widget.getName();text=widget.getText();
            actions=widget.getActions()==null ? null : widget.getActions().clone();
            this.account=account;this.origin=origin==null ? null : new Point(origin);setup=setupSignature();
        }
    }

    private Point checkPointerContext(JsonObject command,PointerTarget target) throws Exception {
        return checkPointerContext(command,target,null);
    }

    private Point checkPointerContext(JsonObject command,PointerTarget target,PointerTiming.Lifetime lifetime) throws Exception {
        checkCancelled(command);
        if(lifetime!=null) lifetime.check(System.nanoTime());
        FutureTask<Point> guard=new FutureTask<>(() -> {
            Widget current=widgets.get(target.key);
            if(!Boolean.getBoolean("flip.allowInput") || !session.equals(command.get("session").getAsString())
                    || client.getGameState()!=GameState.LOGGED_IN || client.getAccountHash()!=target.account
                    || client.getCanvas()!=target.canvas || target.canvas.getWidth()!=target.width || target.canvas.getHeight()!=target.height
                    || current!=target.widget || current.isHidden() || current.getId()!=target.id || current.getIndex()!=target.index
                    || current.getItemId()!=target.itemId || !Objects.equals(current.getName(),target.name)
                    || !Objects.equals(current.getText(),target.text) || !Arrays.equals(current.getActions(),target.actions)
                    || !current.getBounds().equals(target.bounds) || !setupSignature().equals(target.setup)
                    || target.origin!=null && !target.canvas.getLocationOnScreen().equals(target.origin)) {
                throw new IllegalStateException("Pointer target/context changed");
            }
            int depth=0;
            for(Widget parent=current.getParent();parent!=null;parent=parent.getParent()) {
                if(++depth>16) throw new IllegalStateException("Pointer ancestry uncertain");
                if(parent.isHidden()) throw new IllegalStateException("Pointer target hidden");
            }
            net.runelite.api.Point mouse=client.getMouseCanvasPosition();
            if(mouse==null) throw new IllegalStateException("Canvas pointer unavailable");
            return new Point(mouse.getX(),mouse.getY());
        });
        clientThread.invokeLater((Runnable)guard);
        long allowance=TimeUnit.SECONDS.toNanos(2);
        if(lifetime!=null) allowance=Math.min(allowance,lifetime.remainingNanos(System.nanoTime()));
        long deadline=System.nanoTime()+allowance;
        try {
            while(true) {
                try {
                    Point mouse=guard.get(25,TimeUnit.MILLISECONDS);checkCancelled(command);
                    if(lifetime!=null) lifetime.check(System.nanoTime());return mouse;
                } catch(TimeoutException ex) {
                    checkCancelled(command);if(lifetime!=null) lifetime.check(System.nanoTime());
                    if(System.nanoTime()>=deadline) throw ex;
                }
            }
        } finally {if(!guard.isDone()) guard.cancel(false);}
    }

    private void waitPointerDeadline(long deadline,JsonObject command) throws Exception {
        while(true) {
            checkCancelled(command);
            long remaining=deadline-System.nanoTime();
            if(remaining<=0) return;
            Thread.sleep(Math.min(25,Math.max(1,TimeUnit.NANOSECONDS.toMillis(remaining))));
        }
    }

    private void invokeRelease(Runnable release) throws Exception {
        boolean interrupted=Thread.interrupted();
        try {SwingUtilities.invokeAndWait(release);}
        finally {if(interrupted) Thread.currentThread().interrupt();}
    }

    private void scopedMove(Canvas canvas,Point point) throws Exception {
        SwingUtilities.invokeAndWait(() -> {
            MouseEvent event=new MouseEvent(canvas,MouseEvent.MOUSE_MOVED,System.currentTimeMillis(),0,point.x,point.y,0,false);
            for(MouseMotionListener listener:canvas.getMouseMotionListeners()) listener.mouseMoved(event);
        });
    }

    private void dispatchPointer(Canvas canvas,Window window,JsonObject command,Map<String,Object> receipt,PointerTiming timing,PointerTarget target,boolean scoped) throws Exception {
        PointerTiming.PressGate pressGate=new PointerTiming.PressGate();
        PointerTiming.Lifetime lifetime=new PointerTiming.Lifetime(System.nanoTime());
        try {
            Point start=checkPointerContext(command,target,lifetime);
            Robot robot=scoped ? null : new Robot();
            if(!scoped) {
                if(window==null || !window.isActive() || !canvas.isFocusOwner()) throw new IllegalStateException("Focus changed");
                java.awt.PointerInfo actual=MouseInfo.getPointerInfo();
                if(actual==null) throw new IllegalStateException("Pointer unavailable");
                start=actual.getLocation();start.translate(-target.origin.x,-target.origin.y);
                robot.setAutoDelay(0);
            }
            Point[] path=timing.path(start,target.bounds,target.width,target.height);
            long began=System.nanoTime();
            for(int step=0;step<path.length;step++) {
                waitPointerDeadline(began+TimeUnit.MILLISECONDS.toNanos(timing.deadlineMs(step)),command);
                checkPointerContext(command,target,lifetime);
                if(scoped) scopedMove(canvas,path[step]);
                else {
                    if(!window.isActive() || !canvas.isFocusOwner()) throw new IllegalStateException("Focus changed");
                    robot.mouseMove(target.origin.x+path[step].x,target.origin.y+path[step].y);
                }
            }
            Point actualCanvas=checkPointerContext(command,target,lifetime);
            Point end=path[path.length-1];
            if(scoped) {
                if(!actualCanvas.equals(end)) throw new IllegalStateException("Canvas pointer changed before press");
                InputHoldTiming.held(() -> {
                    try {SwingUtilities.invokeAndWait(() -> {
                        // A queued EDT event must never press after the prepress lifetime.
                        lifetime.check(System.nanoTime());
                        try {checkCancelled(command);}catch(InterruptedException ex){throw new IllegalStateException("Input cancelled before press",ex);}
                        if(!pressGate.beginPress()) throw new IllegalStateException("Queued pointer press cancelled");
                        MouseEvent event=new MouseEvent(canvas,MouseEvent.MOUSE_PRESSED,System.currentTimeMillis(),InputEvent.BUTTON1_DOWN_MASK,end.x,end.y,1,false,MouseEvent.BUTTON1);
                        for(MouseListener listener:canvas.getMouseListeners()) listener.mousePressed(event);
                    });}catch(Exception ex){pressGate.preventFuturePress();throw ex;}
                },() -> {
                    if(!pressGate.wasAttempted()) return;
                    invokeRelease(() -> {
                    MouseEvent event=new MouseEvent(canvas,MouseEvent.MOUSE_RELEASED,System.currentTimeMillis(),0,end.x,end.y,1,false,MouseEvent.BUTTON1);
                    for(MouseListener listener:canvas.getMouseListeners()) listener.mouseReleased(event);
                    });
                },timing.holdMs(),() -> checkCancelled(command),Thread::sleep);
            } else {
                if(!window.isActive() || !canvas.isFocusOwner()) throw new IllegalStateException("Focus changed");
                java.awt.PointerInfo actual=MouseInfo.getPointerInfo();
                if(actual==null || !actual.getLocation().equals(new Point(target.origin.x+end.x,target.origin.y+end.y))) throw new IllegalStateException("Pointer changed before press");
                InputHoldTiming.held(() -> {
                    lifetime.check(System.nanoTime());checkCancelled(command);
                    if(!pressGate.beginPress()) throw new IllegalStateException("Pointer press cancelled");
                    robot.mousePress(InputEvent.BUTTON1_DOWN_MASK);
                },
                        () -> robot.mouseRelease(InputEvent.BUTTON1_DOWN_MASK),timing.holdMs(),() -> checkCancelled(command),Thread::sleep);
            }
            receipt.put("pointer_duration_ms",timing.durationMs());receipt.put("pointer_steps",timing.steps());receipt.put("pointer_hold_ms",timing.holdMs());
            receipt.put("pointer_end_x",end.x);receipt.put("pointer_end_y",end.y);
            receipt.put("input_planned_delay_ms",timing.durationMs()+timing.holdMs());
            if(scoped) receipt.put("input_mode","SCOPED_CANVAS");
        } catch(Exception ex) {
            pressGate.preventFuturePress();
            if(!pressGate.wasAttempted()) throw new PointerNotDispatched(ex);
            throw ex;
        }
    }

    private TextEditTiming textEditTiming(JsonObject command,int observedLength) {
        if(!command.get("type").getAsString().equals("text")) throw new IllegalArgumentException("Text command required");
        boolean enter=command.has("enter") && command.get("enter").isJsonPrimitive()
                && command.get("enter").getAsJsonPrimitive().isBoolean() && command.get("enter").getAsBoolean();
        return new TextEditTiming(optionalIntegerToken(command,"clear_key_count"),optionalIntegerToken(command,"enter_pause_ms"),enter,observedLength);
    }

    private void pauseBeforeEnter(JsonObject command,long account,int prompt,TextEditTiming timing) throws Exception {
        checkTypingContext(command,account,prompt);
        String id=command.get("id").getAsString();
        timing.pauseBeforeEnter(() -> {
            if(cancelled(id) || Thread.currentThread().isInterrupted()) throw new InterruptedException("Input cancelled");
        },Thread::sleep);
        checkTypingContext(command,account,prompt);
    }

    private void checkTypingContext(JsonObject command,long account,int prompt) throws Exception {
        String id=command.get("id").getAsString();
        if(cancelled(id) || Thread.currentThread().isInterrupted()) throw new InterruptedException("Input cancelled");
        // Client state is accessed only on the RuneLite client thread.
        FutureTask<Void> guard=new FutureTask<>(() -> {
            if(!Boolean.getBoolean("flip.allowInput") || !session.equals(command.get("session").getAsString())
                    || client.getGameState()!=GameState.LOGGED_IN || client.getAccountHash()!=account
                    || prompt==0 || client.getVarcIntValue(VarClientInt.INPUT_TYPE)!=prompt) {
                throw new IllegalStateException("Game text context changed");
            }
            return null;
        });
        clientThread.invokeLater((Runnable)guard);
        try { guard.get(2,TimeUnit.SECONDS); }
        finally { if(!guard.isDone()) guard.cancel(false); }
        if(cancelled(id) || Thread.currentThread().isInterrupted()) throw new InterruptedException("Input cancelled");
    }

    private void robotTap(Robot robot,int key) {
        try { robot.keyPress(key); }
        finally { robot.keyRelease(key); }
    }

    private void scopedKey(Canvas canvas,int key,char ch) throws Exception {
        SwingUtilities.invokeAndWait(() -> {
            KeyEvent pressed=new KeyEvent(canvas,KeyEvent.KEY_PRESSED,System.currentTimeMillis(),0,key,ch);
            try {
                for(KeyListener k:canvas.getKeyListeners()) k.keyPressed(pressed);
                if(ch!=KeyEvent.CHAR_UNDEFINED) {
                    KeyEvent typed=new KeyEvent(canvas,KeyEvent.KEY_TYPED,System.currentTimeMillis(),0,KeyEvent.VK_UNDEFINED,ch);
                    for(KeyListener k:canvas.getKeyListeners()) k.keyTyped(typed);
                }
            } finally {
                KeyEvent released=new KeyEvent(canvas,KeyEvent.KEY_RELEASED,System.currentTimeMillis(),0,key,ch);
                for(KeyListener k:canvas.getKeyListeners()) k.keyReleased(released);
            }
        });
    }

    private void scopedHeldKey(Canvas canvas,int key,char ch,int hold,JsonObject command) throws Exception {
        if(hold<0) {scopedKey(canvas,key,ch);return;}
        InputHoldTiming.held(() -> SwingUtilities.invokeAndWait(() -> {
            KeyEvent pressed=new KeyEvent(canvas,KeyEvent.KEY_PRESSED,System.currentTimeMillis(),0,key,ch);
            for(KeyListener listener:canvas.getKeyListeners()) listener.keyPressed(pressed);
            KeyEvent typed=new KeyEvent(canvas,KeyEvent.KEY_TYPED,System.currentTimeMillis(),0,KeyEvent.VK_UNDEFINED,ch);
            for(KeyListener listener:canvas.getKeyListeners()) listener.keyTyped(typed);
        }),() -> invokeRelease(() -> {
            KeyEvent released=new KeyEvent(canvas,KeyEvent.KEY_RELEASED,System.currentTimeMillis(),0,key,ch);
            for(KeyListener listener:canvas.getKeyListeners()) listener.keyReleased(released);
        }),hold,() -> checkCancelled(command),Thread::sleep);
    }

    private void dispatchScoped(Canvas canvas,Rectangle bounds,JsonObject command,Map<String,Object> receipt,CharacterTiming timing,TextEditTiming textEditTiming,InputHoldTiming keyHoldTiming,long account,int prompt) throws Exception {
        String id=command.get("id").getAsString();
        if(cancelled(id)) throw new InterruptedException("Input cancelled");
        if(command.get("type").getAsString().equals("click") || command.get("type").getAsString().equals("bank_pin")) {
            int x=bounds.x+bounds.width/2,y=bounds.y+bounds.height/2;
            SwingUtilities.invokeAndWait(() -> {
                MouseEvent moved=new MouseEvent(canvas,MouseEvent.MOUSE_MOVED,System.currentTimeMillis(),0,x,y,0,false);
                for(MouseMotionListener m:canvas.getMouseMotionListeners()) m.mouseMoved(moved);
                MouseEvent pressed=new MouseEvent(canvas,MouseEvent.MOUSE_PRESSED,System.currentTimeMillis(),InputEvent.BUTTON1_DOWN_MASK,x,y,1,false,MouseEvent.BUTTON1);
                try {for(MouseListener m:canvas.getMouseListeners()) m.mousePressed(pressed);}
                finally {
                    MouseEvent released=new MouseEvent(canvas,MouseEvent.MOUSE_RELEASED,System.currentTimeMillis(),0,x,y,1,false,MouseEvent.BUTTON1);
                    for(MouseListener m:canvas.getMouseListeners()) m.mouseReleased(released);
                }
            });
        } else if(command.get("type").getAsString().equals("text")) {
            String text=command.get("text").getAsString();
            for(int i=0;i<textEditTiming.clearKeyCount();i++) {
                checkTypingContext(command,account,prompt);
                scopedKey(canvas,KeyEvent.VK_BACK_SPACE,'\b');Thread.sleep(10);
            }
            for(int i=0;i<text.length();i++) {
                checkTypingContext(command,account,prompt);
                char ch=text.charAt(i);
                scopedHeldKey(canvas,KeyEvent.getExtendedKeyCodeForChar(ch),ch,keyHoldTiming.keyHoldAt(i),command); Thread.sleep(timing.delayAt(i)*2L);
            }
            if(command.has("enter") && command.get("enter").getAsBoolean()) {
                pauseBeforeEnter(command,account,prompt,textEditTiming);
                scopedHeldKey(canvas,KeyEvent.VK_ENTER,'\n',keyHoldTiming.enterHoldMs(),command);
            }
            receipt.put("key_delay_ms",timing.scalarDelay());
            receipt.put("clear_key_count",textEditTiming.clearKeyCount());
            receipt.put("enter_pause_ms",textEditTiming.enterPauseMs());
            receipt.put("key_hold_total_ms",keyHoldTiming.totalHoldMs());
            receipt.put("typing_planned_delay_ms",timing.scopedDurationMs(textEditTiming.clearKeyCount(),textEditTiming.enterPauseMs())+keyHoldTiming.totalHoldMs());
        } else throw new IllegalArgumentException("Unsupported scoped input");
        receipt.put("input_mode","SCOPED_CANVAS");
    }
}
