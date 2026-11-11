using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.Win32.SafeHandles;

namespace WinNewsWire.AppRuntime;

internal sealed class MemoryPressureMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _onLowMemory;
    private readonly ResourceNotification _low;
    private readonly ResourceNotification _high;
    private readonly ManualResetEvent _stop;
    private Thread? _thread;
    private bool _disposed;

    public MemoryPressureMonitor(DispatcherQueue dispatcher, Action onLowMemory)
    {
        _dispatcher = dispatcher;
        _onLowMemory = onLowMemory;
        _low = new(0);
        try
        {
            _high = new(1);
            _stop = new(false);
        }
        catch
        {
            _low.Dispose();
            _high?.Dispose();
            throw;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null) return;
            _thread = new(Monitor) { IsBackground = true, Name = "WinNewsWire memory pressure" };
            _thread.Start();
        }
    }

    private void Monitor()
    {
        try
        {
            while (WaitHandle.WaitAny([_stop, _low]) == 1)
            {
                if (!_dispatcher.TryEnqueue(() =>
                {
                    if (!Volatile.Read(ref _disposed)) _onLowMemory();
                })) Trace.TraceWarning("The UI dispatcher could not accept memory-pressure cleanup.");
                // These are level-triggered notifications; wait for recovery before rearming.
                if (WaitHandle.WaitAny([_stop, _high]) == 0) return;
            }
        }
        catch (Exception error) { Trace.TraceError($"Memory-pressure monitoring failed: {error}"); }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Set();
            thread = _thread;
        }
        thread?.Join();
        _low.Dispose();
        _high.Dispose();
        _stop.Dispose();
    }

    private sealed class ResourceNotification : WaitHandle
    {
        public ResourceNotification(int type)
        {
            SafeWaitHandle = CreateMemoryResourceNotification(type);
            if (!SafeWaitHandle.IsInvalid) return;
            var error = Marshal.GetLastWin32Error();
            Dispose();
            throw new Win32Exception(error, "Windows memory-pressure notifications could not be created.");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeWaitHandle CreateMemoryResourceNotification(int notificationType);
}
