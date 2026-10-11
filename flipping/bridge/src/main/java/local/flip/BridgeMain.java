package local.flip;

import net.runelite.client.RuneLite;
import net.runelite.client.externalplugins.ExternalPluginManager;

public final class BridgeMain {
    public static void main(String[] args) throws Exception {
        ExternalPluginManager.loadBuiltin(FlipBridgePlugin.class);
        RuneLite.main(args);
    }
}
