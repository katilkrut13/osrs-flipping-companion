using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.Versioning;

namespace OsrsFlipping.Core;

public sealed class ConnectionUnavailable(string message, Exception? inner = null) : Exception(message, inner);

public static class Storage
{
    public static JsonNode ReadJson(string path) => JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF'))
        ?? throw new InvalidDataException("Empty JSON: " + Path.GetFileName(path));
    public static void WriteJson(string path, JsonNode? value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(value?.ToJsonString(J.Options) ?? "null");
                file.Write(bytes); file.Flush(true);
            }
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temp, path, true); break; }
                catch (IOException) when (attempt < 5) { Thread.Sleep(20 * (attempt + 1)); }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static JsonObject ReadSnapshot(string path, long? now = null, bool requireGe = true, bool requireLoggedIn = true)
    {
        JsonObject state;
        try { state = ReadJson(path) as JsonObject ?? throw new InvalidDataException("Snapshot must be an object"); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { throw new ConnectionUnavailable("Game snapshot temporarily unavailable", ex); }
        var age = (now ?? J.Now) - state.L("observed_at");
        if (state.L("schema") != 1 || age < 0 || age > 5) throw new ConnectionUnavailable("Game snapshot missing, stale, or incompatible");
        if (requireLoggedIn && (!state.B("logged_in") || state.S("account_key") == "")) throw new ConnectionUnavailable("Log in to the game");
        if (requireGe && !state.B("ge_open")) throw new ConnectionUnavailable("Open the GE offer interface");
        return state;
    }
}

public static class CommonStorage
{
    public static JsonNode ReadJson(string path) => Storage.ReadJson(path);
    public static void WriteJson(string path, JsonNode? value) => Storage.WriteJson(path, value);
    public static JsonObject ReadSnapshot(string path, long? now = null, bool requireGe = true, bool requireLoggedIn = true)
        => Storage.ReadSnapshot(path, now, requireGe, requireLoggedIn);
}

[SupportedOSPlatform("windows")]
public sealed class InstanceLock : IDisposable
{
    private FileStream? stream;
    public bool Acquire(string folder)
    {
        Directory.CreateDirectory(folder);
        try
        {
            stream = new FileStream(Path.Combine(folder, "app.instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (stream.Length == 0) { stream.WriteByte(32); stream.Flush(); }
            stream.Lock(0, 1); return true;
        }
        catch (IOException) { stream?.Dispose(); stream = null; return false; }
    }
    public void Dispose() { if (stream is null) return; stream.Unlock(0, 1); stream.Dispose(); stream = null; }
}
