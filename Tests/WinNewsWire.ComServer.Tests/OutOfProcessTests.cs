using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Xunit;

namespace WinNewsWire.ComServer.Tests;

public sealed class OutOfProcessTests
{
    [Theory]
    [InlineData("powershell.exe", "ComClient.ps1")]
    [InlineData("pwsh.exe", "ComClient.ps1")]
    [InlineData("cscript.exe", "ComClient.vbs")]
    [InlineData("cscript.exe", "ComClient.js")]
    [InlineData("powershell-x86", "ComClient.ps1")]
    [InlineData("cscript-x86", "ComClient.vbs")]
    [InlineData("cscript-x86", "ComClient.js")]
    [InlineData("typed", "")]
    public async Task ScriptClientsActivateAndUseAnIsolatedRealLocalServer(string client, string script)
    {
        var id = Guid.NewGuid();
        string progid = "WinNewsWire.Test" + id.ToString("N") + ".Application";
        string clsidKey = @"Software\Classes\CLSID\{" + id + "}";
        string progidKey = @"Software\Classes\" + progid;
        string executable = Path.Combine(AppContext.BaseDirectory, "WinNewsWire.ComServer.Tests.exe");
        string pipe = "WinNewsWire.ComTest." + id.ToString("N");
        string pidFile = Path.Combine(Path.GetTempPath(), "WinNewsWire-ComTest-" + id.ToString("N") + ".pid");
        using (var key = Registry.CurrentUser.CreateSubKey(clsidKey + @"\LocalServer32"))
            key.SetValue("", $"\"{executable}\" --fixture {pipe} {id} \"{pidFile}\"");
        using (var key = Registry.CurrentUser.CreateSubKey(progidKey + @"\CLSID"))
            key.SetValue("", "{" + id + "}");
        try
        {
            string clientPath = client switch
            {
                "powershell-x86" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "WindowsPowerShell", "v1.0", "powershell.exe"),
                "cscript-x86" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "cscript.exe"),
                "typed" => executable,
                _ => client
            };
            var info = new ProcessStartInfo(clientPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in client == "typed" ? new[] { "--typed-client", progid }
                : client.StartsWith("cscript", StringComparison.Ordinal)
                ? new[] { "//nologo", Path.Combine(AppContext.BaseDirectory, "Clients", script), progid }
                : new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                    Path.Combine(AppContext.BaseDirectory, "Clients", script), "-ProgID", progid })
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            Assert.True(process.ExitCode == 0, $"{client}: {await output}\n{await error}");
            Assert.True((await output).Contains("COM PARITY OK"), $"{client}: {await output}\n{await error}");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(clsidKey, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(progidKey, throwOnMissingSubKey: false);
            if (File.Exists(pidFile))
            {
                int pid = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    using var fixture = Process.GetProcessById(pid);
                    if (!fixture.HasExited && !fixture.WaitForExit(6000))
                    {
                        Assert.Equal(executable, fixture.MainModule!.FileName, ignoreCase: true);
                        fixture.Kill(entireProcessTree: true);
                        fixture.WaitForExit();
                    }
                }
                catch (ArgumentException) { } // The server already exited after releasing its last object.
                File.Delete(pidFile);
                string directory = Path.Combine(Path.GetTempPath(), "WinNewsWire-ComFixture-" + id.ToString("N"));
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MissingOrDisabledPipeFailsExplicitlyInsteadOfReturningEmptyData()
    {
        int launches = 0;
        var transport = new Automation.PipeTransport("WinNewsWire.Missing." + Guid.NewGuid().ToString("N"),
            () => { ++launches; throw new COMException("Disabled fixture", Automation.Errors.Unavailable); });
        var error = await Task.Run(() => Assert.Throws<COMException>(() => transport.Send(new("get", Property: "accounts"))));
        Assert.Equal(Automation.Errors.Unavailable, error.HResult);
        Assert.Equal(1, launches);
    }
}
