using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

public sealed partial class GatewayTests : IDisposable
{
    private static readonly DatumOptions ReadsOnly = new();
    private static readonly DatumOptions Writes = new() { AllowInvokeWrites = true };

    private readonly string _root = Directory.CreateTempSubdirectory("datum-gateway-").FullName;
    private readonly SolidWorksConnection _connection = new(() => new ComBackend(), TimeSpan.FromSeconds(60));
    private readonly Workspace _workspace;

    public GatewayTests() => _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task SearchFindsThickenFromPlainWords()
    {
        var found = Text(await ApiTools.Search(_connection, "thicken surface"));
        Console.WriteLine(found);

        Assert.Contains("FeatureBossThicken", found.Split('\n')[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListsTheWholeApiFromTheInstall()
    {
        var feature = Ok(await ApiTools.Members(_connection, "IFeature"));
        var fillet = Ok(await ApiTools.Members(_connection, "ISimpleFilletFeatureData2", query: "radius"));
        Console.WriteLine($"{feature[..200]}\n{fillet}");

        Assert.Matches(@"^IFeature: \d{2,} members\n", feature);
        Assert.Contains("GetTypeName2() → string\n", feature, StringComparison.Ordinal);
        Assert.Contains("GetDefinition() → object\n", feature, StringComparison.Ordinal);
        Assert.Contains("DefaultRadius { get; set; } double\n", fillet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadsAndChainsThroughHandles() => await InNewPart(async () =>
    {
        var boss = await Block();

        var type = Ok(await Call(ReadsOnly, boss, "GetTypeName2"));
        var title = Ok(await Call(ReadsOnly, "doc", "GetTitle"));
        var extension = Ok(await Call(ReadsOnly, "doc", "Extension"));
        var handle = ObjHandle().Match(extension).Value;
        var mass = Ok(await Call(ReadsOnly, handle, "GetMassProperties2", [1, false]));
        var prompt = Ok(await Call(ReadsOnly, "app", "GetUserPreferenceToggle", ["swInputDimValOnCreate"]));
        Console.WriteLine($"{type}{title}{extension}{mass}{prompt}");

        Assert.Equal($"{boss}.GetTypeName2 → \"Extrusion\"\n", type);
        Assert.StartsWith("doc.GetTitle → \"Part", title, StringComparison.Ordinal);
        Assert.Matches(@"→ obj#\d+ \(I?ModelDocExtension\)\n$", extension);
        Assert.Contains("· out Status = 0", mass, StringComparison.Ordinal);
        Assert.Matches(@"→ (true|false)\n$", prompt);
    });

    [Fact]
    public async Task ChangesAFilletRadiusThroughTheGatewayAlone() => await InNewPart(async () =>
    {
        await Block();
        var fillet = FeatHandle().Match(Ok(await FeatureTools.Fillet(_connection, _workspace, await VerticalEdges(), 2))).Value;

        var definition = Ok(await Call(Writes, fillet, "GetDefinition"));
        var data = ObjHandle().Match(definition).Value;
        Ok(await Call(Writes, data, "AccessSelections", ["doc", null]));
        Ok(await Call(Writes, data, "DefaultRadius", value: "3 mm"));
        var modified = Ok(await Call(Writes, fillet, "ModifyDefinition", [data, "doc", null]));
        var dimensions = Ok(await EditTools.Dimensions(_connection));
        var info = Ok(await InspectionTools.Info(_connection));
        Console.WriteLine($"{definition}{modified}{dimensions}{info}");

        // Each vertical edge of the 50 × 30 × 10 block loses (1 - π/4)·r²·10 mm³; model_info rounds to 4 figures.
        var volume = double.Parse(Volume().Match(info).Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.Contains("(ISimpleFilletFeatureData2)", definition, StringComparison.Ordinal);
        Assert.StartsWith($"{fillet}.ModifyDefinition → true · volume ", modified, StringComparison.Ordinal);
        Assert.Contains("rebuild ok", modified, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^D1@Fillet1 3 mm", dimensions);
        Assert.Equal(15000 - (4 * (1 - (Math.PI / 4)) * 9 * 10), volume, tolerance: 5);
    });

    [Fact]
    public async Task RefusesWritesWhenTheyAreOff() => await InNewPart(async () =>
    {
        await Block();
        var fillet = FeatHandle().Match(Ok(await FeatureTools.Fillet(_connection, _workspace, await VerticalEdges(), 2))).Value;
        var before = Ok(await InspectionTools.Info(_connection));
        var data = ObjHandle().Match(Ok(await Call(ReadsOnly, fillet, "GetDefinition"))).Value;

        var refused = await Call(ReadsOnly, data, "DefaultRadius", value: "3 mm");

        Assert.StartsWith("NOT_ALLOWED: DefaultRadius may change the model", Text(refused), StringComparison.Ordinal);
        Assert.Equal(before, Ok(await InspectionTools.Info(_connection)));
    });

    [Fact]
    public async Task ExplainsUnknownAndRefusedMembers() => await InNewPart(async () =>
    {
        await Block();

        var unknown = await Call(Writes, "doc", "NoSuchMember");
        var refused = await Call(Writes, "doc", "CloseDoc", ["x"]);

        Assert.StartsWith("NOT_FOUND: IModelDoc2 has no member NoSuchMember", Text(unknown), StringComparison.Ordinal);
        Assert.StartsWith("NOT_ALLOWED: CloseDoc isn't available through invoke", Text(refused), StringComparison.Ordinal);
    });

    private Task<CallToolResult> Call(DatumOptions options, string target, string member, object?[]? args = null, object? value = null) =>
        ApiTools.Invoke(
            _connection,
            _workspace,
            options,
            target,
            member,
            args?.Select(a => JsonSerializer.SerializeToElement(a)).ToArray(),
            value is null ? null : JsonSerializer.SerializeToElement(value));

    private async Task InNewPart(Func<Task> test)
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        try
        {
            await test();
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    private async Task<string> Block()
    {
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 50, Height = 30 }]));
        var sketch = FeatHandle().Match(Ok(await SketchTools.End(_connection, _workspace))).Value;
        return FeatHandle().Match(Ok(await FeatureTools.Extrude(_connection, _workspace, sketch, depth: 10))).Value;
    }

    private async Task<string[]> VerticalEdges() =>
        [.. Ok(await InspectionTools.Faces(_connection, kind: "edge", direction: "z", limit: 100)).Split('\n')
            .Where(l => l.StartsWith("edge#", StringComparison.Ordinal)).Select(l => l.Split(' ')[0])];

    private static string Ok(CallToolResult result)
    {
        Assert.True(result.IsError != true, Text(result));
        return Text(result);
    }

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    [GeneratedRegex(@"feat#\d+")]
    private static partial Regex FeatHandle();

    [GeneratedRegex(@"obj#\d+")]
    private static partial Regex ObjHandle();

    [GeneratedRegex(@"volume ([\d.]+) mm³")]
    private static partial Regex Volume();
}
