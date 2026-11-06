using System.Globalization;
using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    private const int SheetIndexFirstAngle = 4;
    private const int SheetIndexWidth = 5;
    private const int SheetIndexHeight = 6;

    public DrawingCreated NewDrawing(DrawingSpec spec) => Call(() =>
    {
        var created = NewDocument(DocumentType.Drawing);
        var document = Find(created.Id);
        var drawing = (IDrawingDoc)document;
        var sheet = (ISheet)drawing.GetCurrentSheet();
        var (numerator, denominator) = spec.ParsedScale;
        var format = FindSheetFormat(spec);
        var template = format is null ? (int)swDwgTemplates_e.swDwgTemplateNone : (int)swDwgTemplates_e.swDwgTemplateCustom;
        drawing.SetupSheet5(sheet.GetName(), spec.PaperSize, template, numerator, denominator, spec.FirstAngle, format ?? "", 0, 0, "", true);
        document.EditRebuild3();

        var properties = (double[])((ISheet)drawing.GetCurrentSheet()).GetProperties2();
        return new DrawingCreated(DescribeActive(document), format, properties[SheetIndexWidth] * Mm, properties[SheetIndexHeight] * Mm, properties[SheetIndexFirstAngle] != 0);
    });

    public IReadOnlyList<ViewInfo> AddStandardViews(string id, string model) => Call(() =>
    {
        var document = Find(id);
        var drawing = DrawingOf(document, id);
        var sheet = (ISheet)drawing.GetCurrentSheet();
        var properties = (double[])sheet.GetProperties2();
        var existing = ViewsOf(document).Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        var type = ModelType(model);
        WithLoaded(model, type, () =>
        {
            var made = properties[SheetIndexFirstAngle] != 0 ? drawing.Create1stAngleViews2(model) : drawing.Create3rdAngleViews2(model);
            if (!made)
            {
                throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't make views of '{model}'");
            }

            // Top right is clear of the three projected views at their automatic positions.
            drawing.CreateDrawViewFromModelView3(model, "*Isometric", properties[SheetIndexWidth] * 0.75, properties[SheetIndexHeight] * 0.72, 0);
            return true;
        });

        // SOLIDWORKS picks its own scale when views go in, so the sheet's is set again.
        sheet.SetScale(properties[2], properties[3], false, false);
        document.EditRebuild3();
        return ViewsOf(document).Where(v => !existing.Contains(v.Name)).Select(v => DescribeView(id, document, v)).ToList();
    });

    public ViewInfo AddView(string id, string model, string orientation, double x, double y) => Call(() =>
    {
        var document = Find(id);
        var drawing = DrawingOf(document, id);
        var named = orientation.Trim().ToLowerInvariant() switch
        {
            "iso" or "isometric" => "*Isometric",
            "trimetric" => "*Trimetric",
            "dimetric" => "*Dimetric",
            "front" => "*Front",
            "back" => "*Back",
            "left" => "*Left",
            "right" => "*Right",
            "top" => "*Top",
            "bottom" => "*Bottom",
            _ => throw new DatumException(ErrorCode.InvalidArgument, $"unknown orientation '{orientation}'", "Use front, back, left, right, top, bottom, iso, trimetric or dimetric"),
        };
        var view = WithLoaded(model, ModelType(model), () => drawing.CreateDrawViewFromModelView3(model, named, x / Mm, y / Mm, 0))
            ?? throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't add a {orientation} view of '{model}'");
        document.EditRebuild3();
        return DescribeView(id, document, view);
    });

    public IReadOnlyList<ViewInfo> ListViews(string id) => Call(() =>
    {
        var document = Find(id);
        DrawingOf(document, id);
        return ViewsOf(document).Select(view => DescribeView(id, document, view)).ToList();
    });

    public ViewInfo SetView(string id, string view, double? x, double? y, string? scale, string? display) => Call(() =>
    {
        var document = Find(id);
        var target = ViewOf(id, document, view);
        if (x is not null || y is not null)
        {
            var position = (double[])target.Position;
            target.Position = new[] { (x / Mm) ?? position[0], (y / Mm) ?? position[1] };
        }

        if (scale is not null)
        {
            var (numerator, denominator) = DrawingScale.Parse(scale);
            target.UseSheetScale = 0;
            target.ScaleRatio = new[] { numerator, denominator };
        }

        if (display is not null && !target.SetDisplayMode4(false, ViewDisplays.Parse(display), false, true, false))
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS didn't change the display of {view}");
        }

        document.EditRebuild3();
        var described = DescribeView(id, document, target);
        if (display is not null && described.Display != ViewDisplays.Name(ViewDisplays.Parse(display)))
        {
            throw new DatumException(ErrorCode.SolidworksError, $"{view} shows {described.Display}, not {display}");
        }

        return described;
    });

    public ViewCapture CaptureSheet(string id, int width, int height) => Call(() =>
    {
        var document = Find(id);
        DrawingOf(document, id);
        if (!App.Visible || App.FrameState == (int)swWindowState_e.swWindowMinimized)
        {
            throw new DatumException(ErrorCode.SolidworksError, "the SOLIDWORKS window is minimized or hidden", "Restore the SOLIDWORKS window; captures are rendered on screen");
        }

        var view = (IModelView)document.ActiveView;
        var (renderWidth, renderHeight) = RenderSize(view, width, height);
        var file = Path.Combine(Path.GetTempPath(), $"datum-{Guid.NewGuid():N}.bmp");
        try
        {
            document.ViewZoomtofit2();
            document.GraphicsRedraw2();
            if (!document.SaveBMP(file, renderWidth, renderHeight))
            {
                throw new DatumException(ErrorCode.SolidworksError, "SOLIDWORKS couldn't render the sheet");
            }

            return new ViewCapture([Bmp.Decode(File.ReadAllBytes(file)).FitContent(width, height)], false, []);
        }
        finally
        {
            File.Delete(file);
        }
    });

    private static IDrawingDoc DrawingOf(IModelDoc2 document, string id) =>
        document as IDrawingDoc ?? throw new DatumException(ErrorCode.InvalidArgument, $"{id} is not a drawing", "Call drawing_new, or doc_activate an open drawing");

    // Every sheet's views; GetViews gives each sheet as an array whose first item is the sheet itself.
    private static List<IView> ViewsOf(IModelDoc2 document) =>
        [.. (((IDrawingDoc)document).GetViews() as object[] ?? []).OfType<object[]>().SelectMany(sheet => sheet.Skip(1).OfType<IView>())];

    private IView ViewOf(string id, IModelDoc2 document, string handle) =>
        Resolve(id, document, handle) as IView
            ?? throw new DatumException(ErrorCode.InvalidArgument, $"{handle} is not a view", "Use a view# handle from view_list");

    private ViewInfo DescribeView(string id, IModelDoc2 document, IView view)
    {
        var position = (double[])view.Position;
        var outline = (double[])view.GetOutline();
        var scale = (double[])view.ScaleRatio;
        return new ViewInfo(
            HandleOf(id, document, "view", view),
            view.Name,
            view.Sheet?.GetName() ?? "",
            ((swDrawingViewTypes_e)view.Type).ToString().Replace("swDrawing", "", StringComparison.Ordinal).Replace("View", "", StringComparison.Ordinal).ToLowerInvariant(),
            Path.GetFileName(view.GetReferencedModelName()),
            string.Create(CultureInfo.InvariantCulture, $"{scale[0]:0.###}:{scale[1]:0.###}"),
            position[0] * Mm,
            position[1] * Mm,
            (outline[2] - outline[0]) * Mm,
            (outline[3] - outline[1]) * Mm,
            ViewDisplays.Name(view.GetDisplayMode2()),
            view.GetDisplayDimensionCount(),
            CentreMarksOf(view).Count,
            view.GetCenterLineCount());
    }

    private static List<ICenterMark> CentreMarksOf(IView view)
    {
        var marks = new List<ICenterMark>();
        for (var mark = view.GetFirstCenterMark() as ICenterMark; mark is not null; mark = mark.GetNext() as ICenterMark)
        {
            marks.Add(mark);
        }

        return marks;
    }

    private static DocumentType ModelType(string model)
    {
        var type = DocumentTypes.FromPath(model);
        if (type is null or DocumentType.Drawing)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"'{model}' is not a part or an assembly");
        }

        return File.Exists(model) ? type.Value : throw new DatumException(ErrorCode.NotFound, $"no file at '{model}'");
    }

    // The install's standard format for this size, which brings the title block.
    private string? FindSheetFormat(DrawingSpec spec)
    {
        var folders = App.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsSheetFormat)?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var name = spec.SheetName.EndsWith("-portrait", StringComparison.Ordinal)
            ? $"{spec.SheetName.Replace("-portrait", "", StringComparison.Ordinal)} - portrait.slddrt"
            : $"{spec.SheetName} - landscape.slddrt";
        return folders.Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
    }
}
