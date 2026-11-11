using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WinNewsWire.AppRuntime;

internal static class UserInputIdle
{
    private const int QuietMilliseconds = 500;

    internal static async Task WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref input))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the user's input activity.");
            int idle = unchecked(Environment.TickCount - (int)input.Time);
            if (idle >= QuietMilliseconds) return;
            await Task.Delay(QuietMilliseconds - Math.Max(0, idle), cancellationToken);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo input);
}
