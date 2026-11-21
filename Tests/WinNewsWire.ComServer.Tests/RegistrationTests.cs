using Microsoft.Win32;
using WinNewsWire.Automation;
using Xunit;

namespace WinNewsWire.ComServer.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public void RegistrationIsOutOfProcessAndUnregisterDoesNotRemoveAnotherInstallation()
    {
        string key = @"Software\WinNewsWire.ComRegistrationTests\" + Guid.NewGuid().ToString("N");
        const string executable = @"C:\A path with spaces\Automation\WinNewsWire.ComServer.exe";
        const string library = @"C:\A path with spaces\Automation\WinNewsWire.Automation.tlb";
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(key);
            Registration.UpdateInKey(root, true, executable, library);
            string classKey = @"CLSID\{" + AutomationIdentity.ClassID + "}";
            using (var server = root.OpenSubKey(classKey + @"\LocalServer32"))
            {
                Assert.Equal("\"" + executable + "\"", server!.GetValue(""));
                Assert.Equal(executable, server.GetValue("ServerExecutable"));
            }
            Assert.Null(root.OpenSubKey(classKey + @"\InprocServer32"));
            using (var typeLibrary = root.OpenSubKey(@"TypeLib\{" + AutomationIdentity.TypeLibraryID + @"}\1.0\0\win32"))
                Assert.Equal(library, typeLibrary!.GetValue(""));
            using (var proxy = root.OpenSubKey(@"Interface\{" + typeof(IApplication).GUID + @"}\ProxyStubClsid32"))
                Assert.Equal("{00020424-0000-0000-C000-000000000046}", proxy!.GetValue(""));
            Registration.UpdateInKey(root, false, @"C:\An old install\WinNewsWire.ComServer.exe", library);
            using (var stillRegistered = root.OpenSubKey(classKey)) Assert.NotNull(stillRegistered);
            Registration.UpdateInKey(root, false, executable, library);
            Assert.Null(root.OpenSubKey(classKey));
            Assert.Null(root.OpenSubKey(AutomationIdentity.ProgID));
            Assert.Null(root.OpenSubKey(@"TypeLib\{" + AutomationIdentity.TypeLibraryID + @"}\1.0"));
            Assert.Null(root.OpenSubKey(@"Interface\{" + typeof(IApplication).GUID + "}"));
            Registration.UpdateInKey(root, false, executable, library);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false); }
    }
}
