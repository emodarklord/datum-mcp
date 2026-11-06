using System.Diagnostics;
using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

public sealed class DocumentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-live-").FullName;
    private readonly SolidWorksConnection _connection = new(() => new ComBackend(), TimeSpan.FromSeconds(30));
    private readonly Workspace _workspace;

    public DocumentTests() => _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"C:\nowhere\Part.prtdot")]
    public async Task FindsAPartTemplateWhenTheDefaultIsMissing(string setting)
    {
        var backend = new ComBackend();
        using var connection = new SolidWorksConnection(() => backend, TimeSpan.FromSeconds(30));
        var original = await connection.UseAsync(c => c.RunAsync(_ => backend.SwapDefaultPartTemplate(setting)), CancellationToken.None);
        try
        {
            Ok(await DocumentTools.New(connection, _workspace, "part"));
            await DocumentTools.Close(connection, discardChanges: true);
        }
        finally
        {
            await connection.UseAsync(c => c.RunAsync(_ => backend.SwapDefaultPartTemplate(original)), CancellationToken.None);
        }
    }

    [Fact]
    public async Task CreatesSavesAndExportsAPartToStep()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part", "live.SLDPRT"));
        try
        {
            Ok(await DocumentTools.Export(_connection, _workspace, "live.step"));
            var second = await DocumentTools.Export(_connection, _workspace, "live.step");

            var step = Path.Combine(_root, "Datum", "live.step");
            Assert.StartsWith("ISO-10303-21", File.ReadLines(step).First(), StringComparison.Ordinal);
            Assert.True(second.IsError);
            Assert.StartsWith("FILE_EXISTS: ", Text(second), StringComparison.Ordinal);
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    [Fact]
    public async Task OnlySavesAnOpenedFileAfterTheUserAllows()
    {
        // Copied under a new name, so it's a file Datum didn't save itself.
        Ok(await DocumentTools.New(_connection, _workspace, "part", "mine.SLDPRT"));
        Ok(await DocumentTools.Close(_connection));
        var file = Path.Combine(_root, "Datum", "theirs.SLDPRT");
        File.Copy(Path.Combine(_root, "Datum", "mine.SLDPRT"), file);

        Ok(await DocumentTools.Open(_connection, _workspace, file));
        try
        {
            var refused = await DocumentTools.Save(_connection, _workspace);
            var allowed = Ok(await DocumentTools.AllowEdit(_connection, _workspace, "theirs.SLDPRT"));
            Ok(await DocumentTools.Save(_connection, _workspace));

            Assert.StartsWith("NOT_OWNED: ", Text(refused), StringComparison.Ordinal);
            Assert.Contains("backed up to", allowed, StringComparison.Ordinal);
            Assert.Single(Directory.GetFiles(Path.Combine(_root, "backups"), "theirs.SLDPRT", SearchOption.AllDirectories));
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    // Records how a save behaves while a modal dialog is up; either outcome is fine as long as it returns.
    [Fact]
    public async Task SaveBehindAModalDialogReturns()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part", "dialog.SLDPRT"));
        using var helper = new StaDispatcher();
        var helperBackend = new ComBackend();
        var alreadyOpen = helperBackend.FindBlockingDialogs().Select(d => d.Handle).ToHashSet();
        var messageBox = helper.InvokeAsync(
            () =>
            {
                helperBackend.Connect(launch: false);
                return helperBackend.ShowMessageBox("Datum live test: this closes by itself");
            },
            TimeSpan.FromMinutes(1));
        try
        {
            var dialog = await WaitForNewDialog(helperBackend, alreadyOpen);
            var watch = Stopwatch.StartNew();

            var result = await DocumentTools.Save(_connection, _workspace);

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}");
            Assert.True(result.IsError != true || Text(result).StartsWith("DIALOG_BLOCKING", StringComparison.Ordinal), Text(result));
            Console.WriteLine($"Save behind dialog: {Text(result).Trim()}");
            DialogFinder.Close(dialog.Handle);
            await messageBox.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            foreach (var dialog in helperBackend.FindBlockingDialogs().Where(d => !alreadyOpen.Contains(d.Handle)))
            {
                DialogFinder.Close(dialog.Handle);
            }

            await helper.InvokeAsync(() => { helperBackend.Dispose(); return true; }, TimeSpan.FromSeconds(10));
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

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

    private static string Ok(CallToolResult result)
    {
        Assert.True(result.IsError != true, Text(result));
        return Text(result);
    }

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
}
