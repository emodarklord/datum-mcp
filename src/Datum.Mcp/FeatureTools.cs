using System.ComponentModel;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class FeatureTools
{
    private const string Toolset = "features";

    private static readonly TimeSpan FeatureTimeout = TimeSpan.FromMinutes(2);

    [McpServerTool(Name = "feature_extrude", Destructive = false, OpenWorld = false)]
    [Description("Extrude a sketch (feat# from sketch_end) into a boss, or cut it from the part. Depth in mm. "
        + "More features (revolve, fillet, chamfer, hole, pattern, mirror, shell) are in toolset 'features'.")]
    public static Task<CallToolResult> Extrude(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Sketch handle, feat#N")] string sketch,
        [Description("Depth in mm; not needed for through_all")] double? depth = null,
        [Description("Cut material away instead of adding it")] bool cut = false,
        [Description("blind, through_all or mid_plane")] string end = "blind",
        [Description("Go the other way from the sketch plane")] bool reverse = false,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new ExtrudeSpec(sketch, depth, cut, end, reverse), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "feature_revolve", Destructive = false, OpenWorld = false)]
    [Description("Revolve a sketch about its one centerline, adding or cutting material. Angle in degrees.")]
    public static Task<CallToolResult> Revolve(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Sketch handle, feat#N; it must contain exactly one centerline")] string sketch,
        [Description("Degrees, up to 360")] double angle = 360,
        [Description("Cut material away instead of adding it")] bool cut = false,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new RevolveSpec(sketch, angle, cut), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "feature_fillet", Destructive = false, OpenWorld = false)]
    [Description("Round edges (edge# handles from model_faces kind=edge) with a constant radius in mm.")]
    public static Task<CallToolResult> Fillet(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Edge handles")] string[] edges,
        [Description("Radius in mm")] double radius,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new FilletSpec(edges, radius), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "feature_chamfer", Destructive = false, OpenWorld = false)]
    [Description("Bevel edges (edge# handles). Equal distances at 45 degrees, or distance and angle.")]
    public static Task<CallToolResult> Chamfer(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Edge handles")] string[] edges,
        [Description("Distance in mm")] double distance,
        [Description("Angle in degrees")] double angle = 45,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new ChamferSpec(edges, distance, angle), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "feature_hole", Destructive = false, OpenWorld = false)]
    [Description("Drill a plain round hole into a flat face at a point given in model mm (as model_faces reports them). Omit depth to go through all.")]
    public static Task<CallToolResult> Hole(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Flat face handle, face#N")] string face,
        [Description("Hole centre on that face as \"x,y,z\" in model mm")] string at,
        [Description("Diameter in mm")] double diameter,
        [Description("Depth in mm; omit for through all")] double? depth = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new HoleSpec(face, at, diameter, depth), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "feature_pattern", Destructive = false, OpenWorld = false)]
    [Description("Repeat features in a line along a straight edge, or around a circular edge or cylindrical face. Count includes the original.")]
    public static Task<CallToolResult> Pattern(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("linear or circular")] string kind,
        [Description("Feature handles to repeat, feat#N")] string[] features,
        [Description("Linear: a straight edge#. Circular: a circular edge# or cylindrical face# to rotate about")] string along,
        [Description("Total copies including the original")] int count,
        [Description("Linear: mm between copies. Circular: degrees between copies; omit to space them evenly around 360")] double? spacing = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new PatternSpec(kind, features, along, count, spacing), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "feature_mirror", Destructive = false, OpenWorld = false)]
    [Description("Mirror features about Front, Top, Right, a plane (feat#) or a flat face (face#).")]
    public static Task<CallToolResult> Mirror(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Feature handles to mirror, feat#N")] string[] features,
        [Description("Front, Top, Right, a plane feat# or a flat face#")] string plane,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new MirrorSpec(features, plane), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "feature_shell", Destructive = false, OpenWorld = false)]
    [Description("Hollow the part to a wall thickness in mm, leaving the given faces open.")]
    public static Task<CallToolResult> Shell(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Wall thickness in mm")] double thickness,
        [Description("Faces to remove (left open), face#N")] string[] remove,
        [Description("Add the wall outside the current faces instead of inside")] bool outward = false,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new ShellSpec(thickness, remove, outward), cancellationToken);

    private static Task<CallToolResult> CreateAsync(
        SolidWorksConnection solidWorks, Workspace workspace, string? doc, FeatureSpec spec, CancellationToken cancellationToken) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var checkedSpec = spec is HoleSpec hole ? hole.Parsed() : spec;
                checkedSpec.Validate();
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                if (target.Type != DocumentType.Part)
                {
                    throw new DatumException(ErrorCode.InvalidArgument, $"{target.Id} is not a part", "Feature tools work on parts");
                }

                DocumentTools.RequireEditable(workspace, target);
                var result = await client.RunAsync(b => b.CreateFeature(target.Id, checkedSpec), FeatureTimeout, cancellationToken).ConfigureAwait(false);
                var delta = result.VolumeAfterMm3 - result.VolumeBeforeMm3;
                return $"{result.Name} is {result.Handle} · volume {Numbers.Format(result.VolumeBeforeMm3)} → {Numbers.Format(result.VolumeAfterMm3)} mm³ "
                    + $"({(delta >= 0 ? "+" : "")}{Numbers.Format(delta)}) · {result.Bodies} {(result.Bodies == 1 ? "body" : "bodies")}\n";
            },
            cancellationToken));
}
