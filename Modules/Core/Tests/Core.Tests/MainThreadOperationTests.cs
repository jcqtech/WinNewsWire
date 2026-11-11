using System.Collections.Concurrent;
using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.Core.Tests;

public sealed class MainThreadOperationTests
{
    [Fact]
    public Task SingleOperation() => RunInOrder([0], []);

    [Fact]
    public Task OperationAndDependency() => RunInOrder([0, 1], [(1, 0)]);

    [Fact]
    public Task OperationAndDependencyAddedOutOfOrder() => RunInOrder([1, 0], [(1, 0)]);

    [Fact]
    public Task OperationAndTwoDependenciesAddedOutOfOrder() => RunInOrder([1, 2, 0], [(1, 0), (2, 0)]);

    [Fact]
    public Task ChildOperationWithTwoDependencies() => RunInOrder([2, 0, 1], [(2, 0), (2, 1)]);

    [Fact]
    public Task AddingManyOperations() => RunInOrder(Enumerable.Range(0, 1000).ToArray(), []);

    private static async Task RunInOrder(int[] additionOrder, (int Child, int Parent)[] dependencies)
    {
        using var context = new UIContext();
        MainThreadOperationQueue? queue = null;
        var operations = await context.InvokeAsync(() =>
        {
            queue = new MainThreadOperationQueue();
            int index = 0;
            var result = Enumerable.Range(0, additionOrder.Length).Select(i => new MainThreadBlockOperation(() =>
            {
                Assert.Equal(context.ThreadID, Environment.CurrentManagedThreadId);
                Assert.Equal(i, index++);
            })).ToArray();
            foreach (var (child, parent) in dependencies) result[child].AddDependency(result[parent]);
            queue.Add(additionOrder.Select(i => result[i]));
            return result;
        });
        await Task.WhenAll(operations.Select(operation => operation.Completion)).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, await context.InvokeAsync(() => queue!.PendingOperationsCount));
    }

    [Fact]
    public async Task AddingManyOperationsAndCancelingManyOperations()
    {
        using var context = new UIContext();
        await context.InvokeAsync(() =>
        {
            var queue = new MainThreadOperationQueue();
            var operations = Enumerable.Range(0, 1000).Select(_ => new MainThreadBlockOperation(() => Assert.Fail("A cancelled operation ran."))).ToArray();
            queue.Add(operations);
            queue.Cancel(operations);
            Assert.Equal(0, queue.PendingOperationsCount);
            return true;
        });
    }

    [Fact]
    public async Task AddingManyOperationsWithCompletionBlocks()
    {
        using var context = new UIContext();
        MainThreadOperationQueue? queue = null;
        var operations = await context.InvokeAsync(() =>
        {
            queue = new MainThreadOperationQueue();
            int index = 0;
            var result = Enumerable.Range(0, 100).Select(i =>
            {
                var operation = new MainThreadBlockOperation(() => Assert.Equal(i, index));
                operation.CompletionBlock = completed =>
                {
                    Assert.Same(operation, completed);
                    Assert.Equal(i, index++);
                };
                return operation;
            }).ToArray();
            queue.Add(result);
            return result;
        });
        await Task.WhenAll(operations.Select(operation => operation.Completion)).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, await context.InvokeAsync(() => queue!.PendingOperationsCount));
    }

    [Fact]
    public async Task CancellingOperationsWithName()
    {
        using var context = new UIContext();
        await context.InvokeAsync(() =>
        {
            var queue = new MainThreadOperationQueue();
            queue.Suspend();
            for (int i = 0; i < 100; i++)
            {
                queue.Add(new MainThreadBlockOperation(() => { }, i.ToString()));
                queue.Add(new MainThreadBlockOperation(() => { }, "foo"));
            }
            queue.Resume();
            queue.Cancel("33");
            queue.Cancel("99");
            queue.Cancel("654");
            queue.Cancel("foo");
            Assert.Equal(98, queue.PendingOperationsCount);
            queue.CancelAll();
            Assert.Equal(0, queue.PendingOperationsCount);
            return true;
        });
    }

    [Fact]
    public async Task BackgroundCompletionReturnsToTheOwningThread()
    {
        using var context = new UIContext();
        MainThreadOperationQueue? queue = null;
        var operation = await context.InvokeAsync(() =>
        {
            queue = new MainThreadOperationQueue();
            var result = new BackgroundOperation();
            result.CompletionBlock = _ => Assert.Equal(context.ThreadID, Environment.CurrentManagedThreadId);
            queue.Add(result);
            return result;
        });
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        GC.KeepAlive(queue);
    }

    [Fact]
    public async Task CancellationPropagatesAndCompletionRunsOnce()
    {
        using var context = new UIContext();
        int completions = 0;
        var child = await context.InvokeAsync(() =>
        {
            var queue = new MainThreadOperationQueue();
            queue.Suspend();
            var parent = new MainThreadBlockOperation(() => Assert.Fail("Cancelled parent ran."));
            var dependent = new MainThreadBlockOperation(() => Assert.Fail("Cancelled child ran."));
            dependent.AddDependency(parent);
            dependent.CompletionBlock = _ => completions++;
            queue.Add([parent, dependent]);
            parent.Cancel();
            parent.Cancel();
            queue.Resume();
            return dependent;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => child.Completion.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, completions);
    }

    [Fact]
    public async Task CyclesAndDuplicateQueueEntriesAreRejected()
    {
        using var context = new UIContext();
        await context.InvokeAsync(() =>
        {
            var first = new MainThreadBlockOperation(() => { });
            var second = new MainThreadBlockOperation(() => { });
            first.AddDependency(second);
            Assert.Throws<InvalidOperationException>(() => second.AddDependency(first));
            var queue = new MainThreadOperationQueue();
            queue.Suspend();
            queue.Add(first);
            Assert.Throws<InvalidOperationException>(() => queue.Add(first));
            queue.CancelAll();
            return true;
        });
    }

    private sealed class BackgroundOperation : MainThreadOperation
    {
        public override void Run() => _ = Task.Run(DidComplete);
    }

    private sealed class UIContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> _callbacks = new();
        private readonly Thread _thread;
        public int ThreadID => _thread.ManagedThreadId;

        public UIContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var callback in _callbacks.GetConsumingEnumerable()) callback();
            }) { IsBackground = true, Name = "Operation test UI" };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Add(() => callback(state));
        public Task<T> InvokeAsync<T>(Func<T> work)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ =>
            {
                try { completion.SetResult(work()); }
                catch (Exception error) { completion.SetException(error); }
            }, null);
            return completion.Task;
        }

        public void Dispose()
        {
            _callbacks.Add(_callbacks.CompleteAdding);
            if (!_thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("The operation test UI did not stop.");
            _callbacks.Dispose();
        }
    }
}
