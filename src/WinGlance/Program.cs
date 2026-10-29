using System;
using System.Runtime.InteropServices;
using WinGlance.UI;

namespace WinGlance;

internal static class Program
{
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch { }

        string? initialPath = args.Length > 0 ? args[0] : null;

        using var window = new NativeGlanceWindow();
        window.Run(initialPath);
        return 0;
    }
}
