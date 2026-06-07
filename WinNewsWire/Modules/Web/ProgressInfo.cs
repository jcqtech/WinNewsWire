namespace WinNewsWire.Web;

/// <summary>Port of <c>ProgressInfo</c> from RSCore. Tracks download progress for a session.</summary>
public readonly record struct ProgressInfo(int NumberRemaining, int NumberCompleted, int NumberOfTasks)
{
    public bool IsComplete => NumberOfTasks > 0 && NumberRemaining < 1;

    public static ProgressInfo Empty => default;
}
