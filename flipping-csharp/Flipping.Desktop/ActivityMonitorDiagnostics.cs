using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using OsrsFlipping.Core;

namespace OsrsFlipping.Desktop;

internal static class RuneLiteProcessBinding
{
    public static int? Find(JsonObject? snapshot = null)
    {
        var suppliedId = snapshot?.L("process_id") ?? 0;
        if (suppliedId != 0)
        {
            if (suppliedId is < 1 or > int.MaxValue) return null;
            try
            {
                using var observed = Process.GetProcessById((int)suppliedId);
                return Valid(observed) ? observed.Id : null;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
        }
        var matches = new List<int>();
        foreach (var process in Process.GetProcessesByName("RuneLite"))
        {
            using (process)
            {
                try { if (Valid(process)) matches.Add(process.Id); }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
            }
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    static bool Valid(Process process)
    {
        if (process.HasExited || process.MainWindowHandle == 0
            || !process.ProcessName.Equals("RuneLite", StringComparison.OrdinalIgnoreCase)
            || !process.MainWindowTitle.StartsWith("RuneLite", StringComparison.OrdinalIgnoreCase)) return false;
        var expected = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RuneLite", "RuneLite.exe"));
        var executable = process.MainModule?.FileName;
        return !string.IsNullOrEmpty(executable) && Path.GetFullPath(executable).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class ActivityMonitorDiagnostics
{
    public static int Run(string output)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Activity monitor diagnostic requires an STA message loop.");
        var processId = RuneLiteProcessBinding.Find();
        using var monitor = new RuneLiteActivityMonitor();
        monitor.BindProcess(processId);
        using var context = new ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 200 };
        timer.Tick += (_, _) => { timer.Stop(); context.ExitThread(); };
        timer.Start();
        Application.Run(context);
        var operational = monitor.IsOperational;
        var bound = monitor.IsBound;
        var error = monitor.Error;
        monitor.Dispose();
        var result = J.O(("ok", operational && bound), ("hook_operational", operational), ("runelite_bound", bound),
            ("runelite_process_id", processId), ("binding", "EXACT_NATIVE_EXECUTABLE_UNIQUE_VISIBLE_WINDOW"),
            ("hooks_disposed", !monitor.IsOperational), ("error", error), ("live_game_inputs", false),
            ("synthetic_inputs", false), ("key_values_captured", false), ("typed_text_captured", false),
            ("pointer_coordinates_retained", false), ("physical_activity_retained", false), ("completed_at", J.Now));
        Storage.WriteJson(output, result);
        return result.B("ok") ? 0 : 1;
    }
}
