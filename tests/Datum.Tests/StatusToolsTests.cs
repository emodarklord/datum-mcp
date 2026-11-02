using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;
using ModelContextProtocol.Protocol;

namespace Datum.Tests;

public class StatusToolsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task ReturnsTheDiagnoseReport()
    {
        var backend = new FakeBackend();
        using var connection = new SolidWorksConnection(() => backend, Timeout);

        var result = await StatusTools.Status(connection, CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(DiagnoseReport.Format(backend.Info), TextOf(result));
    }

    [Fact]
    public async Task ReportsDatumErrorsAsToolErrors()
    {
        using var connection = new SolidWorksConnection(() => new FakeBackend { ConnectFailure = ErrorCode.NotRunning }, Timeout);

        var result = await StatusTools.Status(connection, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.StartsWith("NOT_RUNNING: ", TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsDialogsAsToolErrors()
    {
        var backend = new FakeBackend();
        using var connection = new SolidWorksConnection(() => backend, Timeout);
        await StatusTools.Status(connection, CancellationToken.None);
        backend.RejectNextCall();

        var result = await StatusTools.Status(connection, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.StartsWith("DIALOG_BLOCKING: ", TextOf(result), StringComparison.Ordinal);
    }

    internal static string TextOf(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
}
