using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

public sealed class SketchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-sketch-").FullName;
    private readonly SolidWorksConnection _connection = new(() => new ComBackend(), TimeSpan.FromSeconds(60));
    private readonly Workspace _workspace;

    public SketchTests() => _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task DrawsAFullyDefinedProfileInOneCall()
    {
        var promptBefore = await DimensionPrompt();
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        try
        {
            Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
            var added = Ok(await SketchTools.Add(_connection, _workspace,
            [
                new SketchEntitySpec { Type = "rect", X1 = 0, Y1 = 0, X2 = 50, Y2 = 30, Dimension = true },
                new SketchEntitySpec { Type = "circle", Cx = 25, Cy = 15, Radius = 5, Dimension = true },
            ]));
            var ended = Ok(await SketchTools.End(_connection, _workspace));
            var tree = Ok(await InspectionTools.Tree(_connection));
            Console.WriteLine($"{added}\n{ended}\n{tree}");

            Assert.EndsWith("5 segments, fully defined\n", added, StringComparison.Ordinal);
            Assert.Contains("circle centre 25,15 radius 5", added, StringComparison.Ordinal);
            Assert.Matches(@"^Sketch1 is feat#\d+, fully defined, 5 segments\n$", ended);
            Assert.Contains("Sketch1 [ProfileFeature]", tree, StringComparison.Ordinal);
            Assert.Equal(promptBefore, await DimensionPrompt());
            Assert.Empty(new OptionsJournal(OptionsJournal.DefaultPath).Pending());
            Assert.Empty(await NewDialogs());
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    [Fact]
    public async Task DrawsAConnectedOutlineOnTopInSketchCoordinates()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        try
        {
            var begun = Ok(await SketchTools.Begin(_connection, _workspace, "Top"));
            var added = Ok(await SketchTools.Add(_connection, _workspace,
            [
                new SketchEntitySpec { Type = "polyline", Points = [[0, 0], [40, 0], [40, 10], [10, 10], [10, 30], [0, 30]], Closed = true, Dimension = false },
                new SketchEntitySpec { Type = "line", X1 = 0, Y1 = 40, X2 = 20, Y2 = 45, Dimension = false },
            ]));
            var sloped = added.Split('\n').Single(l => l.Contains("0,40 → 20,45", StringComparison.Ordinal)).Split(' ')[0];
            var constrained = Ok(await SketchTools.Constrain(_connection, _workspace,
                [new SketchConstraintSpec { Kind = "horizontal", Entities = [sloped] }]));
            var ended = Ok(await SketchTools.End(_connection, _workspace));
            Console.WriteLine($"{begun}\n{added}\n{constrained}\n{ended}");

            Assert.Contains("x → +x", begun, StringComparison.Ordinal);
            Assert.Contains("y → -z", begun, StringComparison.Ordinal);
            Assert.Contains("40,0 → 40,10", added, StringComparison.Ordinal);
            Assert.Contains("0,30 → 0,0", added, StringComparison.Ordinal);
            Assert.EndsWith("7 segments, under defined\n", added, StringComparison.Ordinal);
            Assert.StartsWith("Sketch1: 7 segments, ", constrained, StringComparison.Ordinal);
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    public static TheoryData<string, SketchEntitySpec[]> EveryKindOfEntity => new()
    {
        { "sloped line", [new SketchEntitySpec { Type = "line", X1 = 5, Y1 = 3, X2 = 25, Y2 = 14 }] },
        { "centerline", [new SketchEntitySpec { Type = "centerline", X1 = 0, Y1 = -10, X2 = 0, Y2 = 40 }] },
        { "revolve profile", [new SketchEntitySpec { Type = "polyline", Points = [[6, 0], [20, 0], [20, 8], [10, 8], [10, 30], [6, 30]], Closed = true }] },
        { "arc", [new SketchEntitySpec { Type = "arc", X1 = 10, Y1 = 0, X2 = 0, Y2 = 10, X3 = 7.0711, Y3 = 7.0711 }] },
        { "slot", [new SketchEntitySpec { Type = "slot", X1 = 10, Y1 = 5, X2 = 40, Y2 = 5, Width = 8 }] },
        {
            "shared points",
            [
                new SketchEntitySpec { Type = "circle", Cx = 20, Cy = 10, Radius = 4 },
                new SketchEntitySpec { Type = "line", X1 = 20, Y1 = 10, X2 = 35, Y2 = 18, Construction = true },
                new SketchEntitySpec { Type = "point", X1 = 35, Y1 = 18 },
            ]
        },
    };

    [Theory]
    [MemberData(nameof(EveryKindOfEntity))]
    public async Task EveryEntityIsFullyDefinedByDefault(string name, SketchEntitySpec[] entities)
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        try
        {
            Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
            var added = Ok(await SketchTools.Add(_connection, _workspace, entities));
            Console.WriteLine($"{name}\n{added}");

            Assert.EndsWith(", fully defined\n", added, StringComparison.Ordinal);
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    private Task<bool> DimensionPrompt() =>
        _connection.UseAsync(c => c.RunAsync(b => ((ComBackend)b).InputDimensionPrompt()), CancellationToken.None);

    private static Task<IReadOnlyList<BlockingDialog>> NewDialogs() =>
        Task.FromResult(new ComBackend().FindBlockingDialogs());

    private static string Ok(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError != true, text);
        return text;
    }
}
