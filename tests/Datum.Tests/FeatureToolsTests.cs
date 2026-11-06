using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;
using ModelContextProtocol.Protocol;

namespace Datum.Tests;

public sealed class FeatureToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-feature-").FullName;
    private readonly FakeBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;

    public FeatureToolsTests()
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
    public async Task ReportsTheVolumeChange()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));

        var boss = Ok(await FeatureTools.Extrude(_connection, _workspace, "feat#1", depth: 10));
        var cut = Ok(await FeatureTools.Extrude(_connection, _workspace, "feat#1", cut: true, end: "through_all"));

        Assert.Matches(@"^extrusion1 is feat#\d+ · volume 0 → 1000 mm³ \(\+1000\) · 1 body · rebuild ok\n$", boss);
        Assert.Matches(@"^extruded cut2 is feat#\d+ · volume 1000 → 900 mm³ \(-100\) · 1 body · rebuild ok\n$", cut);
    }

    [Fact]
    public async Task FailedFeaturesReportWhatToTry()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        _backend.FailNextFeature();

        var error = Error(await FeatureTools.Fillet(_connection, _workspace, ["edge#1"], 50));

        Assert.Equal("FEATURE_FAILED: SOLIDWORKS couldn't make the fillet: fake failure. Hint: Try a smaller radius; it must be less than the faces around the edge allow", error);
    }

    [Fact]
    public async Task ValidatesBeforeTouchingSolidWorks()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));

        var error = Error(await FeatureTools.Hole(_connection, _workspace, "face#1", "1,2", 5));

        Assert.StartsWith("INVALID_ARGUMENT: hole needs at as", error, StringComparison.Ordinal);
        Assert.Matches(@"^Part1: 0 features\n$", Ok(await InspectionTools.Tree(_connection)));
    }

    [Fact]
    public async Task RefusesDocumentsDatumMayNotEdit()
    {
        var file = Path.Combine(_root, "Datum", "theirs.SLDPRT");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "user's part");
        Ok(await DocumentTools.Open(_connection, _workspace, file));

        Assert.StartsWith("NOT_OWNED: ", Error(await FeatureTools.Shell(_connection, _workspace, 2, [])), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyExtrudeIsCore()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));

        var found = Ok(await ToolsetTools.Find(new Func<ToolCatalog>(() =>
        {
            var catalog = new ToolCatalog(allTools: false);
            var collection = new ModelContextProtocol.Server.McpServerPrimitiveCollection<ModelContextProtocol.Server.McpServerTool>();
            foreach (var method in typeof(FeatureTools).GetMethods().Where(m => m.IsDefined(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false)))
            {
                collection.Add(ModelContextProtocol.Server.McpServerTool.Create(method, (object?)null));
            }

            catalog.Attach(collection);
            return catalog;
        })()));

        Assert.Contains("core (1): feature_extrude", found, StringComparison.Ordinal);
        Assert.Contains("features (11): feature_revolve, feature_sweep, feature_loft, feature_fillet, feature_chamfer, feature_hole, feature_pattern, plane_create, axis_create, feature_mirror, feature_shell", found, StringComparison.Ordinal);
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
