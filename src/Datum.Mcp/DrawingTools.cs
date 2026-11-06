using System.ComponentModel;
using System.Globalization;
using System.Text;
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
        [Description("mm or inches, for the dimensions shown")] string units = "mm",
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var spec = new DrawingSpec(sheet, scale, projection, units);
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
        [Description("The model configuration to show")] string? configuration = null,
        [Description("true to show the configuration's exploded view, false to collapse it")] bool? exploded = null,
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
                var before = await client.RunAsync(b => b.ListViews(target.Id), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                var changed = await client.RunAsync(b => b.SetView(target.Id, view, x, y, scale, display, configuration, exploded), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                Record(solidWorks, target, $"changed {view}");
                var facts = await client.RunAsync(b => b.ReadDrawingFacts(target.Id), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                var report = new StringBuilder($"{changed}\n");
                foreach (var other in facts.Views.Select(v => v.View).Where(v => v.Handle != changed.Handle))
                {
                    if (before.FirstOrDefault(b => b.Handle == other.Handle) is { } old && (Math.Abs(old.X - other.X) > 0.01 || Math.Abs(old.Y - other.Y) > 0.01))
                    {
                        report.Append(CultureInfo.InvariantCulture, $"{other.Handle} {other.Name} moved with it, now at {Numbers.Format(other.X)},{Numbers.Format(other.Y)} mm\n");
                    }
                }

                foreach (var name in DrawingAudit.OffSheet(facts))
                {
                    report.Append(CultureInfo.InvariantCulture, $"WARNING: {name} now runs off the sheet\n");
                }

                return report.ToString();
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "view_delete", Destructive = true, OpenWorld = false)]
    [Description("Delete a view from the drawing. Sections and details cut from it go too. The model isn't touched. A checkpoint can't bring it back.")]
    public static Task<CallToolResult> DeleteView(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("View handle, view#N")] string view,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditableAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var gone = await client.RunAsync(b => b.DeleteView(target.Id, view), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                Record(solidWorks, target, $"deleted {view}");
                return $"Deleted {string.Join(", ", gone)}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "sheet_set", Destructive = false, OpenWorld = false)]
    [Description("Change the sheet's scale, which the title block shows. Views following the sheet scale change with it; views given their own scale with view_set don't.")]
    public static Task<CallToolResult> SetSheet(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Sheet scale, like 1:2")] string scale,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                _ = DrawingScale.Parse(scale);
                var target = await EditableAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var set = await client.RunAsync(b => b.SetSheetScale(target.Id, scale), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                Record(solidWorks, target, $"set the sheet scale to {set}");
                var views = await client.RunAsync(b => b.ListViews(target.Id), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                return $"Sheet scale {set}\n{string.Concat(views.Select(v => $"{v}\n"))}";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "view_entities", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List the straight edges and circles a view shows, in sheet mm, with edge# handles for view_auxiliary and dimension_add. A circle gives its centre. Hidden edges aren't listed.")]
    public static Task<CallToolResult> Entities(
        SolidWorksConnection solidWorks,
        [Description("View handle, view#N")] string view,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DrawingAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var entities = await client.RunAsync(b => b.ListViewEntities(target.Id, view), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                return entities.Count == 0 ? "No visible edges\n" : string.Concat(entities.Select(e => $"{e}\n"));
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "view_section", Destructive = false, OpenWorld = false)]
    [Description("Cut a section through a view and place it at a sheet position. The cutting line runs horizontally or vertically across the parent view, offset in model mm from the view's centre. A section stays aligned with its parent, so one coordinate may not land where asked; the reply gives the real position.")]
    public static Task<CallToolResult> Section(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Parent view, view#N")] string parent,
        [Description("horizontal or vertical")] string direction,
        [Description("Model mm from the parent view's centre")] double offset,
        [Description("Sheet X of the new view's centre in mm")] double x,
        [Description("Sheet Y of the new view's centre in mm")] double y,
        [Description("Section label, e.g. A; SOLIDWORKS picks one if left out")] string? label = null,
        [Description("Look from the other side")] bool flip = false,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var vertical = direction.Trim().ToUpperInvariant() switch
                {
                    "VERTICAL" => true,
                    "HORIZONTAL" => false,
                    _ => throw new DatumException(ErrorCode.InvalidArgument, $"unknown direction '{direction}'", "Use horizontal or vertical"),
                };
                var target = await EditableAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var made = await client.RunAsync(b => b.AddSectionView(target.Id, parent, vertical, offset, x, y, label, flip), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                Record(solidWorks, target, $"added {made.Name}");
                return $"{made}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "view_detail", Destructive = false, OpenWorld = false)]
    [Description("Enlarge part of a view: a detail circle, in model mm from the parent view's centre, shown as a new view at its own scale.")]
    public static Task<CallToolResult> Detail(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Parent view, view#N")] string parent,
        [Description("Circle centre X in model mm from the view's centre")] double atX,
        [Description("Circle centre Y in model mm from the view's centre")] double atY,
        [Description("Circle radius in model mm")] double radius,
        [Description("Detail scale, like 2:1")] string scale,
        [Description("Sheet X of the new view's centre in mm")] double x,
        [Description("Sheet Y of the new view's centre in mm")] double y,
        [Description("Detail label, e.g. B; SOLIDWORKS picks one if left out")] string? label = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                _ = DrawingScale.Parse(scale);
                if (radius <= 0)
                {
                    throw new DatumException(ErrorCode.InvalidArgument, "a detail circle needs a radius above 0");
                }

                var target = await EditableAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var made = await client.RunAsync(b => b.AddDetailView(target.Id, parent, atX, atY, radius, scale, x, y, label), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                Record(solidWorks, target, $"added {made.Name}");
                return $"{made}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "view_auxiliary", Destructive = false, OpenWorld = false)]
    [Description("Project an auxiliary view square to a straight edge of a view, to show an inclined face true size. Get the edge from view_entities.")]
    public static Task<CallToolResult> Auxiliary(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Parent view, view#N")] string parent,
        [Description("A straight edge# from view_entities")] string edge,
        [Description("Sheet X of the new view's centre in mm")] double x,
        [Description("Sheet Y of the new view's centre in mm")] double y,
        [Description("View label; SOLIDWORKS picks one if left out")] string? label = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditableAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var made = await client.RunAsync(b => b.AddAuxiliaryView(target.Id, parent, edge, x, y, label), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                Record(solidWorks, target, $"added {made.Name}");
                return $"{made}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "dimensions_import", Destructive = false, OpenWorld = false)]
    [Description("Bring the model's dimensions onto the drawing, without repeats, in one view or all of them. Check drawing_check afterwards: an import doesn't prove every size is shown.")]
    public static Task<CallToolResult> ImportDimensions(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("View handle, view#N; leave out for every view")] string? view = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditableAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var views = await client.RunAsync(b => b.ImportDimensions(target.Id, view), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                Record(solidWorks, target, "imported dimensions");
                return string.Concat(views.Select(v => $"{v.Handle} {v.Name}: {v.Dimensions} dimensions\n"));
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "dimension_list", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List the dimensions shown on a drawing with their view, name, value and tolerance. Names work with dimension_tolerance.")]
    public static Task<CallToolResult> ListDimensions(
        SolidWorksConnection solidWorks,
        [Description("View handle, view#N; leave out for every view")] string? view = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DrawingAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var dimensions = await client.RunAsync(b => b.ListSheetDimensions(target.Id, view), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                return dimensions.Count == 0 ? "No dimensions\n" : string.Concat(dimensions.Select(d => $"{d}\n"));
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "dimension_add", Destructive = false, OpenWorld = false)]
    [Description("Add a reference dimension an import missed: a length, diameter or radius from one edge, or a distance or angle between two. Edges come from view_entities; x and y place the text in sheet mm.")]
    public static Task<CallToolResult> AddDimension(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("View handle, view#N")] string view,
        [Description("An edge# from view_entities")] string first,
        [Description("Sheet X of the text in mm")] double x,
        [Description("Sheet Y of the text in mm")] double y,
        [Description("A second edge#, to dimension between the two")] string? second = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditableAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var added = await client.RunAsync(b => b.AddSheetDimension(target.Id, view, first, second, x, y), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                Record(solidWorks, target, "added a dimension");
                return $"{added}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "dimension_tolerance", Destructive = false, OpenWorld = false)]
    [Description("Set a dimension's tolerance in mm and read it back. SOLIDWORKS keeps tolerances on the model's dimension, so the model has unsaved changes afterwards and needs saving too.")]
    public static Task<CallToolResult> Tolerance(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Dimension name from dimension_list, e.g. D1@Sketch1")] string dimension,
        [Description("bilateral, symmetric, limit or none")] string kind,
        [Description("Upper deviation in mm")] double upper = 0,
        [Description("Lower deviation in mm; negative below nominal. Ignored for symmetric")] double lower = 0,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                _ = Tolerances.Code(kind);
                var target = await EditableAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var change = await client.RunAsync(b => b.SetTolerance(target.Id, dimension, kind, upper, lower), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                workspace.MarkModelUnsaved(change.ModelPath);
                Record(solidWorks, target, $"set a tolerance on {dimension}");
                return $"{change.Dimension} {change.Kind} +{Numbers.Format(change.Upper)} {Numbers.Format(change.Lower)} mm. Stored in {Path.GetFileName(change.ModelPath)}, which now needs saving\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "marks_add", Destructive = false, OpenWorld = false)]
    [Description("Add a centre mark to every hole seen end-on and a centre line to every hole seen side-on in a view, skipping ones already there, and report what's still missing.")]
    public static Task<CallToolResult> Marks(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("View handle, view#N")] string view,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditableAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var marks = await client.RunAsync(b => b.AddMarks(target.Id, view), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                Record(solidWorks, target, $"added marks to {view}");
                var missing = marks.MissingMarks + marks.MissingLines == 0 ? "nothing missing" : $"still missing {marks.MissingMarks} centre marks and {marks.MissingLines} centre lines";
                return $"{view}: {marks.CentreMarks} centre marks, {marks.CentreLines} centre lines, {missing}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "note_add", Destructive = false, OpenWorld = false)]
    [Description("Add a note at a sheet position in mm, on the sheet or in a view so it moves with that view.")]
    public static Task<CallToolResult> Note(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("The note text")] string text,
        [Description("Sheet X in mm")] double x,
        [Description("Sheet Y in mm")] double y,
        [Description("View handle, view#N; leave out for a sheet note")] string? view = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new DatumException(ErrorCode.InvalidArgument, "a note needs text");
                }

                var target = await EditableAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var written = await client.RunAsync(b => b.AddNote(target.Id, text, x, y, view), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                Record(solidWorks, target, "added a note");
                return $"Note: {written}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "annotation_list", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List the dimensions and notes on a view or the whole sheet, each with its name and sheet position in mm. The sheet format's own text isn't listed. Names work with annotation_move and annotation_delete.")]
    public static Task<CallToolResult> ListAnnotations(
        SolidWorksConnection solidWorks,
        [Description("View handle, view#N; leave out for the whole sheet")] string? view = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DrawingAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var annotations = await client.RunAsync(b => b.ListAnnotations(target.Id, view), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                return annotations.Count == 0 ? "No annotations\n" : string.Concat(annotations.Select(a => $"{a}\n"));
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "annotation_move", Destructive = false, OpenWorld = false)]
    [Description("Move a dimension or note to a sheet position in mm, and read it back. Use it to clear overlaps drawing_check reports.")]
    public static Task<CallToolResult> MoveAnnotation(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Name from annotation_list or dimension_list")] string name,
        [Description("Sheet X in mm")] double x,
        [Description("Sheet Y in mm")] double y,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditableAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var moved = await client.RunAsync(b => b.MoveAnnotation(target.Id, name, x, y), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                Record(solidWorks, target, $"moved {name}");
                return $"{moved}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "annotation_delete", Destructive = true, OpenWorld = false)]
    [Description("Remove a dimension or note from the drawing. A model dimension stays in the model; it just stops showing here. A checkpoint can't bring it back.")]
    public static Task<CallToolResult> DeleteAnnotation(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Name from annotation_list or dimension_list")] string name,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditableAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                await client.RunAsync(b => { b.DeleteAnnotation(target.Id, name); return true; }, DrawingTimeout, cancellationToken).ConfigureAwait(false);
                Record(solidWorks, target, $"deleted {name}");
                return $"Deleted {name}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "drawing_check", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Audit a drawing before calling it finished: hidden lines, centre marks on holes, centre lines on side-on holes, every model dimension shown, dimensions and notes attached to something, "
        + "views and text inside the border and clear of each other and the title block, the title block's scale, the BOM against the assembly, and every balloon against its component. Run it after every round of changes.")]
    public static Task<CallToolResult> Check(
        SolidWorksConnection solidWorks,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DrawingAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var facts = await client.RunAsync(b => b.ReadDrawingFacts(target.Id), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                return DrawingAudit.Report(target.Title, facts);
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "title_set", Destructive = false, OpenWorld = false)]
    [Description("Fill in the title block. Fields such as DrawnBy, Revision and COMPANYNAME are stored on the drawing. Description, Material and Finish come from the model, so they change the model, which then needs saving. "
        + "An unknown field gets a list of the ones this sheet has. The reply says whether the text now shows on the sheet.")]
    public static Task<CallToolResult> Title(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Fields to set")] TitleField[] fields,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditableAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var report = new StringBuilder();
                foreach (var field in fields)
                {
                    var change = await client.RunAsync(b => b.SetTitleField(target.Id, field.Name, field.Value), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                    if (change.ModelPath is { } model)
                    {
                        workspace.MarkModelUnsaved(model);
                    }

                    report.Append(CultureInfo.InvariantCulture, $"{change.Name} = {change.Value}, {(change.Shown ? "showing" : "NOT showing on the sheet")}{(change.ModelPath is null ? "" : $"; stored in {Path.GetFileName(change.ModelPath)}, which now needs saving")}\n");
                }

                Record(solidWorks, target, "filled in the title block");
                return report.ToString();
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "bom_add", Destructive = false, OpenWorld = false)]
    [Description("Add a BOM table for a view of an assembly, its top left corner at a sheet position in mm, and read its rows back. Leave room: the standard table is about 190 mm wide. drawing_check flags a table running off the sheet.")]
    public static Task<CallToolResult> AddBom(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("View handle, view#N")] string view,
        [Description("Sheet X of the top left corner in mm")] double x,
        [Description("Sheet Y of the top left corner in mm")] double y,
        [Description("A .sldbomtbt template; defaults to the install's standard one")] string? template = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditableAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var rows = await client.RunAsync(b => b.AddBom(target.Id, view, x, y, template), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                Record(solidWorks, target, "added a BOM");
                return BomText(rows);
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "bom_read", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Read the BOM table's rows as they appear on the sheet.")]
    public static Task<CallToolResult> ReadBom(
        SolidWorksConnection solidWorks,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DrawingAsync(client, doc, cancellationToken).ConfigureAwait(false);
                return BomText(await client.RunAsync(b => b.ReadBomTable(target.Id), DrawingTimeout, cancellationToken).ConfigureAwait(false));
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "balloons_add", Destructive = false, OpenWorld = false)]
    [Description("Add BOM balloons to a view of an assembly and report which component each points to. Add the BOM first. Balloons can renumber the BOM, so bom_read afterwards.")]
    public static Task<CallToolResult> Balloons(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("View handle, view#N")] string view,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditableAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var balloons = await client.RunAsync(b => b.AddBalloons(target.Id, view), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                Record(solidWorks, target, "added balloons");
                return string.Concat(balloons.Select(b => $"Balloon {b.Item} points at {b.Component ?? "nothing"}{(b.Dangling ? " (dangling)" : "")}\n"));
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "explode_lines", Destructive = false, OpenWorld = false)]
    [Description("Add explode lines to an assembly's active exploded configuration, one for each move of each exploded component, and report where each runs. Run this on the assembly, not the drawing; drawing views of that configuration then show them.")]
    public static Task<CallToolResult> ExplodeLines(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await AssemblyTools.AssemblyAsync(client, doc, cancellationToken).ConfigureAwait(false);
                DocumentTools.RequireEditable(workspace, target);
                var lines = await client.RunAsync(b => b.AddExplodeLines(target.Id), DrawingTimeout, cancellationToken).ConfigureAwait(false);
                solidWorks.Checkpoints.Record(target.Id, "added explode lines", new NoReversal("explode lines"));
                return $"Added {lines.Count} explode lines\n{string.Concat(lines.Select(l => $"{l}\n"))}";
            },
            cancellationToken));

    private static string BomText(IReadOnlyList<BomRow> rows) =>
        rows.Count == 0
            ? "The BOM has no rows\n"
            : string.Concat(rows.Select(r => $"{r.Item}: {r.Quantity} x {r.PartNumber}{(r.Description.Length == 0 ? "" : $" {r.Description}")}\n"));

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
