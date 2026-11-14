using System.Net.NetworkInformation;

namespace WinNewsWire.Web;

/// <summary>Port of NNW's NetworkMonitor. Monitors network connectivity status.</summary>
public static class NetworkMonitor
{
#if PARITY_PROBE
    private static bool _isAvailable = ReadInitialAvailability();
    private static bool ReadInitialAvailability()
    {
        (AppContext.GetData("WinNewsWire.NetworkInitializationProbe") as Action)?.Invoke();
        return NetworkInterface.GetIsNetworkAvailable();
    }
#else
    private static bool _isAvailable = NetworkInterface.GetIsNetworkAvailable();
#endif

    static NetworkMonitor()
    {
        NetworkChange.NetworkAvailabilityChanged += (_, e) =>
        {
            _isAvailable = e.IsAvailable;
            ConnectivityChanged?.Invoke(null, e.IsAvailable);
        };
    }

    /// <summary>Whether network connectivity is currently available.</summary>
    public static bool IsAvailable => _isAvailable;

    /// <summary>Raised when connectivity status changes. True = connected, false = disconnected.</summary>
    public static event EventHandler<bool>? ConnectivityChanged;
}
