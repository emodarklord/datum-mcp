using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    // Offset of dimension text from what it dimensions, in mm; only affects where the number is drawn.
    private const double DimensionTextOffset = 5;

    private static readonly Dictionary<string, string> RelationCodes = new()
    {
        ["horizontal"] = "sgHORIZONTAL2D",
        ["vertical"] = "sgVERTICAL2D",
        ["coincident"] = "sgCOINCIDENT",
        ["parallel"] = "sgPARALLEL",
        ["perpendicular"] = "sgPERPENDICULAR",
        ["tangent"] = "sgTANGENT",
        ["equal"] = "sgSAMELENGTH",
        ["concentric"] = "sgCONCENTRIC",
        ["collinear"] = "sgCOLINEAR",
        ["midpoint"] = "sgATMIDDLE",
        ["fixed"] = "sgFIXED",
    };

    private readonly OptionsJournal _journal = new(OptionsJournal.DefaultPath);
    private readonly HashSet<string> _reopened = [];

    public SketchInfo BeginSketch(string id, string target) => Call(() =>
    {
        var document = Find(id);
        if (document.SketchManager.ActiveSketch is ISketch open)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"{((IFeature)open).Name} is still being edited", "Call sketch_end first");
        }

        document.ClearSelection2(true);
        if (!SelectSketchTarget(id, document, target))
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't select {target}");
        }

        document.SketchManager.InsertSketch(true);
        var sketch = document.SketchManager.ActiveSketch
            ?? throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS didn't start a sketch on {target}");
        return DescribeSketch(sketch);
    });

    public SketchInfo ReopenSketch(string id, string sketch) => Call(() =>
    {
        var document = Find(id);
        if (document.SketchManager.ActiveSketch is ISketch open)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"{((IFeature)open).Name} is still being edited", "Call sketch_end first");
        }

        var feature = FeatureOf(id, document, sketch);
        if (feature.GetSpecificFeature2() is not ISketch)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"{sketch} is not a sketch", "Give a sketch's feat# from model_tree");
        }

        document.ClearSelection2(true);
        feature.Select2(false, 0);
        document.EditSketch();
        var active = document.SketchManager.ActiveSketch
            ?? throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS didn't open {sketch} for editing");
        _reopened.Add(id);
        return DescribeSketch(active) with { Handle = sketch };
    });

    public IReadOnlyList<SketchSegmentInfo> AddSketchEntities(string id, IReadOnlyList<SketchEntitySpec> entities) => Call(() =>
    {
        var document = Find(id);
        var sketchManager = document.SketchManager;
        var sketch = ActiveSketchOf(document);
        var created = new List<(SketchEntitySpec Spec, List<object> Items)>();

        // AddToDB places entities at exactly the given coordinates, with no snapping or inferred relations.
        var addToDb = sketchManager.AddToDB;
        sketchManager.AddToDB = true;
        sketchManager.DisplayWhenAdded = false;
        try
        {
            foreach (var spec in entities)
            {
                var before = SegmentsOf(sketch);
                var point = Create(sketchManager, spec);
                var items = point is not null
                    ? [point]
                    : SegmentsOf(sketch).Where(s => !before.Any(b => ReferenceEquals(b, s))).Cast<object>().ToList();
                if (spec.Type == "polyline")
                {
                    MergeChain(document, items, spec.Closed);
                }

                if (spec.Construction)
                {
                    foreach (var segment in items.OfType<ISketchSegment>())
                    {
                        segment.ConstructionGeometry = true;
                    }
                }

                created.Add((spec, items));
            }
        }
        finally
        {
            sketchManager.AddToDB = addToDb;
            sketchManager.DisplayWhenAdded = true;
        }

        if (created.Any(c => c.Spec.Dimension != false))
        {
            var axes = FrameOf(sketch);
            var positioned = new Dictionary<(long, long), ISketchPoint>();
            WithoutDimensionPrompt(() =>
            {
                foreach (var (spec, items) in created.Where(c => c.Spec.Dimension != false))
                {
                    Dimension(document, axes, spec, items, positioned);
                }
            });
        }

        document.ClearSelection2(true);
        document.GraphicsRedraw2();
        return created.SelectMany(c => c.Items).Select(item => DescribeSketchItem(id, document, item)).ToList();
    });

    public SketchInfo ConstrainSketch(string id, IReadOnlyList<SketchConstraintSpec> constraints) => Call(() =>
    {
        var document = Find(id);
        var sketch = ActiveSketchOf(document);
        var axes = FrameOf(sketch);
        WithoutDimensionPrompt(() =>
        {
            foreach (var constraint in constraints)
            {
                var targets = constraint.Entities.Select(handle => Resolve(id, document, handle)).ToList();
                SelectSketchItems(document, targets);
                if (RelationCodes.TryGetValue(constraint.Kind, out var code))
                {
                    document.SketchAddConstraints(code);
                    continue;
                }

                var at = ToModel(axes, Offset(AnchorOf(targets[0])));
                var dimension = document.AddDimension2(at.X, at.Y, at.Z) as IDisplayDimension
                    ?? throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't add a {constraint.Kind} dimension to {string.Join(", ", constraint.Entities)}");
                var value = constraint.Kind == "angle" ? constraint.Value!.Value * Math.PI / 180 : constraint.Value!.Value / Mm;
                dimension.GetDimension2(0).SetSystemValue3(value, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
            }
        });

        document.ClearSelection2(true);
        return DescribeSketch(sketch);
    });

    public SketchInfo ReadSketch(string id) => Call(() => DescribeSketch(ActiveSketchOf(Find(id))));

    public SketchInfo EndSketch(string id) => Call(() =>
    {
        var document = Find(id);
        var sketch = ActiveSketchOf(document);
        var info = DescribeSketch(sketch);
        var feature = (IFeature)sketch;
        document.SketchManager.InsertSketch(true);
        // SOLIDWORKS leaves the closed sketch selected, which draws a move triad over the model.
        document.ClearSelection2(true);
        return info with { Handle = HandleOf(id, document, "feat", feature), Reopened = _reopened.Remove(id) };
    });

    internal bool InputDimensionPrompt() => Call(() => App.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate));

    internal string SketchStatus(string id, string sketchName) => Call(() =>
        SketchSpecs.DescribeStatus(((ISketch)AllFeatures(Find(id)).First(f => f.Name == sketchName).GetSpecificFeature2()).GetConstrainedStatus()));

    private bool SelectSketchTarget(string id, IModelDoc2 document, string target)
    {
        var defaultPlane = target.Trim().ToUpperInvariant() switch
        {
            "FRONT" => 0,
            "TOP" => 1,
            "RIGHT" => 2,
            _ => -1,
        };
        if (defaultPlane >= 0)
        {
            // By position rather than name, so localized plane names still work.
            var plane = TopLevelFeatures(document).Where(f => f.GetTypeName2() == "RefPlane").ElementAtOrDefault(defaultPlane)
                ?? throw new DatumException(ErrorCode.NotFound, $"no {target} plane in {id}");
            return plane.Select2(false, 0);
        }

        return Resolve(id, document, target) switch
        {
            IFeature feature when feature.GetTypeName2() == "RefPlane" => feature.Select2(false, 0),
            IFace2 face when ((ISurface)face.GetSurface()).IsPlane() => ((IEntity)face).Select4(false, null),
            IFace2 => throw new DatumException(ErrorCode.InvalidArgument, $"{target} is not flat", "Sketch on a planar face or a plane"),
            _ => throw new DatumException(ErrorCode.InvalidArgument, $"{target} is not a plane or a face", "Use Front, Top, Right, a plane feat# or a flat face#"),
        };
    }

    private static ISketchPoint? Create(ISketchManager sketches, SketchEntitySpec e)
    {
        static double M(double? mm) => mm!.Value / Mm;

        object? result = e.Type switch
        {
            "line" => sketches.CreateLine(M(e.X1), M(e.Y1), 0, M(e.X2), M(e.Y2), 0),
            "centerline" => sketches.CreateCenterLine(M(e.X1), M(e.Y1), 0, M(e.X2), M(e.Y2), 0),
            "rect" => sketches.CreateCornerRectangle(M(e.X1), M(e.Y1), 0, M(e.X2), M(e.Y2), 0),
            "center_rect" => sketches.CreateCenterRectangle(M(e.Cx), M(e.Cy), 0, M(e.Cx + (e.Width / 2)), M(e.Cy + (e.Height / 2)), 0),
            "circle" => sketches.CreateCircleByRadius(M(e.Cx), M(e.Cy), 0, M(e.Radius)),
            "arc" => sketches.Create3PointArc(M(e.X1), M(e.Y1), 0, M(e.X2), M(e.Y2), 0, M(e.X3), M(e.Y3), 0),
            "polygon" => sketches.CreatePolygon(M(e.Cx), M(e.Cy), 0, M(e.Cx + e.Radius), M(e.Cy), 0, e.Sides!.Value, true),
            "slot" => sketches.CreateSketchSlot(
                (int)swSketchSlotCreationType_e.swSketchSlotCreationType_line, (int)swSketchSlotLengthType_e.swSketchSlotLengthType_CenterCenter,
                M(e.Width), M(e.X1), M(e.Y1), 0, M(e.X2), M(e.Y2), 0, 0, 0, 0, 1, false),
            "point" => sketches.CreatePoint(M(e.X1), M(e.Y1), 0),
            "polyline" => e.Points!.Zip(e.Points!.Skip(1).Concat(e.Closed ? [e.Points![0]] : []))
                .Select(p => sketches.CreateLine(p.First[0] / Mm, p.First[1] / Mm, 0, p.Second[0] / Mm, p.Second[1] / Mm, 0))
                .ToList() is var lines && lines.All(l => l is not null) ? lines : null,
            _ => null,
        };

        return result is null
            ? throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't create the {e.Type}", "Check the coordinates; overlapping or zero-size shapes are refused")
            : result as ISketchPoint;
    }

    // Lines placed at exact coordinates don't share endpoints, so a chain has to be merged explicitly.
    private static void MergeChain(IModelDoc2 document, List<object> lines, bool closed)
    {
        var chain = lines.OfType<ISketchLine>().ToList();
        for (var i = 0; i < chain.Count - (closed ? 0 : 1); i++)
        {
            document.ClearSelection2(true);
            ((ISketchPoint)chain[i].GetEndPoint2()).Select4(false, null);
            ((ISketchPoint)chain[(i + 1) % chain.Count].GetStartPoint2()).Select4(true, null);
            document.SketchAddConstraints("sgMERGEPOINTS");
        }
    }

    // Fully defines one entity from the sketch origin. AddToDB leaves two points drawn at the same spot
    // unrelated, so a point where another one was already positioned is made coincident with it instead
    // of being dimensioned again, which would over-define the sketch.
    private static void Dimension(IModelDoc2 document, SketchFrame axes, SketchEntitySpec e, List<object> items, Dictionary<(long, long), ISketchPoint> positioned)
    {
        void Place(object point)
        {
            var sketchPoint = (ISketchPoint)point;
            var key = (Key(sketchPoint.X), Key(sketchPoint.Y));
            if (!positioned.TryGetValue(key, out var earlier))
            {
                positioned[key] = sketchPoint;
                Position(document, axes, sketchPoint);
            }
            else if (!ReferenceEquals(earlier, sketchPoint))
            {
                SelectSketchItems(document, [earlier, sketchPoint]);
                document.SketchAddConstraints("sgCOINCIDENT");
            }
        }

        var segments = items.OfType<ISketchSegment>().Where(s => !s.ConstructionGeometry || e.Construction).ToList();
        switch (e.Type)
        {
            case "line" or "centerline" or "polyline":
                foreach (var line in items.OfType<ISketchLine>())
                {
                    Place(line.GetStartPoint2());
                    Place(line.GetEndPoint2());
                }

                break;
            case "arc":
                var arc = (ISketchArc)items[0];
                DimensionEntity(document, axes, arc);
                Place(arc.GetStartPoint2());
                Place(arc.GetEndPoint2());
                break;
            case "slot":
                var ends = items.OfType<ISketchArc>().ToList();
                DimensionEntity(document, axes, ends[0]);
                foreach (var end in ends)
                {
                    Place(end.GetCenterPoint2());
                }

                break;
            case "rect" or "center_rect":
                var lines = segments.OfType<ISketchLine>().ToList();
                DimensionEntity(document, axes, lines.First(l => IsHorizontal(l)));
                DimensionEntity(document, axes, lines.First(l => !IsHorizontal(l)));
                var corner = e.Type == "rect" ? new Point2(e.X1!.Value, e.Y1!.Value) : new Point2(e.Cx!.Value - (e.Width!.Value / 2), e.Cy!.Value - (e.Height!.Value / 2));
                Place(PointAt(lines, corner));
                break;
            case "circle":
                DimensionEntity(document, axes, items[0]);
                Place(((ISketchArc)items[0]).GetCenterPoint2());
                break;
            case "polygon":
                var circle = items.OfType<ISketchArc>().First();
                DimensionEntity(document, axes, circle);
                document.ClearSelection2(true);
                ((ISketchSegment)items.OfType<ISketchLine>().First()).Select4(false, null);
                document.SketchAddConstraints("sgHORIZONTAL2D");
                Place(circle.GetCenterPoint2());
                break;
            case "point":
                Place(items[0]);
                break;
        }
    }

    private static long Key(double metres) => (long)Math.Round(metres * 1e8);

    private static void DimensionEntity(IModelDoc2 document, SketchFrame axes, object entity)
    {
        SelectSketchItems(document, [entity]);
        var at = ToModel(axes, Offset(AnchorOf(entity)));
        document.AddDimension2(at.X, at.Y, at.Z);
    }

    // Ties a point to the sketch origin: a relation where a distance would be zero, a dimension otherwise.
    private static void Position(IModelDoc2 document, SketchFrame axes, ISketchPoint point)
    {
        var at = new Point2(point.X * Mm, point.Y * Mm);
        if (!SelectWithOrigin(document, point))
        {
            return;
        }

        if (Math.Abs(at.X) < 1e-6 && Math.Abs(at.Y) < 1e-6)
        {
            document.SketchAddConstraints("sgCOINCIDENT");
            return;
        }

        if (Math.Abs(at.X) < 1e-6)
        {
            document.SketchAddConstraints("sgVERTICALPOINTS2D");
        }
        else
        {
            var text = ToModel(axes, new Point2(at.X / 2, at.Y - DimensionTextOffset));
            document.AddHorizontalDimension2(text.X, text.Y, text.Z);
        }

        if (!SelectWithOrigin(document, point))
        {
            return;
        }

        if (Math.Abs(at.Y) < 1e-6)
        {
            document.SketchAddConstraints("sgHORIZONTALPOINTS2D");
        }
        else
        {
            var text = ToModel(axes, new Point2(at.X - DimensionTextOffset, at.Y / 2));
            document.AddVerticalDimension2(text.X, text.Y, text.Z);
        }
    }

    private static bool SelectWithOrigin(IModelDoc2 document, ISketchPoint point)
    {
        document.ClearSelection2(true);
        point.Select4(false, null);
        return document.Extension.SelectByID2("Point1@Origin", "EXTSKETCHPOINT", 0, 0, 0, true, 0, null, 0);
    }

    private static void SelectSketchItems(IModelDoc2 document, List<object> items)
    {
        document.ClearSelection2(true);
        for (var i = 0; i < items.Count; i++)
        {
            var selected = items[i] switch
            {
                ISketchSegment segment => segment.Select4(i > 0, null),
                ISketchPoint point => point.Select4(i > 0, null),
                _ => throw new DatumException(ErrorCode.InvalidArgument, "only sketch segments and points can be constrained"),
            };
            if (!selected)
            {
                throw new DatumException(ErrorCode.SolidworksError, "SOLIDWORKS couldn't select a sketch entity");
            }
        }
    }

    private void WithoutDimensionPrompt(Action work)
    {
        // With "Input dimension value" on, every new dimension opens a modal box and blocks the call.
        const swUserPreferenceToggle_e option = swUserPreferenceToggle_e.swInputDimValOnCreate;
        var original = App.GetUserPreferenceToggle((int)option);
        if (!original)
        {
            work();
            return;
        }

        _journal.Record(option.ToString(), 1);
        App.SetUserPreferenceToggle((int)option, false);
        try
        {
            work();
        }
        finally
        {
            App.SetUserPreferenceToggle((int)option, true);
            _journal.Clear(option.ToString());
        }
    }

    private T WithOption<T>(swUserPreferenceIntegerValue_e option, int value, Func<T> work)
    {
        var original = App.GetUserPreferenceIntegerValue((int)option);
        if (original == value)
        {
            return work();
        }

        _journal.Record(option.ToString(), original);
        App.SetUserPreferenceIntegerValue((int)option, value);
        try
        {
            return work();
        }
        finally
        {
            App.SetUserPreferenceIntegerValue((int)option, original);
            _journal.Clear(option.ToString());
        }
    }

    private void RestoreJournaledOptions()
    {
        foreach (var (name, value) in _journal.Pending())
        {
            if (Enum.TryParse<swUserPreferenceToggle_e>(name, out var toggle))
            {
                App.SetUserPreferenceToggle((int)toggle, value != 0);
            }
            else if (Enum.TryParse<swUserPreferenceIntegerValue_e>(name, out var option))
            {
                App.SetUserPreferenceIntegerValue((int)option, value);
            }

            _journal.Clear(name);
        }
    }

    private static SketchInfo DescribeSketch(ISketch sketch)
    {
        var axes = FrameOf(sketch);
        return new SketchInfo(
            null,
            ((IFeature)sketch).Name,
            SketchSpecs.DescribeStatus(sketch.GetConstrainedStatus()),
            SegmentsOf(sketch).Count,
            axes.Origin,
            axes.X,
            axes.Y);
    }

    private SketchSegmentInfo DescribeSketchItem(string id, IModelDoc2 document, object item)
    {
        if (item is ISketchPoint point)
        {
            var at = new Point2(point.X * Mm, point.Y * Mm);
            return new SketchSegmentInfo(HandleOf(id, document, "pt", point), "point", at, at, null, null, false);
        }

        var segment = (ISketchSegment)item;
        var handle = HandleOf(id, document, "seg", segment);
        return segment switch
        {
            ISketchLine line => new SketchSegmentInfo(
                handle, "line", PointOf(line.GetStartPoint2()), PointOf(line.GetEndPoint2()), null, null, segment.ConstructionGeometry),
            ISketchArc arc => new SketchSegmentInfo(
                handle,
                PointOf(arc.GetStartPoint2()) == PointOf(arc.GetEndPoint2()) ? "circle" : "arc",
                PointOf(arc.GetStartPoint2()),
                PointOf(arc.GetEndPoint2()),
                PointOf(arc.GetCenterPoint2()),
                arc.GetRadius() * Mm,
                segment.ConstructionGeometry),
            _ => new SketchSegmentInfo(handle, "curve", default, default, null, null, segment.ConstructionGeometry),
        };
    }

    private static ISketch ActiveSketchOf(IModelDoc2 document) =>
        document.SketchManager.ActiveSketch
            ?? throw new DatumException(ErrorCode.InvalidArgument, "no sketch is being edited", "Call sketch_begin first");

    private static List<ISketchSegment> SegmentsOf(ISketch sketch) => [.. (sketch.GetSketchSegments() as object[] ?? []).OfType<ISketchSegment>()];

    private static IEnumerable<IFeature> TopLevelFeatures(IModelDoc2 document)
    {
        for (var feature = document.FirstFeature() as IFeature; feature is not null; feature = feature.GetNextFeature() as IFeature)
        {
            yield return feature;
        }
    }

    private static Point2 PointOf(object point) => point is ISketchPoint p ? new Point2(Math.Round(p.X * Mm, 6), Math.Round(p.Y * Mm, 6)) : default;

    private static bool IsHorizontal(ISketchLine line) => Math.Abs(PointOf(line.GetStartPoint2()).Y - PointOf(line.GetEndPoint2()).Y) < 1e-6;

    private static ISketchPoint PointAt(IEnumerable<ISketchLine> lines, Point2 at) =>
        lines.SelectMany(l => new[] { (ISketchPoint)l.GetStartPoint2(), (ISketchPoint)l.GetEndPoint2() })
            .OrderBy(p => Math.Abs((p.X * Mm) - at.X) + Math.Abs((p.Y * Mm) - at.Y))
            .First();

    private static Point2 AnchorOf(object entity) => entity switch
    {
        ISketchLine line => Mid(PointOf(line.GetStartPoint2()), PointOf(line.GetEndPoint2())),
        ISketchArc arc => PointOf(arc.GetStartPoint2()),
        ISketchPoint point => new Point2(point.X * Mm, point.Y * Mm),
        _ => default,
    };

    private static Point2 Mid(Point2 a, Point2 b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private static Point2 Offset(Point2 p) => new(p.X + DimensionTextOffset, p.Y + DimensionTextOffset);

    // Maps a sketch-space point in mm to model space in metres, which is what dimension placement takes.
    private static Point3 ToModel(SketchFrame axes, Point2 p) => new(
        (axes.Origin.X + (axes.X.X * p.X) + (axes.Y.X * p.Y)) / Mm,
        (axes.Origin.Y + (axes.X.Y * p.X) + (axes.Y.Y * p.Y)) / Mm,
        (axes.Origin.Z + (axes.X.Z * p.X) + (axes.Y.Z * p.Y)) / Mm);

    private static SketchFrame FrameOf(ISketch sketch)
    {
        var toModel = (MathTransform)((MathTransform)sketch.ModelToSketchTransform).Inverse();
        var a = (double[])toModel.ArrayData;
        return new SketchFrame(new(a[9] * Mm, a[10] * Mm, a[11] * Mm), new(a[0], a[1], a[2]), new(a[3], a[4], a[5]));
    }

    private sealed record SketchFrame(Point3 Origin, Point3 X, Point3 Y);
}
