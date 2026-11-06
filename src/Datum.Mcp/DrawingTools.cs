using System.ComponentModel;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class DrawingTools
{
    private const string Toolset = "drawing";

    private static readonly TimeSpan DrawingTimeout = TimeSpan.FromMinutes(2);

    [Toolset(Toolset)]
    [McpServerTool(Name = "drawing_new", Destructive = false, OpenWorld = false)]
    [Description("Start a drawing with a sheet size, scale and projection, using the install's title block for that size. With model set, also adds front, top, right and isometric views. "
        + "Checkpoints can't undo drawing changes; a drawing is quick to redo.")]
    public static Task<CallToolResult> New(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("The .sldprt or .sldasm to draw; leave out to add views later")] string? model = null,
        [Description("a, a-portrait, b, c, d, e, a4, a4-portrait, a3, a2, a1 or a0")] string sheet = "a",
        [Description("Sheet scale, like 1:2")] string scale = "1:1",
        [Description("first or third angle")] string projection = "third",
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var spec = new DrawingSpec(sheet, scale, projection);
                spec.Validate();
                var file = model is null ? null : workspace.ResolveReadPath(model);
                var created = await client.RunAsync(b => b.NewDrawing(spec), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                workspace.AllowEdits(created.Document.Id);
                var report = $"{created.Document.Id} {created.Document.Title}: {Numbers.Format(created.WidthMm)} x {Numbers.Format(created.HeightMm)} mm, {scale}, {(created.FirstAngle ? "first" : "third")} angle, "
                    + $"{(created.Format is null ? "no title block found for this size" : "with title block")}\n";
                if (file is null)
                {
                    return report;
                }

                var views = await client.RunAsync(b => b.AddStandardViews(created.Document.Id, file), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                return report + string.Concat(views.Select(v => $"{v}\n"));
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "view_add", Destructive = false, OpenWorld = false)]
    [Description("Add a view of a part or assembly at a sheet position in mm, measured from the sheet's bottom left. Orientation 'standard' adds front, top, right and isometric, and ignores the position.")]
    public static Task<CallToolResult> AddView(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("The .sldprt or .sldasm")] string model,
        [Description("standard, front, back, left, right, top, bottom, iso, trimetric or dimetric")] string orientation = "standard",
        [Description("Sheet X of the view's centre in mm")] double x = 100,
        [Description("Sheet Y of the view's centre in mm")] double y = 100,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditableAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var file = workspace.ResolveReadPath(model);
                IReadOnlyList<ViewInfo> views = orientation.Trim().Equals("standard", StringComparison.OrdinalIgnoreCase)
                    ? await client.RunAsync(b => b.AddStandardViews(target.Id, file), DrawingTimeout, cancellationToken).ConfigureAwait(false)
                    : [await client.RunAsync(b => b.AddView(target.Id, file, orientation, x, y), DrawingTimeout, cancellationToken).ConfigureAwait(false)];
                Record(solidWorks, target, "added views");
                return string.Concat(views.Select(v => $"{v}\n"));
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "view_list", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List a drawing's views: handle, kind, scale, position and size in mm, display mode, and counts of dimensions, centre marks and centre lines.")]
    public static Task<CallToolResult> ListViews(
        SolidWorksConnection solidWorks,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DrawingAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var views = await client.RunAsync(b => b.ListViews(target.Id), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                return views.Count == 0 ? "No views\n" : string.Concat(views.Select(v => $"{v}\n"));
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "view_set", Destructive = false, OpenWorld = false)]
    [Description("Move a view, change its scale, or change its display, and read the result back. Display hidden shows hidden lines; removed hides them.")]
    public static Task<CallToolResult> SetView(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("View handle, view#N")] string view,
        [Description("Sheet X of the view's centre in mm")] double? x = null,
        [Description("Sheet Y of the view's centre in mm")] double? y = null,
        [Description("View scale, like 1:2")] string? scale = null,
        [Description("hidden, removed, wireframe or shaded")] string? display = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                if (scale is not null)
                {
                    _ = DrawingScale.Parse(scale);
                }

                if (display is not null)
                {
                    _ = ViewDisplays.Parse(display);
                }

                var target = await EditableAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var changed = await client.RunAsync(b => b.SetView(target.Id, view, x, y, scale, display), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                Record(solidWorks, target, $"changed {view}");
                return $"{changed}\n";
            },
            cancellationToken));

    internal static async Task<DocumentInfo> DrawingAsync(SolidWorksClient client, string? doc, CancellationToken cancellationToken)
    {
        var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
        return target.Type == DocumentType.Drawing
            ? target
            : throw new DatumException(ErrorCode.InvalidArgument, $"{target.Id} is not a drawing", "Call drawing_new, or doc_activate an open drawing");
    }

    private static async Task<DocumentInfo> EditableAsync(SolidWorksClient client, Workspace workspace, string? doc, CancellationToken cancellationToken)
    {
        var target = await DrawingAsync(client, doc, cancellationToken).ConfigureAwait(false);
        DocumentTools.RequireEditable(workspace, target);
        return target;
    }

    private static void Record(SolidWorksConnection solidWorks, DocumentInfo target, string label) =>
        solidWorks.Checkpoints.Record(target.Id, label, new NoReversal("a drawing change"));
}
