using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.Versioning;

namespace Datum.Core;

[SupportedOSPlatform("windows")]
public sealed class StaDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;
    private int _stuck;
    private bool _disposed;

    public StaDispatcher()
    {
        _thread = new Thread(() =>
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                work();
            }
        })
        {
            IsBackground = true,
            Name = "Datum STA",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public async Task<T> InvokeAsync<T>(Func<T> work, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _stuck) == 1)
        {
            throw new DatumException(
                ErrorCode.Busy,
                "SOLIDWORKS is still busy with an earlier call",
                "A slow call may still be running, so wait a minute and retry. sw_status still answers. If it stays busy, look for a dialog in SOLIDWORKS");
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception e)
            {
                completion.SetException(e);
            }
        }, cancellationToken);

        try
        {
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A COM call can't be cancelled, so the thread stays taken until SOLIDWORKS returns.
            if (Interlocked.Exchange(ref _stuck, 1) == 0)
            {
                _ = completion.Task.ContinueWith(
                    finished =>
                    {
                        _ = finished.Exception;
                        Volatile.Write(ref _stuck, 0);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            throw new DatumException(
                ErrorCode.Timeout,
                string.Create(CultureInfo.InvariantCulture, $"SOLIDWORKS did not answer within {timeout.TotalSeconds:0.#} s"));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        if (_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _queue.Dispose();
        }
    }
}
