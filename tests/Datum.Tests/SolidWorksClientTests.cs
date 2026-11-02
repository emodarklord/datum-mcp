using Datum.Core;
using Datum.Fakes;

namespace Datum.Tests;

public class SolidWorksClientTests
{
    private static Task<SolidWorksClient> Connect(FakeBackend backend) =>
        SolidWorksClient.ConnectAsync(backend, launch: false, TimeSpan.FromMilliseconds(200));

    [Fact]
    public async Task ReadsInfoThroughTheBackend()
    {
        var backend = new FakeBackend();
        using var client = await Connect(backend);

        Assert.True(backend.Connected);
        Assert.Equal(backend.Info, await client.ReadInfoAsync());
    }

    [Fact]
    public async Task ConnectFailureSurfacesAndDisposesTheBackend()
    {
        var backend = new FakeBackend { ConnectFailure = ErrorCode.NotRunning };

        var error = await Assert.ThrowsAsync<DatumException>(() => Connect(backend));

        Assert.Equal(ErrorCode.NotRunning, error.Code);
        Assert.True(backend.Disposed);
    }

    [Fact]
    public async Task StuckCallWithADialogReportsTheDialog()
    {
        var backend = new FakeBackend();
        var client = await Connect(backend);
        try
        {
            backend.Dialogs = [new BlockingDialog(1, "Save As")];
            backend.Hang();

            var error = await Assert.ThrowsAsync<DatumException>(() => client.ReadInfoAsync());

            Assert.Equal(ErrorCode.DialogBlocking, error.Code);
            Assert.Contains("\"Save As\"", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            backend.Release();
            client.Dispose();
        }
    }

    [Fact]
    public async Task StuckCallWithoutADialogTimesOut()
    {
        var backend = new FakeBackend();
        var client = await Connect(backend);
        try
        {
            backend.Hang();

            var error = await Assert.ThrowsAsync<DatumException>(() => client.ReadInfoAsync());

            Assert.Equal(ErrorCode.Timeout, error.Code);
        }
        finally
        {
            backend.Release();
            client.Dispose();
        }
    }

    [Fact]
    public async Task RejectedCallReportsDialogBlocking()
    {
        var backend = new FakeBackend();
        using var client = await Connect(backend);
        backend.RejectNextCall();

        var error = await Assert.ThrowsAsync<DatumException>(() => client.ReadInfoAsync());

        Assert.Equal(ErrorCode.DialogBlocking, error.Code);
        Assert.Equal(backend.Info, await client.ReadInfoAsync());
    }

    [Fact]
    public async Task DisposeReleasesTheBackend()
    {
        var backend = new FakeBackend();
        var client = await Connect(backend);

        client.Dispose();

        Assert.True(backend.Disposed);
    }
}
