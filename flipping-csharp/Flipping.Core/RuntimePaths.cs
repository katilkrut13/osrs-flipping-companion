using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public static class RuntimePaths
{
    public static string Resolve(string? explicitFolder = null)
    {
        if (!string.IsNullOrEmpty(explicitFolder)) return Path.GetFullPath(explicitFolder);
        var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RuneLite", "config.json");
        if (File.Exists(settings))
        {
            var cfg = Storage.ReadJson(settings).AsObject();
            if (cfg.S("mainClass") == "local.flip.BridgeMain")
            {
                var values = cfg.Arr("vmArgs").Select(n => n?.GetValue<string>() ?? "")
                    .Where(s => s.StartsWith("-Dflip.directory=", StringComparison.Ordinal))
                    .Select(s => s[17..].Trim().Trim('"')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (values.Length != 1 || !Path.IsPathFullyQualified(values[0])) throw new InvalidDataException("RuneLite veri klasörü doğrulanamadı.");
                var folder = Path.GetFullPath(values[0]);
                var jars = cfg.Arr("classPath").Select(n => n?.GetValue<string>() ?? "")
                    .Where(p => Path.IsPathFullyQualified(p) && Path.GetFileName(p).StartsWith("flip-bridge", StringComparison.OrdinalIgnoreCase)
                             && Path.GetExtension(p) == ".jar" && File.Exists(p)
                             && Path.GetFullPath(Path.GetDirectoryName(p)!) == Path.GetFullPath(Path.Combine(folder, "bridge/build/libs"))).ToArray();
                if (!File.Exists(Path.Combine(folder, "config.json")) || jars.Length != 1) throw new InvalidDataException("RuneLite köprüsü ve veri klasörü eşleşmiyor.");
                return folder;
            }
        }
        var adjacent = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (File.Exists(adjacent)) return AppContext.BaseDirectory;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsrsFlipping");
    }
    public static void Ensure(string folder)
    {
        Directory.CreateDirectory(folder);
        var configPath = Path.Combine(folder, "config.json");
        if (!File.Exists(configPath))
        {
            var source = Path.Combine(AppContext.BaseDirectory, "config.default.json");
            if (File.Exists(source)) File.Copy(source, configPath);
            else
            {
                using var resource = typeof(RuntimePaths).Assembly.GetManifestResourceStream("config.default.json")
                    ?? throw new FileNotFoundException("Başlangıç yapılandırması bulunamadı", source);
                using var target = File.Create(configPath); resource.CopyTo(target);
            }
        }
        var jar = Path.Combine(folder, "bridge/build/libs/flip-bridge.jar");
        if (!File.Exists(jar))
        {
            using var resource = typeof(RuntimePaths).Assembly.GetManifestResourceStream("flip-bridge.jar");
            if (resource != null) { Directory.CreateDirectory(Path.GetDirectoryName(jar)!); using var target = File.Create(jar); resource.CopyTo(target); }
        }
    }
}
