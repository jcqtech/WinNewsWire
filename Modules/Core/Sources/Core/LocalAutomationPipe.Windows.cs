using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WinNewsWire.Core;

public sealed partial class LocalAutomationPipe
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FirstPipeInstance = 0x00080000;
    private const uint Overlapped = 0x40000000;
    private const uint RejectRemoteClients = 0x00000008;

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateWindowsServer(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User?.Value ?? throw new InvalidOperationException("The current Windows user is unavailable.");
        var owner = identity.Owner?.Value ?? user;
        string descriptor = $"O:{owner}D:P(A;;GA;;;{user})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(descriptor, 1, out var securityDescriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = securityDescriptor,
                InheritHandle = false
            };
            var handle = CreateNamedPipeW(@"\\.\pipe\" + name, PipeAccessDuplex | FirstPipeInstance | Overlapped,
                RejectRemoteClients, 1, 8192, 8192, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error());
                handle.Dispose();
                throw error;
            }
            try { return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(securityDescriptor); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string descriptor, uint revision,
        out IntPtr securityDescriptor, out uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maximumInstances,
        uint outputBufferSize, uint inputBufferSize, uint defaultTimeout, ref SecurityAttributes attributes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
