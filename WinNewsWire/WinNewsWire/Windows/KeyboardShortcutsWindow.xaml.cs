using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace WinNewsWire.AppWindows;

public sealed partial class KeyboardShortcutsWindow : Window
{
    // The window starts compact and is capped at MaxWidth × MaxHeight via a
    // Win32 subclass that responds to WM_GETMINMAXINFO. Letting the OS resize
    // bigger than this defeats the purpose of the compact reference card.
    private const int DefaultWidth  = 460;
    private const int DefaultHeight = 560;
    private const int MinWidth      = 380;
    private const int MinHeight     = 360;
    private const int MaxWidth      = 600;
    private const int MaxHeight     = 760;

    private IntPtr _hwnd;
    private SUBCLASSPROC? _subclassProcDelegate;

    public KeyboardShortcutsWindow()
    {
        InitializeComponent();
        WindowIconHelper.ApplyFlatIcon(this);
        WindowThemeHelper.Attach(this);

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        // Initial size — modest reference-card footprint, not a full document.
        var dpi = GetDpiForWindow(_hwnd);
        double scale = dpi <= 0 ? 1.0 : dpi / 96.0;
        appWindow.Resize(new SizeInt32(
            (int)Math.Round(DefaultWidth  * scale),
            (int)Math.Round(DefaultHeight * scale)));

        // Subclass the HWND so we can intercept WM_GETMINMAXINFO and clamp
        // the user-resize range without disabling resize entirely.
        _subclassProcDelegate = SubclassProc;
        SetWindowSubclass(_hwnd, _subclassProcDelegate, IntPtr.Zero, IntPtr.Zero);

        Closed += (_, _) =>
        {
            if (_subclassProcDelegate is not null)
            {
                RemoveWindowSubclass(_hwnd, _subclassProcDelegate, IntPtr.Zero);
                _subclassProcDelegate = null;
            }
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // --- Win32 plumbing for size constraints ---

    private IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
                                IntPtr uIdSubclass, IntPtr dwRefData)
    {
        const uint WM_GETMINMAXINFO = 0x0024;
        if (uMsg == WM_GETMINMAXINFO)
        {
            var dpi = GetDpiForWindow(hWnd);
            double scale = dpi <= 0 ? 1.0 : dpi / 96.0;
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMinTrackSize = new POINT((int)Math.Round(MinWidth  * scale),
                                           (int)Math.Round(MinHeight * scale));
            mmi.ptMaxTrackSize = new POINT((int)Math.Round(MaxWidth  * scale),
                                           (int)Math.Round(MaxHeight * scale));
            Marshal.StructureToPtr(mmi, lParam, fDeleteOld: true);
            return IntPtr.Zero;
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
                                         IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("Comctl32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass,
                                                 IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("Comctl32.dll", CharSet = CharSet.Unicode)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass,
                                                    IntPtr uIdSubclass);

    [DllImport("Comctl32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
}

