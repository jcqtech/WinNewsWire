namespace WinNewsWire.Core;

public sealed class MainThreadBlockOperation(Action work, string? name = null) : MainThreadOperation(name)
{
    public override void Run()
    {
        work();
        DidComplete();
    }
}

public sealed class MainThreadAsyncOperation(Func<CancellationToken, Task> work, string? name = null) : MainThreadOperation(name)
{
    public override async void Run()
    {
        try
        {
            await work(CancellationToken);
            DidComplete();
        }
        catch (OperationCanceledException) when (IsCanceled) { DidComplete(); }
        catch (Exception error) { Fail(error); }
    }
}
