using Microsoft.Win32;

namespace WinNewsWire.Automation;

internal static class Registration
{
    private static readonly Type[] Interfaces =
        [typeof(IApplication), typeof(IAccount), typeof(IFolder), typeof(IFeed), typeof(IArticle), typeof(IAuthor), typeof(IObjectCollection)];

    internal static void Update(bool register)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The server path is unavailable.");
        if (!Path.GetFileName(executable).Equals("WinNewsWire.ComServer.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use WinNewsWire.ComServer.exe, not dotnet, for registration.");
        string typeLibrary = Path.Combine(AppContext.BaseDirectory, "WinNewsWire.Automation.tlb");
        if (register && !File.Exists(typeLibrary)) throw new FileNotFoundException("The scripting type library is missing.", typeLibrary);
        foreach (var view in Environment.Is64BitOperatingSystem
            ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry32 })
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using var classes = user.CreateSubKey(@"Software\Classes");
            UpdateInKey(classes, register, executable, typeLibrary);
        }
    }

    internal static void UpdateInKey(RegistryKey classes, bool register, string executable, string typeLibrary)
    {
        string classKey = @"CLSID\{" + AutomationIdentity.ClassID + "}";
        string libraryKey = @"TypeLib\{" + AutomationIdentity.TypeLibraryID + @"}\1.0";
        if (register)
        {
            using var clsid = classes.CreateSubKey(classKey);
            clsid.SetValue("", "WinNewsWire scripting application");
            using (var server = clsid.CreateSubKey("LocalServer32"))
            {
                server.SetValue("", "\"" + executable + "\"");
                server.SetValue("ServerExecutable", executable);
            }
            using (var progid = clsid.CreateSubKey("ProgID")) progid.SetValue("", AutomationIdentity.ProgID);
            using (var libraryID = clsid.CreateSubKey("TypeLib")) libraryID.SetValue("", "{" + AutomationIdentity.TypeLibraryID + "}");
            using var application = classes.CreateSubKey(AutomationIdentity.ProgID);
            application.SetValue("", "WinNewsWire scripting application");
            using (var applicationClass = application.CreateSubKey("CLSID")) applicationClass.SetValue("", "{" + AutomationIdentity.ClassID + "}");
            using var library = classes.CreateSubKey(libraryKey);
            library.SetValue("", "WinNewsWire scripting 1.0");
            using (var win32 = library.CreateSubKey(@"0\win32")) win32.SetValue("", typeLibrary);
            if (Environment.Is64BitOperatingSystem)
            {
                using var win64 = library.CreateSubKey(@"0\win64");
                win64.SetValue("", typeLibrary);
            }
            using (var flags = library.CreateSubKey("FLAGS")) flags.SetValue("", "0");
            using (var help = library.CreateSubKey("HELPDIR")) help.SetValue("", Path.GetDirectoryName(typeLibrary)!);
            foreach (var type in Interfaces)
            {
                using var contract = classes.CreateSubKey(@"Interface\{" + type.GUID + "}");
                contract.SetValue("", type.Name);
                contract.SetValue("WinNewsWireServer", executable);
                using (var proxy = contract.CreateSubKey("ProxyStubClsid32"))
                    proxy.SetValue("", "{00020424-0000-0000-C000-000000000046}");
                using var contractLibrary = contract.CreateSubKey("TypeLib");
                contractLibrary.SetValue("", "{" + AutomationIdentity.TypeLibraryID + "}");
                contractLibrary.SetValue("Version", "1.0");
            }
        }
        else
        {
            using var server = classes.OpenSubKey(classKey + @"\LocalServer32");
            if (!string.Equals(server?.GetValue("ServerExecutable") as string, executable, StringComparison.OrdinalIgnoreCase)) return;
            classes.DeleteSubKeyTree(classKey, throwOnMissingSubKey: false);
            using var applicationClass = classes.OpenSubKey(AutomationIdentity.ProgID + @"\CLSID");
            if (string.Equals(applicationClass?.GetValue("") as string, "{" + AutomationIdentity.ClassID + "}", StringComparison.OrdinalIgnoreCase))
                classes.DeleteSubKeyTree(AutomationIdentity.ProgID, throwOnMissingSubKey: false);
            using var library = classes.OpenSubKey(libraryKey + @"\0\win32");
            if (string.Equals(library?.GetValue("") as string, typeLibrary, StringComparison.OrdinalIgnoreCase))
                classes.DeleteSubKeyTree(libraryKey, throwOnMissingSubKey: false);
            foreach (var type in Interfaces)
            {
                string key = @"Interface\{" + type.GUID + "}";
                using var contract = classes.OpenSubKey(key);
                if (string.Equals(contract?.GetValue("WinNewsWireServer") as string, executable, StringComparison.OrdinalIgnoreCase))
                    classes.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            }
        }
    }
}
