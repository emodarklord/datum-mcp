using System.Globalization;
using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    private const int ImportFromEntireModel = 0;
    private const double AxisTolerance = 1e-3;
    private const double MarkPositionTolerance = 1e-5;

    public IReadOnlyList<ViewInfo> ImportDimensions(string id, string? view) => Call(() =>
    {
        var document = Find(id);
        var drawing = DrawingOf(document, id);
        document.ClearSelection2(true);
        var target = view is null ? null : ViewOf(id, document, view);
        if (target is not null)
        {
            document.Extension.SelectByID2(target.Name, "DRAWINGVIEW", 0, 0, 0, false, 0, null, 0);
        }

        drawing.InsertModelAnnotations3(ImportFromEntireModel, (int)swInsertAnnotation_e.swInsertDimensionsMarkedForDrawing, target is null, true, false, false);
        document.ClearSelection2(true);
        document.EditRebuild3();
        return (target is null ? ViewsOf(document) : [target]).Select(v => DescribeView(id, document, v)).ToList();
    });

    public IReadOnlyList<SheetDimension> ListSheetDimensions(string id, string? view) => Call(() =>
    {
        var document = Find(id);
        DrawingOf(document, id);
        var views = view is null ? ViewsOf(document) : [ViewOf(id, document, view)];
        return views.SelectMany(v => (v.GetDisplayDimensions() as object[] ?? []).OfType<IDisplayDimension>().Select(shown => DescribeSheetDimension(id, document, v, shown))).ToList();
    });

    public SheetDimension AddSheetDimension(string id, string view, string first, string? second, double x, double y) => Call(() =>
    {
        var document = Find(id);
        var drawing = DrawingOf(document, id);
        var target = ViewOf(id, document, view);
        drawing.ActivateView(target.Name);
        document.ClearSelection2(true);
        foreach (var (handle, append) in second is null ? new[] { (first, false) } : [(first, false), (second, true)])
        {
            var edge = Resolve(id, document, handle) as IEdge
                ?? throw new DatumException(ErrorCode.InvalidArgument, $"{handle} is not an edge", "Use an edge# handle from view_entities");
            if (!target.SelectEntity(edge, append))
            {
                throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't select {handle} in {view}");
            }
        }

        var added = WithoutDimensionPromptResult(() => document.AddDimension2(x / Mm, y / Mm, 0) as IDisplayDimension);
        document.ClearSelection2(true);
        return added is null
            ? throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't dimension {first}{(second is null ? "" : $" and {second}")}")
            : DescribeSheetDimension(id, document, target, added);
    });

    public ToleranceChange SetTolerance(string id, string dimension, string kind, double upper, double lower) => Call(() =>
    {
        var document = Find(id);
        DrawingOf(document, id);
        var (view, found) = ViewsOf(document)
            .SelectMany(v => (v.GetDisplayDimensions() as object[] ?? []).OfType<IDisplayDimension>().Select(shown => (View: v, Dimension: shown.GetDimension2(0))))
            .FirstOrDefault(pair => string.Equals(ShortName(pair.Dimension), dimension, StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            throw new DatumException(ErrorCode.NotFound, $"no dimension '{dimension}' on this drawing", "Call dimension_list");
        }

        var code = Tolerances.Code(kind);
        var low = code == (int)swTolType_e.swTolSYMMETRIC ? -Math.Abs(upper) : lower;
        var tolerance = found.Tolerance;
        tolerance.Type = code;
        // From a drawing "this configuration" means nothing, so SetValues2 refuses it; the plain call applies everywhere.
        if (code != (int)swTolType_e.swTolNONE
            && !tolerance.SetValues2(low / Mm, upper / Mm, (int)swSetValueInConfiguration_e.swSetValue_InAllConfigurations, null)
            && !tolerance.SetValues(low / Mm, upper / Mm))
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS refused that tolerance on {dimension}");
        }

        document.EditRebuild3();

        // The tolerance lives on the model, but changing it from a drawing doesn't mark the model as changed, so
        // without this a save of the model would skip it.
        (view.ReferencedDocument as IModelDoc2)?.SetSaveFlag();
        var read = found.Tolerance;
        read.GetMinValue2(out var readLow);
        read.GetMaxValue2(out var readHigh);
        if (read.Type != code || (code != (int)swTolType_e.swTolNONE && Math.Abs(readHigh - (upper / Mm)) > 1e-9))
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS didn't keep the tolerance on {dimension}");
        }

        return new ToleranceChange(ShortName(found), kind.Trim().ToLowerInvariant(), readHigh * Mm, readLow * Mm, view.GetReferencedModelName());
    });

    public MarkCount AddMarks(string id, string view) => Call(() =>
    {
        var document = Find(id);
        var drawing = DrawingOf(document, id);
        var target = ViewOf(id, document, view);
        drawing.ActivateView(target.Name);
        foreach (var cylinder in CylindersIn(target).Where(c => c.EndOn && !HasCentreMark(target, c)))
        {
            document.ClearSelection2(true);
            if (cylinder.Edge is not null && target.SelectEntity(cylinder.Edge, false))
            {
                drawing.InsertCenterMark3((int)swCenterMarkStyle_e.swCenterMark_Single, false, false);
            }
        }

        var lines = target.GetCenterLineCount();
        foreach (var cylinder in CylindersIn(target).Where(c => !c.EndOn).Skip(lines))
        {
            document.ClearSelection2(true);
            if (target.SelectEntity(cylinder.Face, false))
            {
                drawing.InsertCenterLine2();
            }
        }

        document.ClearSelection2(true);
        document.EditRebuild3();
        return CountMarks(id, document, target);
    });

    public string AddNote(string id, string text, double x, double y, string? view) => Call(() =>
    {
        var document = Find(id);
        var drawing = DrawingOf(document, id);
        if (view is not null)
        {
            drawing.ActivateView(ViewOf(id, document, view).Name);
        }
        else
        {
            drawing.ActivateSheet(((ISheet)drawing.GetCurrentSheet()).GetName());
        }

        var note = document.InsertNote(text) as INote
            ?? throw new DatumException(ErrorCode.SolidworksError, "SOLIDWORKS couldn't add the note");
        ((IAnnotation)note.GetAnnotation()).SetPosition2(x / Mm, y / Mm, 0);
        document.ClearSelection2(true);
        return note.GetText();
    });

    public DrawingFacts ReadDrawingFacts(string id) => Call(() =>
    {
        var document = Find(id);
        var drawing = DrawingOf(document, id);
        var views = ViewsOf(document);
        var properties = (double[])((ISheet)drawing.GetCurrentSheet()).GetProperties2();
        var models = views.Select(view => view.GetReferencedModelName()).Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var parts = views.Select(view => view.ReferencedDocument).OfType<IPartDoc>().Cast<IModelDoc2>().GroupBy(model => model.GetPathName()).Select(group => group.First()).ToList();
        var assembly = views.Select(view => view.ReferencedDocument).OfType<IAssemblyDoc>().FirstOrDefault();
        return new DrawingFacts(
            properties[SheetIndexWidth] * Mm,
            properties[SheetIndexHeight] * Mm,
            TitleAreaOf(drawing),
            [.. views.Select(view => FactsOf(id, document, view))],
            [.. parts.SelectMany(ModelDimensionsOf)],
            [.. views.SelectMany(v => (v.GetDisplayDimensions() as object[] ?? []).OfType<IDisplayDimension>().Select(shown => DescribeSheetDimension(id, document, v, shown)))],
            [.. models.Where(path => !File.Exists(path))],
            document.Extension.NeedsRebuild2 != (int)swModelRebuildStatus_e.swModelRebuildStatus_FullyRebuilt)
        {
            BomRows = BomRowsOf(document),
            BomBox = BomBoxOf(document),
            ActualBom = assembly is null ? null : BomOf(assembly),
            Balloons = assembly is null ? null : BalloonsOf(document),
        };
    });

    private ViewFacts FactsOf(string id, IModelDoc2 document, IView view)
    {
        var outline = (double[])view.GetOutline();
        var marks = CountMarks(id, document, view);
        return new ViewFacts(
            DescribeView(id, document, view),
            new SheetBox(outline[0] * Mm, outline[1] * Mm, outline[2] * Mm, outline[3] * Mm),
            (view.GetOrientationName() ?? "").Contains("Isometric", StringComparison.OrdinalIgnoreCase),
            CylindersIn(view).Count(c => !c.EndOn),
            marks.MissingMarks,
            marks.MissingLines,
            [.. (view.GetAnnotations() as object[] ?? []).OfType<IAnnotation>().Select(DescribeAnnotation)]);
    }

    private static SheetAnnotation DescribeAnnotation(IAnnotation annotation)
    {
        var at = annotation.GetPosition() as double[] ?? [0, 0];
        var text = annotation.GetSpecificAnnotation() switch
        {
            IDisplayDimension shown => ShortName(shown.GetDimension2(0)),
            INote note => note.GetText(),
            _ => annotation.GetName(),
        };
        return new SheetAnnotation(text, at[0] * Mm, at[1] * Mm, annotation.IsDangling());
    }

    // The sheet format's notes sit in the title block, so their extent marks it out. Those in the top half are zone
    // labels, not the block.
    private static SheetBox? TitleAreaOf(IDrawingDoc drawing)
    {
        var sheetView = drawing.GetFirstView() as IView;
        var height = ((double[])((ISheet)drawing.GetCurrentSheet()).GetProperties2())[SheetIndexHeight];
        var points = (sheetView?.GetNotes() as object[] ?? []).OfType<INote>()
            .Where(note => note.GetText().Length > 1)
            .Select(note => note.GetTextPoint2() as double[])
            .OfType<double[]>()
            .Where(at => at[1] < height / 2)
            .ToList();
        return points.Count == 0
            ? null
            : new SheetBox(points.Min(p => p[0]) * Mm, points.Min(p => p[1]) * Mm, points.Max(p => p[0]) * Mm, points.Max(p => p[1]) * Mm);
    }

    private static IEnumerable<ModelDimension> ModelDimensionsOf(IModelDoc2 model)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var owner in AllFeatures(model).Where(feature => !IsSuppressed(feature)))
        {
            for (var display = owner.GetFirstDisplayDimension(); display is not null; display = owner.GetNextDisplayDimension(display))
            {
                var dimension = ((IDisplayDimension)display).GetDimension2(0);
                if (dimension is not null && dimension.DrivenState == (int)swDimensionDrivenState_e.swDimensionDriving && seen.Add(ShortName(dimension)))
                {
                    var angular = dimension.GetType() == (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular;
                    yield return new ModelDimension(ShortName(dimension), FromSystem(dimension.SystemValue, angular), angular ? "°" : " mm");
                }
            }
        }
    }

    private SheetDimension DescribeSheetDimension(string id, IModelDoc2 document, IView view, IDisplayDimension shown)
    {
        var dimension = shown.GetDimension2(0);
        var angular = dimension.GetType() == (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular;
        var annotation = (IAnnotation)shown.GetAnnotation();
        return new SheetDimension(
            HandleOf(id, document, "view", view),
            ShortName(dimension),
            FromSystem(dimension.SystemValue, angular),
            angular ? "°" : " mm",
            annotation.IsDangling(),
            ToleranceText(dimension));
    }

    private static string? ToleranceText(IDimension dimension)
    {
        var tolerance = dimension.Tolerance;
        if (tolerance is null || tolerance.Type == (int)swTolType_e.swTolNONE)
        {
            return null;
        }

        tolerance.GetMinValue2(out var low);
        tolerance.GetMaxValue2(out var high);
        var (upper, lower) = (high * Mm, low * Mm);
        return tolerance.Type == (int)swTolType_e.swTolSYMMETRIC
            ? string.Create(CultureInfo.InvariantCulture, $"±{Numbers.Format(upper)}")
            : string.Create(CultureInfo.InvariantCulture, $"+{Numbers.Format(upper)} {Numbers.Format(lower)}");
    }

    private T? WithoutDimensionPromptResult<T>(Func<T?> work)
        where T : class
    {
        T? result = null;
        WithoutDimensionPrompt(() => result = work());
        return result;
    }

    private MarkCount CountMarks(string id, IModelDoc2 document, IView view)
    {
        var cylinders = CylindersIn(view);
        var missingMarks = cylinders.Count(c => c.EndOn && !HasCentreMark(view, c));
        var lines = view.GetCenterLineCount();
        return new MarkCount(HandleOf(id, document, "view", view), CentreMarksOf(view).Count, lines, missingMarks, Math.Max(0, cylinders.Count(c => !c.EndOn) - lines));
    }

    // The full cylinders of a part's view, one per axis. Rounds are left out: their edges cover less than a circle.
    // Assembly views return none, because which holes need axes there depends on the design.
    private static List<ViewCylinder> CylindersIn(IView view)
    {
        if (view.ReferencedDocument is not IPartDoc part)
        {
            return [];
        }

        var transform = (double[])view.ModelToViewTransform.ArrayData;
        var towardViewer = new Point3(transform[2], transform[5], transform[8]);
        var found = new List<ViewCylinder>();
        foreach (var face in SolidBodies(part).SelectMany(b => (b.GetFaces() as object[] ?? []).OfType<IFace2>()))
        {
            var surface = (ISurface)face.GetSurface();
            if (!surface.IsCylinder() || surface.CylinderParams is not double[] { Length: >= 7 } c)
            {
                continue;
            }

            var axis = new Point3(c[3], c[4], c[5]).Normalized();
            var origin = new Point3(c[0], c[1], c[2]);
            var across = Math.Abs(axis.Dot(towardViewer));
            if (across > AxisTolerance && across < 1 - AxisTolerance)
            {
                continue;
            }

            var edges = (face.GetEdges() as object[] ?? []).OfType<IEdge>().Where(e => ((ICurve)e.GetCurve()).IsCircle()).ToList();
            var around = edges.Sum(e => { var p = e.GetCurveParams3(); return ((ICurve)e.GetCurve()).GetLength3(p.UMinValue, p.UMaxValue); });
            if (around < 2 * Math.PI * c[6] * 0.99 || found.Any(known => SameAxis(known, origin, axis)))
            {
                continue;
            }

            found.Add(new ViewCylinder(face, edges.FirstOrDefault(), origin, axis, across >= 1 - AxisTolerance));
        }

        return found;
    }

    private static bool SameAxis(ViewCylinder known, Point3 origin, Point3 axis)
    {
        var between = origin.Minus(known.Point);
        var along = between.Dot(known.Axis);
        var apart = between.Minus(new Point3(known.Axis.X * along, known.Axis.Y * along, known.Axis.Z * along)).Length;
        return apart < MarkPositionTolerance && Math.Abs(axis.Dot(known.Axis)) > 1 - AxisTolerance;
    }

    private static bool HasCentreMark(IView view, ViewCylinder cylinder) =>
        CentreMarksOf(view).Any(mark => mark.GetPosition(0) is double[] { Length: >= 3 } at && SameAxis(cylinder, new Point3(at[0], at[1], at[2]), cylinder.Axis));

    private sealed record ViewCylinder(IFace2 Face, IEdge? Edge, Point3 Point, Point3 Axis, bool EndOn);
}
