#if PARITY_PROBE
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TouchPointerInfo
    {
        public uint Type, Id, Frame, Flags;
        public IntPtr Device, Target;
        public CapturePoint PixelLocation, HimetricLocation, RawPixelLocation, RawHimetricLocation;
        public uint Time, HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public uint ButtonChange;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TouchContact
    {
        public TouchPointerInfo Pointer;
        public uint Flags, Mask;
        public CaptureRect Contact, RawContact;
        public uint Orientation, Pressure;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeTouchInjection(uint count, uint feedback);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InjectTouchInput(uint count, TouchContact[] contacts);
    private static bool _touchInjectionInitialized;

    private static Task<object> DriveTwoContactTouchAsync(MainWindow window, FrameworkElement view, int seconds)
    {
        if (!_touchInjectionInitialized)
        {
            if (!InitializeTouchInjection(2, 3)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Initialize native touch input");
            _touchInjectionInitialized = true;
        }
        var handle = WindowNative.GetWindowHandle(window);
        var origin = new CapturePoint();
        Require(ClientToScreen(handle, ref origin), "Could not locate the owned touch surface.");
        var bounds = view.TransformToVisual((UIElement)window.Content)
            .TransformBounds(new(0, 0, view.ActualWidth, view.ActualHeight));
        double scale = view.XamlRoot.RasterizationScale;
        int x = origin.X + (int)Math.Round((bounds.Left + bounds.Width / 2) * scale);
        int top = origin.Y + (int)Math.Round((bounds.Top + bounds.Height * 0.15) * scale);
        int bottom = origin.Y + (int)Math.Round((bounds.Bottom - bounds.Height * 0.15) * scale);
        int separation = (int)Math.Round(24 * scale);
        Require(bottom - top > 100, "The browser is too short for an owned touch gesture.");
        Require(GetForegroundWindow() == handle, "The private reader must own touch input.");
        return Task.Factory.StartNew<object>(() =>
        {
            var contacts = new TouchContact[2];
            var watch = Stopwatch.StartNew();
            int gestures = 0, events = 0, y = bottom;
            bool down = false;
            void Send(int nextY, uint flags)
            {
                y = nextY;
                for (int index = 0; index < contacts.Length; index++)
                {
                    int contactX = x + (index == 0 ? -separation : separation);
                    contacts[index] = new TouchContact
                    {
                        Pointer = new()
                        {
                            Type = 2, Id = (uint)index, Flags = flags,
                            PixelLocation = new() { X = contactX, Y = y }
                        },
                        Mask = 7,
                        Contact = new() { Left = contactX - 2, Right = contactX + 2, Top = y - 2, Bottom = y + 2 },
                        Orientation = 90, Pressure = 512
                    };
                }
                if (!InjectTouchInput(2, contacts)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Inject two native touch contacts");
                events++;
            }
            try
            {
                while (watch.Elapsed.TotalSeconds < seconds)
                {
                    Require(GetForegroundWindow() == handle, "The owned touch window lost foreground; input stopped.");
                    bool forward = (int)(watch.Elapsed.TotalSeconds / 4) % 2 == 0;
                    int start = forward ? bottom : top, end = forward ? top : bottom;
                    foreach (int contactX in new[] { x - separation, x + separation })
                        Require(GetAncestor(WindowFromPoint(new() { X = contactX, Y = start }), 2) == handle,
                            "The owned touch surface is covered; input stopped.");
                    Send(start, 0x10006);
                    down = true;
                    Thread.Sleep(16);
                    for (int step = 1; step <= 35; step++)
                    {
                        Require(GetForegroundWindow() == handle, "The owned touch gesture lost foreground; input stopped.");
                        Send(start + (end - start) * step / 35, 0x20006);
                        Thread.Sleep(12);
                    }
                    Send(y, 0x40000);
                    down = false;
                    gestures++;
                    Thread.Sleep(150);
                }
                return new { InputKind = "TwoContactTouchscreen", PrecisionTouchpad = false,
                    Contacts = 2, Gestures = gestures, EventsSent = events, DurationMs = watch.Elapsed.TotalMilliseconds };
            }
            finally
            {
                if (down) Send(y, 0x48000);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
}
#endif
