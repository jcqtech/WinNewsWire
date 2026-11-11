using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinNewsWire.Core;

public static class WindowsScreen
{
    public static int MaxIconPixelSize(double maximumScreenScale)
    {
        if (!double.IsFinite(maximumScreenScale) || maximumScreenScale <= 0 || maximumScreenScale > int.MaxValue / 48.0)
            throw new ArgumentOutOfRangeException(nameof(maximumScreenScale));
        return checked((int)Math.Ceiling(48 * maximumScreenScale));
    }

    [SupportedOSPlatform("windows")]
    public static double MaxScreenScale()
    {
        double maximum = 0;
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            int result = GetScaleFactorForMonitor(monitor, out int scale);
            if (result < 0) Trace.TraceWarning($"Could not read a monitor scale: 0x{result:X8}.");
            if (scale > 0) maximum = Math.Max(maximum, scale / 100.0);
            return true;
        }, IntPtr.Zero))
            Trace.TraceWarning($"Could not enumerate display scales: {Marshal.GetLastPInvokeError()}.");
        if (maximum > 0) return maximum;
        Trace.TraceWarning("No display scale is available; using the native 2x fallback.");
        return 2;
    }

    private delegate bool MonitorCallback(IntPtr monitor, IntPtr deviceContext, IntPtr rectangle, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("shcore.dll")]
    private static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale);
}
