using Datum.Core;

namespace Datum.Tests;

public class StaDispatcherTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RunsAllWorkOnOneStaThread()
    {
        using var dispatcher = new StaDispatcher();

        var first = await dispatcher.InvokeAsync(() => (Thread.CurrentThread.GetApartmentState(), Environment.CurrentManagedThreadId), Long);
        var secondThread = await dispatcher.InvokeAsync(() => Environment.CurrentManagedThreadId, Long);

        Assert.Equal(ApartmentState.STA, first.Item1);
        Assert.Equal(first.Item2, secondThread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, secondThread);
    }

    [Fact]
    public async Task PropagatesExceptionsFromWork()
    {
        using var dispatcher = new StaDispatcher();

        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.InvokeAsync<int>(() => throw new InvalidOperationException(), Long));
    }

    [Fact]
    public async Task TimesOutThenRejectsUntilTheStuckCallReturns()
    {
        using var gate = new ManualResetEventSlim();
        using var dispatcher = new StaDispatcher();

        var timeout = await Assert.ThrowsAsync<DatumException>(() => dispatcher.InvokeAsync(() => gate.Wait(Long), Short));
        var busy = await Assert.ThrowsAsync<DatumException>(() => dispatcher.InvokeAsync(() => 1, Long));
        gate.Set();

        Assert.Equal(ErrorCode.Timeout, timeout.Code);
        Assert.Equal(ErrorCode.Busy, busy.Code);
        Assert.Equal(2, await RetryWhileBusy(() => dispatcher.InvokeAsync(() => 2, Long)));
    }

    [Fact]
    public async Task RejectsWorkAfterDispose()
    {
        var dispatcher = new StaDispatcher();
        dispatcher.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => dispatcher.InvokeAsync(() => 1, Long));
    }

    internal static async Task<T> RetryWhileBusy<T>(Func<Task<T>> call)
    {
        var deadline = DateTime.UtcNow + Long;
        while (true)
        {
            try
            {
                return await call();
            }
            catch (DatumException e) when (e.Code == ErrorCode.Busy && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
        }
    }
}
