using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinNewsWire;

public sealed partial class MainWindow
{
    private ClockWindowProc? _clockWindowProc;
    private nint _clockWindowHandle;

    private void InitializeStatusBarSystemMessages(nint handle)
    {
        _clockWindowProc = StatusBarWindowMessage;
        if (!SetWindowSubclass(handle, _clockWindowProc, 0, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not observe system clock and locale changes.");
        _clockWindowHandle = handle;
    }

    private void StopStatusBarSystemMessages()
    {
        if (_clockWindowHandle == 0 || _clockWindowProc is null) return;
        if (!RemoveWindowSubclass(_clockWindowHandle, _clockWindowProc, 0))
            App.LogException("Stop observing system clock changes",
                new Win32Exception(Marshal.GetLastWin32Error()));
        _clockWindowHandle = 0;
        _clockWindowProc = null;
    }

    private nint StatusBarWindowMessage(nint handle, uint message, nuint wParam, nint lParam,
        nuint subclassId, nuint referenceData)
    {
        // WM_SETTINGCHANGE, WM_TIMECHANGE and WM_POWERBROADCAST (including resume).
        if (!_closed && message is 0x001A or 0x001E or 0x0218)
            MainContentControl.InvalidateStatusBarClock();
        return DefSubclassProc(handle, message, wParam, lParam);
    }

    private delegate nint ClockWindowProc(nint handle, uint message, nuint wParam, nint lParam,
        nuint subclassId, nuint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint handle, ClockWindowProc callback, nuint id, nuint data);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(nint handle, ClockWindowProc callback, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint handle, uint message, nuint wParam, nint lParam);
}
