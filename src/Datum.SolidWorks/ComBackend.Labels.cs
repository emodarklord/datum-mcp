using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    private const int MaxLabels = 30;
    private const double RayRadius = 1e-5;

    private List<ImageLabel> PlaceLabels(string id, IModelDoc2 document, IModelView view, CaptureLabels kind, ImageCrop crop)
    {
        var rotation = (double[])view.Orientation3.ArrayData;
        var faces = VisibleFaces(id, document);
        var triangles = faces.Select(f => Placed(f.Face.GetTessTriangles(true) as float[] ?? [], f.Pose)).ToList();
        var vertices = triangles.SelectMany(Vertices).ToList();
        if (vertices.Count == 0)
        {
            return [];
        }

        var projected = vertices.Select(v => ToView(v, rotation)).ToList();
        var extents = new ViewExtents(projected.Min(p => p.X), projected.Max(p => p.X), projected.Min(p => p.Y), projected.Max(p => p.Y));
        var reach = 2 * (vertices.Max(v => v.Length) + 1);

        // Cylinders are usually holes, the things most worth naming; large areas come next.
        IEnumerable<(string Handle, Point3 At, Func<object, bool> Owns)> candidates = kind == CaptureLabels.Faces
            ? faces.Select((f, i) => (f.Face, Triangles: triangles[i]))
                .Where(f => f.Triangles.Length > 0)
                .OrderByDescending(f => ((ISurface)f.Face.GetSurface()).IsCylinder())
                .ThenByDescending(f => f.Face.GetArea())
                .Select(f => (HandleOf(id, document, "face", f.Face), LargestTriangleCentre(f.Triangles), (Func<object, bool>)(hit => IsSame(hit, f.Face))))
            : VisibleEdges(id, document)
                .Select(e => (e.Edge, e.Pose, Parameters: e.Edge.GetCurveParams3()))
                .OrderByDescending(e => ((ICurve)e.Edge.GetCurve()).GetLength3(e.Parameters.UMinValue, e.Parameters.UMaxValue))
                .Select(e => (HandleOf(id, document, "edge", e.Edge), InAssembly(EdgeMiddle(e.Edge, e.Parameters), e.Pose), (Func<object, bool>)(hit => TouchesEdge(hit, e.Edge))));

        return KeepingSelection(document, () => candidates
            .Where(c => Visible(document, c.At, rotation, reach, c.Owns))
            .Take(MaxLabels)
            .Select(c =>
            {
                var (viewX, viewY) = ToView(c.At, rotation);
                var (x, y) = LabelPlacement.Project(extents, crop, viewX, viewY);
                return new ImageLabel(c.Handle, x, y);
            })
            .ToList());
    }

    private static List<(IFace2 Face, ComponentPose? Pose)> VisibleFaces(string id, IModelDoc2 document) => document is IAssemblyDoc
        ? [.. ComponentsOf(document, id).Where(c => !c.IsSuppressed()).SelectMany(c => BodiesOf(c).SelectMany(b => (b.GetFaces() as object[] ?? []).OfType<IFace2>()).Select(face => (face, (ComponentPose?)PoseOf(c))))]
        : [.. FacesOf(document).Select(face => (face, (ComponentPose?)null))];

    private static List<(IEdge Edge, ComponentPose? Pose)> VisibleEdges(string id, IModelDoc2 document) => document is IAssemblyDoc
        ? [.. ComponentsOf(document, id).Where(c => !c.IsSuppressed()).SelectMany(c => BodiesOf(c).SelectMany(b => (b.GetEdges() as object[] ?? []).OfType<IEdge>()).Select(edge => (edge, (ComponentPose?)PoseOf(c))))]
        : [.. EdgesOf(document).Select(edge => (edge, (ComponentPose?)null))];

    // A component's faces report its own coordinates, not the assembly's.
    private static Point3 InAssembly(Point3 metres, ComponentPose? pose)
    {
        if (pose is not { } placed)
        {
            return metres;
        }

        var mm = placed.Place(new Point3(metres.X * Mm, metres.Y * Mm, metres.Z * Mm));
        return new Point3(mm.X / Mm, mm.Y / Mm, mm.Z / Mm);
    }

    private static float[] Placed(float[] triangles, ComponentPose? pose)
    {
        if (pose is null)
        {
            return triangles;
        }

        return [.. Vertices(triangles).Select(v => InAssembly(v, pose)).SelectMany(v => new[] { (float)v.X, (float)v.Y, (float)v.Z })];
    }

    private static IEnumerable<Point3> Vertices(float[] triangles)
    {
        for (var i = 0; i + 2 < triangles.Length; i += 3)
        {
            yield return new Point3(triangles[i], triangles[i + 1], triangles[i + 2]);
        }
    }

    private static (double X, double Y) ToView(Point3 point, double[] rotation) => (
        (point.X * rotation[0]) + (point.Y * rotation[3]) + (point.Z * rotation[6]),
        (point.X * rotation[1]) + (point.Y * rotation[4]) + (point.Z * rotation[7]));

    // A face's bounding-box centre can be empty air, as in a hole; a triangle's centre is on the surface.
    private static Point3 LargestTriangleCentre(float[] triangles)
    {
        var points = Vertices(triangles).ToList();
        var (best, bestArea) = (points[0], -1.0);
        for (var i = 0; i + 2 < points.Count; i += 3)
        {
            var (a, b, c) = (points[i], points[i + 1], points[i + 2]);
            var area = Cross(new Point3(b.X - a.X, b.Y - a.Y, b.Z - a.Z), new Point3(c.X - a.X, c.Y - a.Y, c.Z - a.Z)).Length;
            if (area > bestArea)
            {
                (best, bestArea) = (new Point3((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3), area);
            }
        }

        return best;
    }

    private static Point3 EdgeMiddle(IEdge edge, ICurveParamData parameters)
    {
        var at = (double[])((ICurve)edge.GetCurve()).Evaluate2((parameters.UMinValue + parameters.UMaxValue) / 2, 0);
        return new Point3(at[0], at[1], at[2]);
    }

    private bool IsSame(object first, object second) => App.IsSame(first, second) == (int)swObjectEquality.swObjectSame;

    private bool TouchesEdge(object hit, IEdge edge) => (edge.GetTwoAdjacentFaces2() as object[] ?? []).Any(face => IsSame(hit, face));

    // The first face a ray from the camera meets is the one the viewer sees at that point.
    private static bool Visible(IModelDoc2 document, Point3 at, double[] rotation, double reach, Func<object, bool> owns)
    {
        var (dx, dy, dz) = (rotation[2], rotation[5], rotation[8]);
        document.ClearSelection2(true);
        var hit = document.Extension.SelectByRay(
            at.X + (dx * reach), at.Y + (dy * reach), at.Z + (dz * reach), -dx, -dy, -dz, RayRadius, (int)swSelectType_e.swSelFACES, false, 0, 0);
        return hit && ((ISelectionMgr)document.SelectionManager).GetSelectedObject6(1, -1) is { } face && owns(face);
    }
}
