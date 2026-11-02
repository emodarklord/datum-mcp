using System.Diagnostics;
using Datum.Core;
using Datum.SolidWorks;

namespace Datum.LiveTests;

public class ConnectionTests
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ReadsInfoFromRunningSolidWorks()
    {
        using var client = await SolidWorksClient.ConnectAsync(new ComBackend(), launch: false, CallTimeout);

        var info = await client.ReadInfoAsync();

        Assert.True(info.Revision.IsSupported, $"Unsupported revision {info.Revision}");
        Assert.NotEqual(LicenseType.Unknown, info.License);
        Assert.Contains(Process.GetProcessesByName("SLDWORKS"), p => p.Id == info.ProcessId);
    }

    // SOLIDWORKS keeps serving API calls behind its own message boxes, so either outcome is fine;
    // what matters is that the call returns instead of hanging.
    [Fact]
    public async Task SurvivesAModalDialog()
    {
        var backend = new ComBackend();
        using var client = await SolidWorksClient.ConnectAsync(backend, launch: false, CallTimeout);
        using var helper = new StaDispatcher();
        var helperBackend = new ComBackend();
        var alreadyOpen = backend.FindBlockingDialogs().Select(d => d.Handle).ToHashSet();
        var messageBox = helper.InvokeAsync(
            () =>
            {
                helperBackend.Connect(launch: false);
                return helperBackend.ShowMessageBox("Datum live test: this closes by itself");
            },
            TimeSpan.FromMinutes(1));

        try
        {
            var dialog = await WaitForNewDialog(backend, alreadyOpen);
            var watch = Stopwatch.StartNew();

            try
            {
                await client.ReadInfoAsync();
            }
            catch (DatumException error)
            {
                Assert.Equal(ErrorCode.DialogBlocking, error.Code);
                Assert.Contains(dialog.Title, error.Message, StringComparison.Ordinal);
            }

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}");

            DialogFinder.Close(dialog.Handle);
            await messageBox.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(0, (await RetryWhileBlocked(client.ReadInfoAsync)).ProcessId);
        }
        finally
        {
            foreach (var dialog in backend.FindBlockingDialogs().Where(d => !alreadyOpen.Contains(d.Handle)))
            {
                DialogFinder.Close(dialog.Handle);
            }

            await helper.InvokeAsync(() => { helperBackend.Dispose(); return true; }, TimeSpan.FromSeconds(10));
        }
    }

    // Ignores dialogs the user already had open, so the test never closes one of theirs.
    private static async Task<BlockingDialog> WaitForNewDialog(ComBackend backend, HashSet<nint> alreadyOpen)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (backend.FindBlockingDialogs().FirstOrDefault(d => !alreadyOpen.Contains(d.Handle)) is { } dialog)
            {
                return dialog;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("SOLIDWORKS never showed the test message box.");
    }

    private static async Task<T> RetryWhileBlocked<T>(Func<CancellationToken, Task<T>> call)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            try
            {
                return await call(CancellationToken.None);
            }
            catch (DatumException e) when (e.Code is ErrorCode.Busy or ErrorCode.Timeout or ErrorCode.DialogBlocking && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
        }
    }
}
