using System.Text.Json;
using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;
using ModelContextProtocol.Protocol;

namespace Datum.Tests;

public sealed class ApiToolsTests : IDisposable
{
    private static readonly DatumOptions ReadsOnly = new();
    private static readonly DatumOptions Writes = new() { AllowInvokeWrites = true };

    private readonly string _root = Directory.CreateTempSubdirectory("datum-api-").FullName;
    private readonly FakeBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;

    public ApiToolsTests()
    {
        _connection = new SolidWorksConnection(() => _backend, TimeSpan.FromSeconds(5));
        _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));
    }

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ListsMembersAsSignatures()
    {
        await Part();

        var listed = Ok(await ApiTools.Members(_connection, "Feature", query: "name"));

        Assert.Equal("IFeature: 2 members\nGetTypeName2() → string\nName { get; set; } string\n", listed);
    }

    [Fact]
    public async Task ReadsWithoutTheWritesOption()
    {
        var sketch = await Part();

        var read = Ok(await ApiTools.Invoke(_connection, _workspace, ReadsOnly, sketch, "GetTypeName2"));
        var definition = Ok(await ApiTools.Invoke(_connection, _workspace, ReadsOnly, sketch, "GetDefinition"));

        Assert.Equal($"{sketch}.GetTypeName2 → \"ProfileFeature\"\n", read);
        Assert.Matches(@"^feat#\d+\.GetDefinition → obj#\d+ \(IFakeFeatureData\)\n$", definition);
    }

    [Fact]
    public async Task WritesNeedTheOption()
    {
        var sketch = await Part();

        var refused = await ApiTools.Invoke(_connection, _workspace, ReadsOnly, sketch, "Name", value: Json("\"Profile\""));

        Assert.StartsWith("NOT_ALLOWED: Name may change the model", Error(refused), StringComparison.Ordinal);
        Assert.Contains("--allow-invoke-writes", Error(refused), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritesNeedAnEditableDocument()
    {
        var file = Path.Combine(_root, "Datum", "theirs.SLDPRT");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "user's part");
        Ok(await DocumentTools.Open(_connection, _workspace, file));
        var id = (await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)).Single(d => d.Active).Id;
        _backend.GeometryOf(id).Features.Add(new FeatureInfo("feat#900", "Boss1", "Extrusion", 0, false, null));

        var refused = await ApiTools.Invoke(_connection, _workspace, Writes, "feat#900", "Name", value: Json("\"Mine\""));

        Assert.StartsWith("NOT_OWNED: ", Error(refused), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWriteCantBeRestoredPast()
    {
        var sketch = await Part();
        var start = Ok(await EditTools.Create(_connection, "start")).Split(' ')[0];

        var set = Ok(await ApiTools.Invoke(_connection, _workspace, Writes, sketch, "Name", value: Json("\"Profile\"")));
        var list = Ok(await EditTools.List(_connection));
        var restore = await EditTools.Restore(_connection, _workspace, start);

        Assert.StartsWith($"{sketch}.Name → set · volume ", set, StringComparison.Ordinal);
        Assert.EndsWith(" · Datum can't undo this, so checkpoints can't restore past it\n", set, StringComparison.Ordinal);
        Assert.Contains($"invoke {sketch} Name (can't restore past this)", list, StringComparison.Ordinal);
        Assert.StartsWith("CANNOT_RESTORE: ", Error(restore), StringComparison.Ordinal);
        Assert.Contains("can't undo a gateway call to Name", Error(restore), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesSessionOperationsEvenAsReads()
    {
        await Part();

        var refused = await ApiTools.Invoke(_connection, _workspace, Writes, "doc", "CloseDoc");

        Assert.StartsWith("NOT_ALLOWED: CloseDoc isn't available through invoke. Hint: Use doc_close", Error(refused), StringComparison.Ordinal);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private async Task<string> Part()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        return Ok(await SketchTools.End(_connection, _workspace)).Split(' ')[2].TrimEnd(',');
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
