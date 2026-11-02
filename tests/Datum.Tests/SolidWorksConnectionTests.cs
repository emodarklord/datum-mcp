using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;

namespace Datum.Tests;

public class SolidWorksConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task ConnectsOnFirstUseAndReusesTheClient()
    {
        var created = new List<FakeBackend>();
        using var connection = new SolidWorksConnection(() => Track(created, new FakeBackend()), Timeout);

        Assert.Empty(created);
        await connection.UseAsync(client => client.ReadInfoAsync(), CancellationToken.None);
        await connection.UseAsync(client => client.ReadInfoAsync(), CancellationToken.None);

        Assert.Single(created);
    }

    [Fact]
    public async Task RetriesConnectAfterAFailedConnect()
    {
        var created = new List<FakeBackend>();
        var failNext = true;
        using var connection = new SolidWorksConnection(
            () => Track(created, new FakeBackend { ConnectFailure = failNext ? ErrorCode.NotRunning : null }),
            Timeout);

        var error = await Assert.ThrowsAsync<DatumException>(() => connection.UseAsync(client => client.ReadInfoAsync(), CancellationToken.None));
        failNext = false;
        await connection.UseAsync(client => client.ReadInfoAsync(), CancellationToken.None);

        Assert.Equal(ErrorCode.NotRunning, error.Code);
        Assert.Equal(2, created.Count);
    }

    [Fact]
    public async Task ReconnectsAfterSolidWorksGoesAway()
    {
        var created = new List<FakeBackend>();
        using var connection = new SolidWorksConnection(() => Track(created, new FakeBackend()), Timeout);
        await connection.UseAsync(client => client.ReadInfoAsync(), CancellationToken.None);

        await Assert.ThrowsAsync<DatumException>(() => connection.UseAsync<int>(
            _ => throw new DatumException(ErrorCode.NotRunning, "SOLIDWORKS closed"),
            CancellationToken.None));
        await connection.UseAsync(client => client.ReadInfoAsync(), CancellationToken.None);

        Assert.Equal(2, created.Count);
        Assert.True(created[0].Disposed);
    }

    [Fact]
    public async Task KeepsTheClientAfterOtherErrors()
    {
        var created = new List<FakeBackend>();
        using var connection = new SolidWorksConnection(() => Track(created, new FakeBackend()), Timeout);
        await connection.UseAsync(client => client.ReadInfoAsync(), CancellationToken.None);

        created[0].RejectNextCall();
        await Assert.ThrowsAsync<DatumException>(() => connection.UseAsync(client => client.ReadInfoAsync(), CancellationToken.None));
        await connection.UseAsync(client => client.ReadInfoAsync(), CancellationToken.None);

        Assert.Single(created);
    }

    [Fact]
    public async Task RaisesCapabilitiesOnConnectAndNullWhenSolidWorksGoesAway()
    {
        using var connection = new SolidWorksConnection(() => new FakeBackend(), Timeout);
        var raised = new List<SolidWorksCapabilities?>();
        connection.CapabilitiesChanged += raised.Add;

        await connection.UseAsync(client => client.ReadInfoAsync(), CancellationToken.None);
        await Assert.ThrowsAsync<DatumException>(() => connection.UseAsync<int>(
            _ => throw new DatumException(ErrorCode.NotRunning, "SOLIDWORKS closed"),
            CancellationToken.None));

        Assert.Equal(2, raised.Count);
        Assert.Equal(2026, raised[0]?.Year);
        Assert.Null(raised[1]);
    }

    private static FakeBackend Track(List<FakeBackend> created, FakeBackend backend)
    {
        created.Add(backend);
        return backend;
    }
}
