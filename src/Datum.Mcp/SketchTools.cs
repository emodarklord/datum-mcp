using System.ComponentModel;
using System.Text;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class SketchTools
{
    private static readonly TimeSpan SketchTimeout = TimeSpan.FromMinutes(1);

    [McpServerTool(Name = "sketch_begin", Destructive = false, OpenWorld = false)]
    [Description("Start a sketch on Front, Top or Right, or on a plane (feat#) or flat face (face#) handle. "
        + "Coordinates in later sketch calls are mm in this sketch's x/y, which the reply maps to model axes.")]
    public static Task<CallToolResult> Begin(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Front, Top, Right, or a feat#/face# handle")] string on,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditablePartAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var sketch = await client.RunAsync(b => b.BeginSketch(target.Id, on), SketchTimeout, cancellationToken).ConfigureAwait(false);
                return $"Editing {sketch.Name} on {on}: x → {EntityQuery.DescribeDirection(sketch.XAxis)}, "
                    + $"y → {EntityQuery.DescribeDirection(sketch.YAxis)}, origin {sketch.Origin} mm\n";
            },
            cancellationToken));

    [McpServerTool(Name = "sketch_add", Destructive = false, OpenWorld = false)]
    [Description("Add a batch of entities to the sketch being edited, in mm. Set dimension: true on an entity to fully define it "
        + "(size plus position from the sketch origin). The whole batch is checked first, so a bad entity draws nothing.")]
    public static Task<CallToolResult> Add(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Entities to draw")] SketchEntitySpec[] entities,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                SketchSpecs.Validate(entities);
                var target = await EditablePartAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var created = await client.RunAsync(b => b.AddSketchEntities(target.Id, entities), SketchTimeout, cancellationToken).ConfigureAwait(false);
                var sketch = await client.RunAsync(b => b.ReadSketch(target.Id), cancellationToken: cancellationToken).ConfigureAwait(false);
                var report = new StringBuilder();
                foreach (var segment in created)
                {
                    report.Append(Line(segment)).Append('\n');
                }

                return report.Append(Status(sketch)).ToString();
            },
            cancellationToken));

    [McpServerTool(Name = "sketch_constrain", Destructive = false, OpenWorld = false)]
    [Description("Add relations or dimensions between sketch entities (seg#/pt# handles) in the sketch being edited. Dimension values are mm, angles degrees.")]
    public static Task<CallToolResult> Constrain(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Relations and dimensions to add")] SketchConstraintSpec[] constraints,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                SketchSpecs.Validate(constraints);
                var target = await EditablePartAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var sketch = await client.RunAsync(b => b.ConstrainSketch(target.Id, constraints), SketchTimeout, cancellationToken).ConfigureAwait(false);
                return Status(sketch);
            },
            cancellationToken));

    [McpServerTool(Name = "sketch_end", Destructive = false, OpenWorld = false)]
    [Description("Finish the sketch being edited. Returns its feat# handle for features such as extrude.")]
    public static Task<CallToolResult> End(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditablePartAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var sketch = await client.RunAsync(b => b.EndSketch(target.Id), SketchTimeout, cancellationToken).ConfigureAwait(false);
                return $"{sketch.Name} is {sketch.Handle ?? "(no handle)"}, {sketch.Status}, {sketch.Segments} segments\n";
            },
            cancellationToken));

    private static async Task<DocumentInfo> EditablePartAsync(SolidWorksClient client, Workspace workspace, string? doc, CancellationToken cancellationToken)
    {
        var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
        if (target.Type != DocumentType.Part)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"{target.Id} is not a part", "Sketch tools work on parts");
        }

        DocumentTools.RequireEditable(workspace, target);
        return target;
    }

    private static string Line(SketchSegmentInfo s)
    {
        var text = s.Kind switch
        {
            "circle" => $"{s.Handle} circle centre {s.Center} radius {Numbers.Format(s.Radius ?? 0)}",
            "arc" => $"{s.Handle} arc {s.Start} → {s.End} centre {s.Center} radius {Numbers.Format(s.Radius ?? 0)}",
            "point" => $"{s.Handle} point {s.Start}",
            _ => $"{s.Handle} {s.Kind} {s.Start} → {s.End}",
        };
        return s.Construction ? text + " (construction)" : text;
    }

    private static string Status(SketchInfo sketch) => $"{sketch.Name}: {sketch.Segments} segments, {sketch.Status}\n";
}
