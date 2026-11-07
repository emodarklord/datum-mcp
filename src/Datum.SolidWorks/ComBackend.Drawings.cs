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
    private const double CuttingLineOverrun = 0.006;

    // GetOutline adds this much on every side, in sheet metres, whatever the scale.
    private const double ViewMargin = 0.00432;
    private const double EdgeOnTolerance = 0.01;

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
        sheet = (ISheet)drawing.GetCurrentSheet();
        if (format is not null)
        {
            // SetupSheet5 records the format without loading it.
            sheet.SetTemplateName(format);
            sheet.ReloadTemplate(true);
            sheet.SheetFormatVisible = true;
        }

        SetUnits(document, drawing, spec.Units);
        document.EditRebuild3();

        var properties = (double[])sheet.GetProperties2();
        var loaded = format is not null && string.Equals(sheet.GetTemplateName(), format, StringComparison.OrdinalIgnoreCase) ? format : null;
        return new DrawingCreated(DescribeActive(document), loaded, properties[SheetIndexWidth] * Mm, properties[SheetIndexHeight] * Mm, properties[SheetIndexFirstAngle] != 0);
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
        var sheet = (ISheet)drawing.GetCurrentSheet();
        var properties = (double[])sheet.GetProperties2();
        var view = WithLoaded(model, ModelType(model), () => drawing.CreateDrawViewFromModelView3(model, named, x / Mm, y / Mm, 0))
            ?? throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't add a {orientation} view of '{model}'");

        // As in AddStandardViews: the first view resets the sheet's scale. A view also comes in with its own scale
        // unless told to follow the sheet's.
        sheet.SetScale(properties[2], properties[3], false, false);
        view.UseSheetScale = 1;
        document.EditRebuild3();

        // A view's outline stays at its old size until it has been drawn.
        document.GraphicsRedraw2();
        return DescribeView(id, document, view);
    });

    public string SetSheetScale(string id, string scale) => Call(() =>
    {
        var document = Find(id);
        var drawing = DrawingOf(document, id);
        var (numerator, denominator) = DrawingScale.Parse(scale);
        var sheet = (ISheet)drawing.GetCurrentSheet();
        sheet.SetScale(numerator, denominator, false, false);
        document.EditRebuild3();
        return SheetScaleOf(sheet);
    });

    public void SetSheetUnits(string id, string units) => Call(() =>
    {
        var document = Find(id);
        SetUnits(document, DrawingOf(document, id), units);
        document.EditRebuild3();
        return true;
    });

    // The stock drawing template works in inches whatever the model uses, and the stock formats say so in a note.
    private static void SetUnits(IModelDoc2 document, IDrawingDoc drawing, string units)
    {
        var inches = units.Trim().Equals("inches", StringComparison.OrdinalIgnoreCase);
        document.Extension.SetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitSystem, 0, (int)(inches ? swUnitSystem_e.swUnitSystem_IPS : swUnitSystem_e.swUnitSystem_MMGS));
        var (from, to) = inches ? (FormatMm, FormatInches) : (FormatInches, FormatMm);
        var notes = ((drawing.GetFirstView() as IView)?.GetNotes() as object[] ?? []).OfType<INote>().Where(note => note.PropertyLinkedText.Contains(from, StringComparison.OrdinalIgnoreCase)).ToList();
        if (notes.Count == 0)
        {
            return;
        }

        drawing.EditTemplate();
        foreach (var note in notes)
        {
            note.SetText(note.PropertyLinkedText.Replace(from, to, StringComparison.OrdinalIgnoreCase));
        }

        drawing.EditSheet();
    }

    public IReadOnlyList<string> DeleteView(string id, string view) => Call(() =>
    {
        var document = Find(id);
        var target = ViewOf(id, document, view);
        var before = ViewsOf(document).Select(v => v.Name).ToList();
        document.ClearSelection2(true);
        if (!document.Extension.SelectByID2(target.Name, "DRAWINGVIEW", 0, 0, 0, false, 0, null, 0) || !document.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed))
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't delete {view}");
        }

        document.ClearSelection2(true);
        document.EditRebuild3();
        var left = ViewsOf(document).Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        return before.Where(name => !left.Contains(name)).ToList();
    });

    public IReadOnlyList<ViewInfo> ListViews(string id) => Call(() =>
    {
        var document = Find(id);
        DrawingOf(document, id);
        return ViewsOf(document).Select(view => DescribeView(id, document, view)).ToList();
    });

    public ViewInfo SetView(string id, string view, double? x, double? y, string? scale, string? display, string? configuration, bool? exploded) => Call(() =>
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

        if (configuration is not null)
        {
            target.ReferencedConfiguration = configuration;
            if (!string.Equals(target.ReferencedConfiguration, configuration, StringComparison.OrdinalIgnoreCase))
            {
                throw new DatumException(ErrorCode.NotFound, $"{view} couldn't show configuration {configuration}", "Check the name in the model with config_list");
            }
        }

        if (exploded is { } show && (target.ShowExploded(show) != true || target.IsExploded() != show))
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS didn't {(show ? "explode" : "collapse")} {view}", "The view's configuration needs an exploded view");
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

    public IReadOnlyList<ViewEntity> ListViewEntities(string id, string view) => Call(() =>
    {
        var document = Find(id);
        var target = ViewOf(id, document, view);
        var transform = (double[])target.ModelToViewTransform.ArrayData;
        var towardViewer = new Point3(transform[2], transform[5], transform[8]);
        return (target.GetVisibleEntities2(null, (int)swViewEntityType_e.swViewEntityType_Edge) as object[] ?? []).OfType<IEdge>()
            .Select(edge =>
            {
                var curve = (ICurve)edge.GetCurve();

                // Evaluating the curve at the edge's parameters lands elsewhere on it: a line's parameter doesn't start at its start.
                var (start, end) = EndsOf(edge);
                var handle = _handles.HandleForLive(id, "edge", edge);
                if (!curve.IsCircle() || curve.CircleParams is not double[] { Length: >= 7 } circle)
                {
                    var (fromX, fromY) = OnSheet(target, start);
                    var (toX, toY) = OnSheet(target, end);
                    return new ViewEntity(handle, curve.IsLine() ? "line" : "curve", fromX, fromY, toX, toY);
                }

                var centre = new Point3(circle[0], circle[1], circle[2]);
                var axis = new Point3(circle[3], circle[4], circle[5]).Normalized();
                var facing = Math.Abs(axis.Dot(towardViewer));
                if (facing < EdgeOnTolerance)
                {
                    // Seen edge-on a circle is drawn as a straight line across its diameter.
                    var across = new Point3((axis.Y * towardViewer.Z) - (axis.Z * towardViewer.Y), (axis.Z * towardViewer.X) - (axis.X * towardViewer.Z), (axis.X * towardViewer.Y) - (axis.Y * towardViewer.X)).Normalized();
                    var reach = new Point3(across.X * circle[6], across.Y * circle[6], across.Z * circle[6]);
                    var (lowX, lowY) = OnSheet(target, centre.Minus(reach));
                    var (highX, highY) = OnSheet(target, new Point3(centre.X + reach.X, centre.Y + reach.Y, centre.Z + reach.Z));
                    return new ViewEntity(handle, "line", lowX, lowY, highX, highY);
                }

                var whole = start.Minus(end).Length < EdgeOnTolerance * circle[6];
                var (x1, y1) = OnSheet(target, centre);
                var (x2, y2) = OnSheet(target, end);
                return new ViewEntity(handle, facing < 1 - EdgeOnTolerance ? "ellipse" : whole ? "circle" : "arc", x1, y1, x2, y2);
            })
            .ToList();
    });

    public ViewInfo AddSectionView(string id, string parent, bool vertical, double offset, double x, double y, string? label, bool flip) => Call(() =>
    {
        var document = Find(id);
        var drawing = DrawingOf(document, id);
        var view = ViewOf(id, document, parent);
        var existing = ViewsOf(document).Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        var outline = (double[])view.GetOutline();
        var scale = view.ScaleDecimal;
        var reach = (((vertical ? outline[3] - outline[1] : outline[2] - outline[0]) / 2) + CuttingLineOverrun) / scale;
        var at = offset / Mm;
        drawing.ActivateView(view.Name);
        var line = vertical
            ? document.SketchManager.CreateLine(at, -reach, 0, at, reach, 0)
            : document.SketchManager.CreateLine(-reach, at, 0, reach, at, 0);
        if (line is null)
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't draw a cutting line in {parent}");
        }

        var options = flip ? (int)swCreateSectionViewAtOptions_e.swCreateSectionView_ChangeDirection : 0;
        var section = drawing.CreateSectionViewAt5(x / Mm, y / Mm, 0, label ?? "", options, null, 0);
        document.ClearSelection2(true);
        document.EditRebuild3();
        return NewView(id, document, existing, section, $"the section of {parent}");
    });

    public ViewInfo AddDetailView(string id, string parent, double atX, double atY, double radius, string scale, double x, double y, string? label) => Call(() =>
    {
        var document = Find(id);
        var drawing = DrawingOf(document, id);
        var view = ViewOf(id, document, parent);
        var existing = ViewsOf(document).Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        var (numerator, denominator) = DrawingScale.Parse(scale);
        drawing.ActivateView(view.Name);
        if (document.SketchManager.CreateCircleByRadius(atX / Mm, atY / Mm, 0, radius / Mm) is null)
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't draw a detail circle in {parent}");
        }

        var detail = drawing.CreateDetailViewAt4(
            x / Mm, y / Mm, 0, (int)swDetViewStyle_e.swDetViewSTANDARD, numerator, denominator, label ?? "", (int)swDetCircleShowType_e.swDetCirclePROFILE, true, false, false, 0) as IView;
        document.ClearSelection2(true);
        document.EditRebuild3();
        return NewView(id, document, existing, detail, $"the detail of {parent}");
    });

    public ViewInfo AddAuxiliaryView(string id, string parent, string edge, double x, double y, string? label) => Call(() =>
    {
        var document = Find(id);
        var drawing = DrawingOf(document, id);
        var view = ViewOf(id, document, parent);
        var existing = ViewsOf(document).Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        var target = Resolve(id, document, edge) as IEdge
            ?? throw new DatumException(ErrorCode.InvalidArgument, $"{edge} is not an edge", "Use an edge# handle from view_entities");
        document.ClearSelection2(true);
        if (!view.SelectEntity(target, false))
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't select {edge} in {parent}");
        }

        var auxiliary = drawing.CreateAuxiliaryViewAt2(x / Mm, y / Mm, 0, true, label ?? "", true, false) as IView;
        document.ClearSelection2(true);
        document.EditRebuild3();
        return NewView(id, document, existing, auxiliary, $"the auxiliary view of {parent}");
    });

    private static (Point3 Start, Point3 End) EndsOf(IEdge edge)
    {
        var parameters = edge.GetCurveParams3();
        var start = (double[])parameters.StartPoint;
        var end = (double[])parameters.EndPoint;
        return (new Point3(start[0], start[1], start[2]), new Point3(end[0], end[1], end[2]));
    }

    private static string SheetScaleOf(ISheet sheet)
    {
        var properties = (double[])sheet.GetProperties2();
        return string.Create(CultureInfo.InvariantCulture, $"{properties[2]:0.###}:{properties[3]:0.###}");
    }

    private ViewInfo NewView(string id, IModelDoc2 document, HashSet<string> existing, IView? made, string what)
    {
        document.GraphicsRedraw2();
        var view = made ?? ViewsOf(document).FirstOrDefault(v => !existing.Contains(v.Name))
            ?? throw new DatumException(ErrorCode.FeatureFailed, $"SOLIDWORKS couldn't make {what}", "Check the view shows geometry at that position");
        return DescribeView(id, document, view);
    }

    // Model point in metres to sheet mm. The transform holds each model axis's image as a row, then the view's
    // origin on the sheet, then its scale.
    private static (double X, double Y) OnSheet(IView view, Point3 model)
    {
        var t = (double[])view.ModelToViewTransform.ArrayData;
        return (
            (((model.X * t[0]) + (model.Y * t[3]) + (model.Z * t[6])) * t[12]) + t[9],
            (((model.X * t[1]) + (model.Y * t[4]) + (model.Z * t[7])) * t[12]) + t[10]) is var (x, y)
            ? (x * Mm, y * Mm)
            : default;
    }

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
        var outline = OutlineOf(view);
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
            outline.Right - outline.Left,
            outline.Top - outline.Bottom,
            ViewDisplays.Name(view.GetDisplayMode2()),
            view.GetDisplayDimensionCount(),
            CentreMarksOf(view).Count,
            view.GetCenterLineCount())
        {
            Exploded = view.IsExploded() ? view.ReferencedConfiguration : null,
        };
    }

    private static SheetBox OutlineOf(IView view)
    {
        var outline = (double[])view.GetOutline();
        return new SheetBox((outline[0] + ViewMargin) * Mm, (outline[1] + ViewMargin) * Mm, (outline[2] - ViewMargin) * Mm, (outline[3] - ViewMargin) * Mm);
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
