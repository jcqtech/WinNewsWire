using System.Globalization;

namespace WinNewsWire.ActivityLog;

public sealed partial class ActivityLog
{
    public static string DataSizeMessage(ReadOnlySpan<byte> data) => DataSizeMessage(data.Length);
    public static string DataSizeMessage(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes < 1000) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        string[] units = ["kB", "MB", "GB", "TB", "PB", "EB"];
        double value = bytes / 1000d;
        int unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        return value.ToString(value >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
