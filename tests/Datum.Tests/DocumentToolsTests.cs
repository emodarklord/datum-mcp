using System.Text.RegularExpressions;
using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;
using ModelContextProtocol.Protocol;

namespace Datum.Tests;

public sealed class DocumentToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-docs-").FullName;
    private readonly FakeBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;

    public DocumentToolsTests()
    {
        _connection = new SolidWorksConnection(() => _backend, TimeSpan.FromSeconds(5));
        _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));
    }

    private string Datum => Path.Combine(_root, "Datum");

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task CreatesSavesAndExportsAPart()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));

        var saved = Ok(await DocumentTools.Save(_connection, _workspace, path: "bracket"));
        var exported = Ok(await DocumentTools.Export(_connection, _workspace, "bracket.step"));

        Assert.Equal($"Saved {Path.Combine(Datum, "bracket.sldprt")}\n", saved);
        Assert.StartsWith("Exported bracket.sldprt to ", exported, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Datum, "bracket.step")));
    }

    [Fact]
    public async Task NewWithAPathSavesImmediately()
    {
        var created = Ok(await DocumentTools.New(_connection, _workspace, "assembly", "rig.SLDASM"));

        Assert.Matches(@"^Created d\d+\* rig\.SLDASM \[assembly\] " + Regex.Escape(Path.Combine(Datum, "rig.SLDASM")) + ", editable\n$", created);
    }

    [Fact]
    public async Task RefusesToOverwriteWithoutConsent()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part", "a.SLDPRT"));
        Ok(await DocumentTools.Export(_connection, _workspace, "a.step"));

        var refused = await DocumentTools.Export(_connection, _workspace, "a.step");
        var replaced = Ok(await DocumentTools.Export(_connection, _workspace, "a.step", overwrite: true));

        Assert.StartsWith("FILE_EXISTS: ", Error(refused), StringComparison.Ordinal);
        Assert.Contains("previous file backed up to", replaced, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlySavesDocumentsTheUserAllowed()
    {
        var file = Path.Combine(Datum, "theirs.SLDPRT");
        Directory.CreateDirectory(Datum);
        File.WriteAllText(file, "user's original");
        Ok(await DocumentTools.Open(_connection, _workspace, file));

        var refused = await DocumentTools.Save(_connection, _workspace);
        var allowed = Ok(await DocumentTools.AllowEdit(_connection, _workspace, "theirs.SLDPRT"));
        Ok(await DocumentTools.Save(_connection, _workspace));

        Assert.StartsWith("NOT_OWNED: ", Error(refused), StringComparison.Ordinal);
        var backup = allowed[(allowed.IndexOf("backed up to ", StringComparison.Ordinal) + 13)..^2];
        Assert.Equal("user's original", File.ReadAllText(backup));
    }

    [Fact]
    public async Task RefusesToWriteOutsideTheAllowedFolders()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));

        var result = await DocumentTools.Save(_connection, _workspace, path: Path.Combine(_root, "outside.SLDPRT"));

        Assert.StartsWith("PATH_NOT_ALLOWED: ", Error(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesTheWrongNativeExtensionAndUnknownExportFormats()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));

        Assert.StartsWith("INVALID_ARGUMENT: a part must be saved as .sldprt", Error(await DocumentTools.Save(_connection, _workspace, path: "x.SLDASM")), StringComparison.Ordinal);
        Assert.StartsWith("INVALID_ARGUMENT: can't export to '.docx'", Error(await DocumentTools.Export(_connection, _workspace, "x.docx")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavingANeverSavedDocumentNeedsAPath()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));

        Assert.StartsWith("INVALID_ARGUMENT: ", Error(await DocumentTools.Save(_connection, _workspace)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesToCloseWithUnsavedChangesUnlessTold()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part", "c.SLDPRT"));
        var id = (await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)).Single().Id;
        _backend.MarkModified(id);

        var refused = await DocumentTools.Close(_connection);
        var closed = Ok(await DocumentTools.Close(_connection, discardChanges: true));

        Assert.StartsWith("UNSAVED_CHANGES: ", Error(refused), StringComparison.Ordinal);
        Assert.Equal($"Closed {id} c.SLDPRT\n", closed);
    }

    [Fact]
    public async Task MissingRelativeFileSaysWhereItLooked()
    {
        var error = Error(await DocumentTools.Open(_connection, _workspace, "box.sldprt"));

        Assert.Equal(
            $"NOT_FOUND: no file at '{Path.Combine(Datum, "box.sldprt")}'. Hint: Relative paths are looked up in {Datum}; pass the full path for files elsewhere",
            error);
    }

    [Fact]
    public async Task MissingAbsoluteFileIsNotFound()
    {
        var missing = Path.Combine(_root, "nowhere", "part.SLDPRT");

        Assert.Equal($"NOT_FOUND: no file at '{missing}'", Error(await DocumentTools.Open(_connection, _workspace, missing)));
    }

    [Fact]
    public async Task ReportsUnknownDocuments()
    {
        Assert.StartsWith("NOT_FOUND: no open document 'nope'", Error(await DocumentTools.Activate(_connection, _workspace, "nope")), StringComparison.Ordinal);
        Assert.StartsWith("NOT_FOUND: no document is active", Error(await DocumentTools.Close(_connection)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListsDocumentsWithIdsAndEditability()
    {
        Assert.Equal("No documents open\n", Ok(await DocumentTools.List(_connection, _workspace, CancellationToken.None)));
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        Ok(await DocumentTools.New(_connection, _workspace, "drawing"));

        var lines = Ok(await DocumentTools.List(_connection, _workspace, CancellationToken.None)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^d\d+ Part1 \[part\] not saved yet, editable$", lines[0]);
        Assert.Matches(@"^d\d+\* Drawing2 \[drawing\] not saved yet, editable$", lines[1]);
    }

    private static string Ok(CallToolResult result)
    {
        Assert.True(result.IsError != true, StatusToolsTests.TextOf(result));
        return StatusToolsTests.TextOf(result);
    }

    private static string Error(CallToolResult result)
    {
        Assert.True(result.IsError);
        return StatusToolsTests.TextOf(result);
    }
}
