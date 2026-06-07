using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace WinNewsWire.AppWindows;

internal static class WindowIconHelper
{
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint LR_DEFAULTCOLOR = 0x0000;
    private const uint WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const int SM_CXICON = 11;
    private const int SM_CYICON = 12;
    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string lpszName, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public static void ApplyFlatIcon(Window window)
    {
        try
        {
            var hWnd = WindowNative.GetWindowHandle(window);
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "wnw-flat.ico");
            SetWindowIconFromIco(hWnd, iconPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ApplyFlatIcon failed: {ex.Message}");
        }
    }

    public static void SetWindowIconFromIco(IntPtr hWnd, string icoPath)
    {
        if (!System.IO.File.Exists(icoPath)) return;

        var bigIcon = LoadImageW(IntPtr.Zero, icoPath, IMAGE_ICON,
            GetSystemMetrics(SM_CXICON), GetSystemMetrics(SM_CYICON),
            LR_LOADFROMFILE | LR_DEFAULTCOLOR);
        var smallIcon = LoadImageW(IntPtr.Zero, icoPath, IMAGE_ICON,
            GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON),
            LR_LOADFROMFILE | LR_DEFAULTCOLOR);

        if (bigIcon != IntPtr.Zero)
            SendMessageW(hWnd, WM_SETICON, (IntPtr)ICON_BIG, bigIcon);
        if (smallIcon != IntPtr.Zero)
            SendMessageW(hWnd, WM_SETICON, (IntPtr)ICON_SMALL, smallIcon);
    }
}
