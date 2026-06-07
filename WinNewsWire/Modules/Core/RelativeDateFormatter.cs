using System.Globalization;

namespace WinNewsWire.Core;

/// <summary>Port of <c>ArticleStringFormatter</c> relative date logic.</summary>
public static class RelativeDateFormatter
{
    public static string Format(DateTime date, DateTime now)
    {
        date = date.ToLocalTime();
        now = now.ToLocalTime();
        var today = now.Date;
        var day = date.Date;
        if (day == today) return date.ToString("t", CultureInfo.CurrentCulture);
        if (day == today.AddDays(-1)) return "Yesterday";
        if ((today - day).TotalDays < 7) return date.ToString("dddd", CultureInfo.CurrentCulture);
        if (date.Year == now.Year) return date.ToString("MMM d", CultureInfo.CurrentCulture);
        return date.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }
}

/// <summary>Port of <c>CoalescingQueue</c> — simple debouncer.</summary>
public sealed class CoalescingQueue
{
    private readonly TimeSpan _delay;
    private readonly Action _callback;
    private readonly object _lock = new();
    private Timer? _timer;
    private bool _pending;

    public CoalescingQueue(TimeSpan delay, Action callback) { _delay = delay; _callback = callback; }

    public void Add()
    {
        lock (_lock)
        {
            _pending = true;
            _timer ??= new Timer(OnTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Flush()
    {
        bool run;
        lock (_lock) { run = _pending; _pending = false; }
        if (run) _callback();
    }

    private void OnTick(object? _)
    {
        bool run;
        lock (_lock) { run = _pending; _pending = false; }
        if (run) _callback();
    }
}
