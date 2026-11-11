using WinNewsWire.Account;

if (args.Length != 1 || args[0] != "--stdio")
{
    Console.Error.WriteLine("This executable is the private WinNewsWire refresh worker.");
    return 2;
}

try
{
    await LocalAccountRefreshWorker.RunAsync(Console.In, Console.Out);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
