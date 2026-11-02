using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;
using ModelContextProtocol.Protocol;

namespace Datum.Tests;

public class StatusToolsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

    private static readonly SolidWorksInfo Info = new(
        SolidWorksRevision.Parse("34.4.1"),
        LicenseType.Student,
        4242,
        [
            new AddInInfo(Guid.Parse("219180b0-7183-4fe2-b167-4e2bfe534004"), "3DEXPERIENCE Marketplace", Loaded: true),
            new AddInInfo(Guid.Parse("11ee348e-e286-4860-aeb9-1a6ed4138eae"), "Autotrace", Loaded: false),
        ]);

    [Fact]
    public async Task StatusIsBriefAndPointsAtTheFullList()
    {
        using var connection = new SolidWorksConnection(() => new FakeBackend { Info = Info }, Timeout);

        var result = await StatusTools.Status(connection, CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(
            """
            SOLIDWORKS 2026 SP4.1 (revision 34.4.1), supported
            License: Student (educational, files are watermarked)
            Add-ins: 2 registered, 1 loaded (3DEXPERIENCE Marketplace). Full list: sw_addins in toolset 'diagnostics'

            """.ReplaceLineEndings("\n"),
            TextOf(result));
    }

    [Fact]
    public async Task AddInsListsEveryAddInWithItsGuid()
    {
        using var connection = new SolidWorksConnection(() => new FakeBackend { Info = Info }, Timeout);

        var result = await StatusTools.AddIns(connection, CancellationToken.None);

        Assert.Equal(
            """
            [x] 3DEXPERIENCE Marketplace {219180b0-7183-4fe2-b167-4e2bfe534004}
            [ ] Autotrace {11ee348e-e286-4860-aeb9-1a6ed4138eae}

            """.ReplaceLineEndings("\n"),
            TextOf(result));
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
