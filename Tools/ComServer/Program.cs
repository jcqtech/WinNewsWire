using System.Diagnostics;
using System.Runtime.InteropServices;

[assembly: ComVisible(false)]

namespace WinNewsWire.Automation;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
#if PARITY_PROBE
            string? probeOutput = null;
            int probeOption = Array.IndexOf(args, "--probe-output");
            if (probeOption >= 0 && probeOption + 1 < args.Length)
            {
                probeOutput = Path.GetFullPath(args[probeOption + 1]);
                WinNewsWire.Core.AppConfig.SetDataDirectoryOverride(Path.Combine(probeOutput, "profile"));
                File.WriteAllText(Path.Combine(probeOutput, "com-server-arguments.json"),
                    System.Text.Json.JsonSerializer.Serialize(args));
                args = args.Where((_, index) => index != probeOption && index != probeOption + 1).ToArray();
            }
#endif
            if (args.Length == 1 && args[0].Equals("--register", StringComparison.OrdinalIgnoreCase))
                Registration.Update(register: true);
            else if (args.Length == 1 && args[0].Equals("--unregister", StringComparison.OrdinalIgnoreCase))
                Registration.Update(register: false);
            else if (args.Length is 1 or 2 && args.All(arg => arg.Equals("-Embedding", StringComparison.OrdinalIgnoreCase)))
#if PARITY_PROBE
                LocalServer.Run(PipeTransport.ForApplication(probeOutput));
#else
                LocalServer.Run(PipeTransport.ForApplication());
#endif
            else throw new ArgumentException("Use --register, --unregister, or COM activation (-Embedding).");
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException
            or UnauthorizedAccessException or COMException or System.ComponentModel.Win32Exception
            or System.Security.SecurityException)
        {
            Trace.TraceError("WinNewsWire COM server: {0}", error);
            // WinExe has no console; make registration/startup failures discoverable without a blocking dialog.
            try
            {
                string directory = WinNewsWire.Core.AppConfig.LogsDirectory;
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "ComServer.log"), $"{DateTimeOffset.Now:O} {error}\n");
            }
            catch (Exception loggingError) when (loggingError is IOException or UnauthorizedAccessException)
            {
                Trace.TraceError("Cannot write COM server log: {0}", loggingError);
            }
            return 1;
        }
    }
}
