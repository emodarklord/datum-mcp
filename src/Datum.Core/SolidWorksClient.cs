using System.Runtime.Versioning;

namespace Datum.Core;

[SupportedOSPlatform("windows")]
public sealed class SolidWorksClient : IDisposable
{
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromMinutes(3);

    private readonly ISolidWorksBackend _backend;
    private readonly TimeSpan _callTimeout;
    private readonly StaDispatcher _dispatcher = new();

    private SolidWorksClient(ISolidWorksBackend backend, TimeSpan callTimeout)
    {
        _backend = backend;
        _callTimeout = callTimeout;
    }

    // Takes ownership of the backend.
    public static async Task<SolidWorksClient> ConnectAsync(
        ISolidWorksBackend backend, bool launch, TimeSpan callTimeout, CancellationToken cancellationToken = default)
    {
        var client = new SolidWorksClient(backend, callTimeout);
        try
        {
            await client.CallAsync(() => { backend.Connect(launch); return true; }, launch ? LaunchTimeout : callTimeout, cancellationToken)
                .ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public Task<SolidWorksInfo> ReadInfoAsync(CancellationToken cancellationToken = default) =>
        CallAsync(_backend.ReadInfo, _callTimeout, cancellationToken);

    // For backend calls without a dedicated wrapper; slow ones like open and save pass a longer timeout.
    // Behind a modal dialog SOLIDWORKS can accept a change and skip the rebuild, so an edit would report success
    // with nothing done. Status calls go through ReadInfoAsync, which stays open so the dialog can be reported.
    public Task<T> RunAsync<T>(Func<ISolidWorksBackend, T> work, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var dialogs = _backend.FindBlockingDialogs();
        return dialogs.Count > 0
            ? throw Blocked(dialogs)
            : CallAsync(() => work(_backend), timeout ?? _callTimeout, cancellationToken);
    }

    public void Dispose()
    {
        try
        {
            // COM references belong to the STA thread, so release them there.
            _dispatcher.InvokeAsync(() => { _backend.Dispose(); return true; }, TimeSpan.FromSeconds(5))
                .GetAwaiter().GetResult();
        }
        catch (DatumException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _dispatcher.Dispose();
    }

    private async Task<T> CallAsync<T>(Func<T> work, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await _dispatcher.InvokeAsync(work, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (DatumException e) when (e.Code is ErrorCode.Timeout or ErrorCode.Busy or ErrorCode.DialogBlocking)
        {
            var dialogs = _backend.FindBlockingDialogs();
            if (dialogs.Count == 0)
            {
                throw;
            }

            throw Blocked(dialogs);
        }
    }

    private static DatumException Blocked(IReadOnlyList<BlockingDialog> dialogs) => new(
        ErrorCode.DialogBlocking,
        $"SOLIDWORKS is showing {string.Join(", ", dialogs.Select(d => $"\"{d.Title}\""))}",
        "Close the dialog in SOLIDWORKS, then retry");
}
