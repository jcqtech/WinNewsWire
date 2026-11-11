using WinNewsWire.AppShared.Timer;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public class TimerTests
{
    [Fact]
    public async Task PeriodicTimer_StartStop_IsThreadSafe()
    {
        await using var timer = new AsyncPeriodicTimer(_ => Task.CompletedTask,
            error => throw new InvalidOperationException("Unexpected timer failure.", error));
        var tasks = new List<Task>();
        for (int i = 0; i < 8; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 50; j++)
                {
                    timer.Start(null);
                    timer.Stop();
                }
            }));
        }
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ArticleStatusSyncTimer_ErrorHandler_CanBeAssigned()
    {
        Exception? captured = null;
        var previous = ArticleStatusSyncTimer.ErrorHandler;
        try
        {
            ArticleStatusSyncTimer.ErrorHandler = ex => captured = ex;
            var ex = new InvalidOperationException("boom");
            ArticleStatusSyncTimer.ErrorHandler!(ex);
            Assert.Same(ex, captured);
        }
        finally
        {
            ArticleStatusSyncTimer.ErrorHandler = previous;
        }
    }
}
