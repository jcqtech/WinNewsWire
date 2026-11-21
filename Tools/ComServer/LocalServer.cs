using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WinNewsWire.Automation;

[ComVisible(true), Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
 TypeLibType(TypeLibTypeFlags.FHidden | TypeLibTypeFlags.FRestricted)]
public interface IClassFactory
{
    [PreserveSig] int CreateInstance(nint outer, ref Guid iid, out nint instance);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool @lock);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None), TypeLibType(TypeLibTypeFlags.FHidden | TypeLibTypeFlags.FRestricted)]
public sealed class ApplicationFactory : IClassFactory
{
    private readonly AutomationSession _session;
    internal ApplicationFactory(AutomationSession session) => _session = session;
    internal int Locks { get; private set; }
    public int CreateInstance(nint outer, ref Guid iid, out nint instance)
    {
        instance = 0;
        if (outer != 0) return unchecked((int)0x80040110);
        var application = _session.Wrap(new());
        var unknown = Marshal.GetIUnknownForObject(application);
        try { return Marshal.QueryInterface(unknown, ref iid, out instance); }
        finally { Marshal.Release(unknown); }
    }
    public int LockServer(bool @lock)
    {
        if (!@lock && Locks == 0) return Errors.InvalidArgument;
        Locks += @lock ? 1 : -1;
        return 0;
    }
}

internal static class LocalServer
{
    private static bool _securityInitialized;
    internal static void Run(IAutomationTransport transport, Guid? classID = null, uint idleMilliseconds = 30000)
    {
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2));
        uint cookie = 0;
        nuint timer = 0;
        var session = new AutomationSession(transport);
        var factory = new ApplicationFactory(session);
        try
        {
            InitializeSecurity();
            var clsid = classID ?? new Guid(AutomationIdentity.ClassID);
            Marshal.ThrowExceptionForHR(CoRegisterClassObject(ref clsid, factory, 4, 5, out cookie));
            timer = SetTimer(0, 0, idleMilliseconds, 0);
            if (timer == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Marshal.ThrowExceptionForHR(CoResumeClassObjects());
            while (true)
            {
                int status = GetMessage(out var message, 0, 0, 0);
                if (status == -1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if (status == 0) break;
                if (message.Message == 0x0113 && message.WParam == timer && factory.Locks == 0)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    if (!session.HasLiveObjects())
                    {
                        Marshal.ThrowExceptionForHR(CoSuspendClassObjects());
                        break;
                    }
                }
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            if (timer != 0) KillTimer(0, timer);
            if (cookie != 0) CoRevokeClassObject(cookie);
            GC.KeepAlive(factory);
            CoUninitialize();
        }
    }

    internal static void InitializeSecurity()
    {
        if (_securityInitialized) return;
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("The Windows user could not be identified.");
        // COM_RIGHTS_EXECUTE | COM_RIGHTS_EXECUTE_LOCAL, deliberately excluding remote execution.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"O:{sid}G:{sid}D:(A;;0x3;;;{sid})(A;;0x3;;;SY)", 1, out var descriptor, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var buffers = new nint[5];
        try
        {
            uint size = 0, daclSize = 0, saclSize = 0, ownerSize = 0, groupSize = 0;
            if (MakeAbsoluteSD(descriptor, 0, ref size, 0, ref daclSize, 0, ref saclSize, 0, ref ownerSize, 0, ref groupSize)
                || Marshal.GetLastWin32Error() != 122)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            uint[] sizes = [size, daclSize, saclSize, ownerSize, groupSize];
            for (int i = 0; i < buffers.Length; ++i)
                if (sizes[i] != 0) buffers[i] = Marshal.AllocHGlobal(checked((int)sizes[i]));
            if (!MakeAbsoluteSD(descriptor, buffers[0], ref size, buffers[1], ref daclSize, buffers[2], ref saclSize,
                buffers[3], ref ownerSize, buffers[4], ref groupSize))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Marshal.ThrowExceptionForHR(CoInitializeSecurity(buffers[0], -1, 0, 0, 6, 2, 0, 0x3000, 0));
            _securityInitialized = true;
        }
        finally
        {
            foreach (var buffer in buffers) if (buffer != 0) Marshal.FreeHGlobal(buffer);
            LocalFree(descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MessageData
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern int CoInitializeSecurity(nint descriptor, int services, nint authenticationServices,
        nint reserved, uint authenticationLevel, uint impersonationLevel, nint authenticationList, uint capabilities, nint reserved2);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoRegisterClassObject(ref Guid clsid,
        [MarshalAs(UnmanagedType.Interface)] IClassFactory factory, uint context, uint flags, out uint cookie);
    [DllImport("ole32.dll")] private static extern int CoRevokeClassObject(uint cookie);
    [DllImport("ole32.dll")] private static extern int CoResumeClassObjects();
    [DllImport("ole32.dll")] private static extern int CoSuspendClassObjects();
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessage(out MessageData message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MessageData message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref MessageData message);
    [DllImport("user32.dll", SetLastError = true)] private static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint window, nuint id);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out nint descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool MakeAbsoluteSD(nint relative, nint absolute, ref uint absoluteSize,
        nint dacl, ref uint daclSize, nint sacl, ref uint saclSize, nint owner, ref uint ownerSize, nint group, ref uint groupSize);
}
