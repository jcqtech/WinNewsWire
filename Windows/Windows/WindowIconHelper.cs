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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    public static void ApplyFlatIcon(Window window)
    {
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "wnw-flat.ico");
            SetWindowIconFromIco(window, iconPath, System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "wnw.ico"));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or System.Runtime.InteropServices.COMException)
        {
            App.LogException("Apply window icons", ex);
        }
    }

    public static void SetWindowIconFromIco(Window window, string icoPath, string taskbarIcoPath)
    {
        var hWnd = WindowNative.GetWindowHandle(window);
        if (!System.IO.File.Exists(icoPath)) throw new System.IO.FileNotFoundException("The window icon is missing.", icoPath);
        if (!System.IO.File.Exists(taskbarIcoPath)) throw new System.IO.FileNotFoundException("The taskbar icon is missing.", taskbarIcoPath);

        var bigIcon = LoadImageW(IntPtr.Zero, taskbarIcoPath, IMAGE_ICON,
            GetSystemMetrics(SM_CXICON), GetSystemMetrics(SM_CYICON),
            LR_LOADFROMFILE | LR_DEFAULTCOLOR);
        if (bigIcon == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not load the taskbar icon.");
        var smallIcon = LoadImageW(IntPtr.Zero, icoPath, IMAGE_ICON,
            GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON),
            LR_LOADFROMFILE | LR_DEFAULTCOLOR);
        if (smallIcon == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            DestroyIcon(bigIcon);
            throw new System.ComponentModel.Win32Exception(error, "Could not load the small window icon.");
        }
        SendMessageW(hWnd, WM_SETICON, (IntPtr)ICON_BIG, bigIcon);
        SendMessageW(hWnd, WM_SETICON, (IntPtr)ICON_SMALL, smallIcon);
        void Closed(object sender, WindowEventArgs args)
        {
            window.Closed -= Closed;
            SendMessageW(hWnd, WM_SETICON, (IntPtr)ICON_BIG, IntPtr.Zero);
            SendMessageW(hWnd, WM_SETICON, (IntPtr)ICON_SMALL, IntPtr.Zero);
            if (!DestroyIcon(bigIcon)) App.LogException("Release taskbar icon", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            if (!DestroyIcon(smallIcon)) App.LogException("Release window icon", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }
        window.Closed += Closed;
    }
}
