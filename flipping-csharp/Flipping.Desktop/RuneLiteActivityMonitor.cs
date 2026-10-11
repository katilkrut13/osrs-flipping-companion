using System.ComponentModel;
using System.Runtime.InteropServices;
using OsrsFlipping.Core;

namespace OsrsFlipping.Desktop;

/// <summary>
/// Passive Windows hooks. Only physical activity directed at an explicitly bound
/// RuneLite process is reported. Key values, typed text and coordinates are never
/// retained or exposed. Construct/dispose on the WinForms message-loop thread.
/// </summary>
internal sealed class RuneLiteActivityMonitor : IDisposable
{
    const int WhKeyboardLowLevel = 13, WhMouseLowLevel = 14;
    const int MouseMove = 0x0200;
    readonly HookProcedure keyboardProcedure, mouseProcedure;
    nint keyboardHook, mouseHook;
    int targetProcessId;
    bool disposed;

    public event Action? PhysicalActivity;
    public bool IsOperational => keyboardHook != 0 && mouseHook != 0 && !disposed;
    public bool IsBound => Volatile.Read(ref targetProcessId) > 0;
    public string? Error { get; private set; }

    public RuneLiteActivityMonitor()
    {
        keyboardProcedure = KeyboardCallback; mouseProcedure = MouseCallback;
        var module = GetModuleHandle(null);
        keyboardHook = SetWindowsHookEx(WhKeyboardLowLevel, keyboardProcedure, module, 0);
        if (keyboardHook == 0) { Error = "Keyboard activity monitor: " + new Win32Exception(Marshal.GetLastWin32Error()).Message; return; }
        mouseHook = SetWindowsHookEx(WhMouseLowLevel, mouseProcedure, module, 0);
        if (mouseHook == 0)
        {
            Error = "Mouse activity monitor: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
            UnhookWindowsHookEx(keyboardHook); keyboardHook = 0;
        }
    }

    // The owner validates the PID against the observed bridge or the exact
    // RuneLite executable path. No process-name or title guesses happen here.
    public void BindProcess(int? processId) => Volatile.Write(ref targetProcessId, Math.Max(0, processId.GetValueOrDefault()));

    nint KeyboardCallback(int code, nuint message, nint data)
    {
        try
        {
            if (code >= 0 && PhysicalActivityRules.Keyboard(IsTarget(GetForegroundWindow()), (uint)Marshal.ReadInt32(data, 8)))
                PhysicalActivity?.Invoke();
        }
        catch (Exception e) { Error = "Activity callback failed: " + e.GetType().Name; }
        return CallNextHookEx(keyboardHook, code, message, data);
    }

    nint MouseCallback(int code, nuint message, nint data)
    {
        try
        {
            if (code >= 0 && IsBound)
            {
                var point = Marshal.PtrToStructure<NativePoint>(data);
                var isMovement = (int)message == MouseMove;
                if (PhysicalActivityRules.Mouse(IsTarget(WindowFromPoint(point)), IsTarget(GetForegroundWindow()),
                    isMovement, (uint)Marshal.ReadInt32(data, 12)))
                    PhysicalActivity?.Invoke();
            }
        }
        catch (Exception e) { Error = "Activity callback failed: " + e.GetType().Name; }
        return CallNextHookEx(mouseHook, code, message, data);
    }

    bool IsTarget(nint window)
    {
        var expected = Volatile.Read(ref targetProcessId);
        if (expected <= 0 || window == 0) return false;
        GetWindowThreadProcessId(window, out var owner);
        return owner == (uint)expected;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; BindProcess(null);
        if (keyboardHook != 0) { UnhookWindowsHookEx(keyboardHook); keyboardHook = 0; }
        if (mouseHook != 0) { UnhookWindowsHookEx(mouseHook); mouseHook = 0; }
        PhysicalActivity = null;
        GC.KeepAlive(keyboardProcedure); GC.KeepAlive(mouseProcedure);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint { public int X, Y; }
    delegate nint HookProcedure(int code, nuint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] static extern nint SetWindowsHookEx(int hook, HookProcedure callback, nint module, uint threadId);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint GetModuleHandle(string? moduleName);
}
