using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace WinNewsWire.Diagnostics;

internal static class Program
{
    private sealed record Command(long Handle, int ProcessId, int X, int Y, long Origin, long Frequency,
        int IntervalMs, int Delta, int ReverseMs, int DurationMs, int BurstMs);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public MouseInput Mouse; }
    private readonly record struct Sample(double AtMs, int Delta);
    private sealed record Result(List<Sample> Events, double InputStoppedMs, double QuietEndedMs,
        int DriverProcessId, int[] GcCollections, int[] QuietGcCollections);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPhysicalPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, IntPtr callback, IntPtr argument, bool resume);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle timer, uint milliseconds);

    private static int Main()
    {
        Console.WriteLine("ready");
        while (Console.ReadLine() is { } line)
        {
            try
            {
                var command = JsonSerializer.Deserialize<Command>(line)
                    ?? throw new InvalidDataException("A wheel command is required.");
                Console.WriteLine("started");
                Console.WriteLine(JsonSerializer.Serialize(Run(command)));
            }
            catch (Exception error)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { Error = error.ToString() }));
                return 1;
            }
        }
        return 0;
    }

    private static void VerifyTarget(IntPtr window, int process, Point point)
    {
        GetWindowThreadProcessId(window, out uint owner);
        if (owner != process || GetForegroundWindow() != window)
            throw new InvalidOperationException("The private reader lost foreground or exited; wheel input stopped.");
        if (!GetPhysicalCursorPos(out var current) || current.X != point.X || current.Y != point.Y)
            throw new InvalidOperationException($"The cursor moved from {point.X},{point.Y} to {current.X},{current.Y}; wheel input stopped.");
        if (GetAncestor(WindowFromPhysicalPoint(point), 2) != window)
            throw new InvalidOperationException("The private input point was covered; wheel input stopped.");
    }

    private static Result Run(Command command)
    {
        var previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previousDpi == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Set input thread DPI awareness");
        try { return RunCore(command); }
        finally { SetThreadDpiAwarenessContext(previousDpi); }
    }

    private static Result RunCore(Command command)
    {
        var (handle, process, x, y, origin, frequency, interval, delta, reverse, duration, burst) = command;
        if (frequency != Stopwatch.Frequency || interval < 1 || duration < 1 || duration > 60000 ||
            delta < 1 || delta > 1200 || reverse < interval || burst < 0 || burst > reverse)
            throw new ArgumentException("Invalid wheel workload or incompatible monotonic clock.");
        var window = new IntPtr(handle);
        var point = new Point { X = x, Y = y };
        VerifyTarget(window, process, point);
        using var timer = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x1F0003);
        if (timer.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Create input timer");
        var input = new Input[1];
        input[0].Mouse.Flags = 0x0800;
        int size = Marshal.SizeOf<Input>();
        var events = new List<Sample>(duration / interval + 2);
        var collections = new int[3];
        var inputCollections = new int[3];
        var quietCollections = new int[3];
        GC.Collect();
        GC.WaitForPendingFinalizers();
        for (int generation = 0; generation < 3; generation++)
            collections[generation] = GC.CollectionCount(generation);
        long due = -interval * 10000L;
        long started = Stopwatch.GetTimestamp();
        if (!SetWaitableTimer(timer, ref due, interval, IntPtr.Zero, IntPtr.Zero, false))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Start input timer");
        while ((Stopwatch.GetTimestamp() - started) * 1000.0 / frequency < duration)
        {
            VerifyTarget(window, process, point);
            long now = Stopwatch.GetTimestamp();
            double elapsed = (now - started) * 1000.0 / frequency;
            if (elapsed >= duration) break;
            if (burst > 0 && elapsed % reverse >= burst)
            {
                Wait(timer);
                continue;
            }
            int phase = interval == reverse && burst == 0 ? events.Count : (int)(elapsed / reverse);
            int amount = (phase % 2 == 0 ? -1 : 1) * delta;
            input[0].Mouse.Data = unchecked((uint)amount);
            if (SendInput(1, input, size) != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Send wheel input");
            events.Add(new((now - origin) * 1000.0 / frequency, amount));
            Wait(timer);
        }
        double stopped = events[^1].AtMs;
        for (int generation = 0; generation < 3; generation++)
            inputCollections[generation] = GC.CollectionCount(generation) - collections[generation];
        double quietEnded;
        do
        {
            VerifyTarget(window, process, point);
            Wait(timer);
            quietEnded = (Stopwatch.GetTimestamp() - origin) * 1000.0 / frequency;
        } while (quietEnded - stopped < 1500);
        for (int generation = 0; generation < 3; generation++)
            quietCollections[generation] = GC.CollectionCount(generation) - collections[generation] - inputCollections[generation];
        return new(events, stopped, quietEnded, Environment.ProcessId, inputCollections, quietCollections);
    }

    private static void Wait(SafeWaitHandle timer)
    {
        if (WaitForSingleObject(timer, 5000) != 0)
            throw new InvalidOperationException("The input timer stopped responding.");
    }
}
