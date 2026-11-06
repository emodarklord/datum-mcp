using System.ComponentModel;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class ViewTools
{
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(60);

    private static readonly StandardView[] SheetViews = [StandardView.Front, StandardView.Top, StandardView.Right, StandardView.Isometric];

    [McpServerTool(Name = "view_capture", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Look at a part, assembly or drawing sheet: a PNG of one standard view, or 'sheet' for front, top, right and isometric in one image. "
        + "Use it to check a change looks right. Selected entities (model_select) show highlighted. The user's view is put back afterwards. "
        + "mode=hidden shows hidden lines, section cuts the view open to show the inside, and labels=faces or edges draws handle numbers on one view, "
        + "so a feature seen in the picture can be matched to its handle. The default size costs about 640 image tokens; larger sizes cost more.")]
    public static Task<CallToolResult> Capture(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("front, back, left, right, top, bottom, iso, trimetric, dimetric or sheet")] string view = "iso",
        [Description("Image width in pixels, 200 to 1600; the height is three quarters of it")] int size = 800,
        [Description("Also save the PNG to this file; relative paths go in the Datum folder")] string? path = null,
        [Description("Replace an existing file (it is backed up first)")] bool overwrite = false,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        [Description("shaded, hidden (hidden lines shown) or wireframe")] string mode = "shaded",
        [Description("Cut the view open along front, top or right; the model isn't changed")] string? section = null,
        [Description("Where the section plane sits, in mm from that plane")] double sectionOffset = 0,
        [Description("faces or edges: draw handle numbers. Only what the view can see is labelled, up to 30 items")] string? labels = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync<IList<ContentBlock>>(
            async client =>
            {
                if (size is < 200 or > 1600)
                {
                    throw new DatumException(ErrorCode.InvalidArgument, $"size {size} is out of range", "Use 200 to 1600 pixels");
                }

                var sheet = string.Equals(view.Trim(), "sheet", StringComparison.OrdinalIgnoreCase);
                var options = CaptureOptions.Parse(mode, section, sectionOffset, labels, sheet);
                StandardView[] views = sheet ? SheetViews : [Views.Parse(view)];
                var file = path is null ? null : workspace.ResolveWritePath(path);
                if (file is not null && !string.Equals(Path.GetExtension(file), ".png", StringComparison.OrdinalIgnoreCase))
                {
                    throw new DatumException(ErrorCode.InvalidArgument, "captures are saved as PNG", "Give path a .png extension");
                }

                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                if (target.Type == DocumentType.Drawing)
                {
                    if (options != new CaptureOptions() || !view.Trim().Equals("iso", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DatumException(ErrorCode.InvalidArgument, $"{target.Id} is a drawing", "A drawing captures as its whole sheet; leave view, mode, section and labels unset");
                    }

                    var sheetImage = await client.RunAsync(b => b.CaptureSheet(target.Id, size, size * 3 / 4), CaptureTimeout, cancellationToken).ConfigureAwait(false);
                    var sheetPng = Png.Encode(sheetImage.Views[0]);
                    var sheetText = $"{target.Title} sheet, {sheetImage.Views[0].Width}×{sheetImage.Views[0].Height}";
                    if (file is not null)
                    {
                        var sheetBackup = DocumentTools.Replace(workspace, file, overwrite);
                        await File.WriteAllBytesAsync(file, sheetPng, cancellationToken).ConfigureAwait(false);
                        sheetText += $" · saved to {file}{DocumentTools.BackupNote(sheetBackup)}";
                    }

                    return [new TextContentBlock { Text = sheetText + "\n" }, ImageContentBlock.FromBytes(sheetPng, "image/png")];
                }

                var height = size * 3 / 4;
                var (viewWidth, viewHeight) = sheet ? ((size - 2) / 2, (height - 2) / 2) : (size, height);
                var capture = await client.RunAsync(b => b.CaptureViews(target.Id, views, viewWidth, viewHeight, options), CaptureTimeout, cancellationToken).ConfigureAwait(false);
                var image = sheet ? Raster.Sheet(capture.Views) : capture.Views[0];
                var png = Png.Encode(image);

                var text = sheet
                    ? $"{target.Title} sheet {image.Width}×{image.Height}: front | top / right | isometric, all at one scale"
                    : $"{target.Title} {Views.Name(views[0])}, {image.Width}×{image.Height}";
                if (options.Mode != CaptureMode.Shaded)
                {
                    text += $" · {options.Mode.ToString().ToLowerInvariant()}";
                }

                if (options.Section is { } cut)
                {
                    text += $" · cut along {cut.Plane.ToString().ToLowerInvariant()} at {Numbers.Format(cut.OffsetMm)} mm";
                }

                if (options.Labels != CaptureLabels.None)
                {
                    text += capture.Labels.Count == 0 ? " · nothing to label" : $" · numbers are {string.Join(", ", capture.Labels.Select(l => l.Handle))}";
                }

                if (capture.SketchOpen)
                {
                    text += " · the open sketch isn't drawn; it shows after sketch_end";
                }

                if (file is not null)
                {
                    var backup = DocumentTools.Replace(workspace, file, overwrite);
                    await File.WriteAllBytesAsync(file, png, cancellationToken).ConfigureAwait(false);
                    text += $" · saved to {file}{DocumentTools.BackupNote(backup)}";
                }

                return [new TextContentBlock { Text = text + "\n" }, ImageContentBlock.FromBytes(png, "image/png")];
            },
            cancellationToken));
}
