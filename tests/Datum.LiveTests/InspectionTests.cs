using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

public sealed class InspectionTests : IDisposable
{
    private const string Sample = @"C:\Users\Public\Documents\SOLIDWORKS\SOLIDWORKS 2026\samples\introsw\box.sldprt";

    private readonly string _root = Directory.CreateTempSubdirectory("datum-inspect-").FullName;
    private readonly SolidWorksConnection _connection = new(() => new ComBackend(), TimeSpan.FromSeconds(60));
    private readonly Workspace _workspace;

    public InspectionTests() => _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task InspectsTheSampleBoxWithStableHandles()
    {
        Assert.True(File.Exists(Sample), $"Sample part missing: {Sample}");
        Ok(await DocumentTools.Open(_connection, _workspace, Sample, readOnly: true));
        try
        {
            var dirtyAfterOpen = await IsModified();
            var tree = Ok(await InspectionTools.Tree(_connection));
            var info = Ok(await InspectionTools.Info(_connection));
            var faces = Ok(await InspectionTools.Faces(_connection));
            var again = Ok(await InspectionTools.Faces(_connection));
            var edges = Ok(await InspectionTools.Faces(_connection, kind: "edge", limit: 5));
            Console.WriteLine($"{tree}\n{info}\n{faces}\n{edges}");

            Assert.Contains("[Extrusion]", tree, StringComparison.Ordinal);
            Assert.DoesNotContain("Mass 0 g", info, StringComparison.Ordinal);
            Assert.Contains("Bounding box ", info, StringComparison.Ordinal);
            var faceLines = faces.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(faceLines.Length >= 6, faces);
            Assert.All(faceLines, line => Assert.Matches(@"^face#\d+ ", line));
            Assert.Equal(faces, again);
            Assert.Matches(@"^edge#\d+ ", edges);
            var dirtyAfterInspecting = await IsModified();

            await _connection.UseAsync(c => c.RunAsync(b => ((ComBackend)b).Rebuild(DocumentId(b))), CancellationToken.None);
            Assert.Equal(faces, Ok(await InspectionTools.Faces(_connection)));
            var dirtyAfterRebuild = await IsModified();

            var largest = faceLines[0].Split(' ')[0];
            Ok(await InspectionTools.Select(_connection, [largest]));
            Assert.Equal($"{largest}\n", Ok(await InspectionTools.Selection(_connection)));
            var dirtyAfterSelect = await IsModified();

            Console.WriteLine($"Modified after open={dirtyAfterOpen} inspect={dirtyAfterInspecting} rebuild={dirtyAfterRebuild} select={dirtyAfterSelect}");
            Assert.Equal(dirtyAfterOpen, dirtyAfterInspecting);
            Assert.Equal(dirtyAfterRebuild, dirtyAfterSelect);
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    private static string DocumentId(ISolidWorksBackend backend) => backend.ListDocuments().Single(d => d.Active).Id;

    private async Task<bool> IsModified() =>
        (await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)).Single(d => d.Active).Modified;

    private static string Ok(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError != true, text);
        return text;
    }
}
