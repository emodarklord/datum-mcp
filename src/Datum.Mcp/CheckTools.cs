using System.ComponentModel;
using System.Globalization;
using System.Text;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class CheckTools
{
    private const string Toolset = "check";

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FlexTimeout = TimeSpan.FromMinutes(5);
    private const double DefaultFlexPercent = 10;

    [Toolset(Toolset)]
    [McpServerTool(Name = "model_measure", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Measure one entity, or the distance, angle and clearance between two. Takes face#, edge# and plane feat# handles, Front, Top or Right, or in an assembly a component's plane such as comp#5:front. "
        + "Between a plane and a face or edge, 'plane offset' is the distance from the plane to its centre, so use it to locate a hole. "
        + "Use it to verify a size or a gap rather than trusting a rebuild. Results are in mm and degrees.")]
    public static Task<CallToolResult> Measure(
        SolidWorksConnection solidWorks,
        [Description("A face#, edge# or plane feat# handle, or Front, Top or Right")] string first,
        [Description("A second entity, to measure between the two")] string? second = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                string[] entities = second is null ? [first] : [first, second];
                var measured = await client.RunAsync(b => b.Measure(target.Id, entities), ReadTimeout, cancellationToken).ConfigureAwait(false);
                return $"{string.Join(" to ", entities)}: {measured}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "sketch_read", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Read a finished sketch: its entities (mm, in sketch coordinates), relations, driving dimensions and whether it's fully defined. "
        + "Use it to understand a sketch someone else made.")]
    public static Task<CallToolResult> ReadSketch(
        SolidWorksConnection solidWorks,
        [Description("Sketch handle, feat#N from model_tree")] string sketch,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await PartAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var detail = await client.RunAsync(b => b.ReadSketchDetail(target.Id, sketch), ReadTimeout, cancellationToken).ConfigureAwait(false);
                var report = new StringBuilder($"{detail.Sketch.Handle} {detail.Sketch.Name}: {detail.Sketch.Status}, {detail.Items.Count} items\n");
                foreach (var item in detail.Items)
                {
                    report.Append(Item(item)).Append('\n');
                }

                report.Append(detail.Relations.Count == 0 ? "No relations\n" : "Relations:\n");
                foreach (var relation in detail.Relations)
                {
                    report.Append(CultureInfo.InvariantCulture, $"{relation.Kind}: {string.Join(", ", relation.Entities)}\n");
                }

                report.Append(detail.Dimensions.Count == 0 ? "No driving dimensions\n" : "Dimensions:\n");
                foreach (var dimension in detail.Dimensions)
                {
                    report.Append(CultureInfo.InvariantCulture, $"{dimension.Name} {Numbers.Format(dimension.Value)}{dimension.Unit}\n");
                }

                return report.ToString();
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "model_summary", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Understand a part in one call, especially one you didn't build: units, material, mass and size, every feature with its dimensions, "
        + "and a list of problems (rebuild errors with their cause and fix, sketches that aren't fully defined, a missing material). "
        + "An empty problem list doesn't mean the part meets its requirements.")]
    public static Task<CallToolResult> Summary(
        SolidWorksConnection solidWorks,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await PartAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var model = await client.RunAsync(b => b.ReadModel(target.Id), ReadTimeout, cancellationToken).ConfigureAwait(false);
                var facts = await client.RunAsync(b => b.ReadFacts(target.Id), ReadTimeout, cancellationToken).ConfigureAwait(false);
                var tree = await client.RunAsync(b => b.ListFeatures(target.Id), ReadTimeout, cancellationToken).ConfigureAwait(false);
                var dimensions = await client.RunAsync(b => b.ListDimensions(target.Id, null), ReadTimeout, cancellationToken).ConfigureAwait(false);
                return Report(model, facts, Modelling(tree), dimensions);
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "model_flex", Destructive = false, OpenWorld = false)]
    [Description("Test whether a part survives changes: try each driving dimension up and down, rebuild, report what broke (rebuild errors, "
        + "changed body or face counts, values an equation controls), and put every value back. The saved file isn't touched. Takes a while on large parts.")]
    public static Task<CallToolResult> Flex(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Dimension names to test; default is all driving dimensions, up to 20")] string[]? dimensions = null,
        [Description("How far to move each value, in percent")] double percent = 10,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                if (percent is <= 0 or >= 100)
                {
                    throw new DatumException(ErrorCode.InvalidArgument, $"percent {Numbers.Format(percent)} is out of range", "Use more than 0 and less than 100");
                }

                var target = await PartAsync(client, doc, cancellationToken).ConfigureAwait(false);
                DocumentTools.RequireEditable(workspace, target);
                return (await RunFlexAsync(solidWorks, client, target, dimensions, percent, cancellationToken).ConfigureAwait(false)).Report();
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "requirements_set", Destructive = false, OpenWorld = false)]
    [Description("Write down what a part must meet before building or judging it, with a source for each number, and confirm the list with the user. "
        + "Replaces any earlier list for this part. The list is kept across chats. Then call requirements_check.")]
    public static Task<CallToolResult> SetRequirements(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("The requirements")] RequirementSpec[] items,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var cleared = items.Select(item => item with { Mark = null, FirstReference = null, SecondReference = null }).ToList();
                RequirementChecks.Validate(cleared);
                var target = await PartAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var list = new List<RequirementSpec>();
                foreach (var item in cleared)
                {
                    list.Add(item.Check == "measure"
                        ? item with
                        {
                            FirstReference = await ReferenceAsync(client, target, item.First, cancellationToken).ConfigureAwait(false),
                            SecondReference = await ReferenceAsync(client, target, item.Second, cancellationToken).ConfigureAwait(false),
                        }
                        : item);
                }

                workspace.Requirements.Save(target, list);
                var manual = list.Count(item => item.Check == "manual");
                return $"Stored {list.Count} requirements for {target.Title}; {list.Count - manual} can be measured, {manual} need a person\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "requirements_check", Destructive = false, OpenWorld = false)]
    [Description("Measure the part against its stored requirements and report each as pass, fail or unverified. Use this before calling work finished. "
        + "Requirements with no automatic check stay unverified until marked; only mark what a person actually confirmed.")]
    public static Task<CallToolResult> CheckRequirements(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Manual requirements a person has confirmed")] RequirementMark[]? marks = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await PartAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var stored = workspace.Requirements.Load(target);
                if (stored.Count == 0)
                {
                    throw new DatumException(ErrorCode.NotFound, $"no requirements stored for {target.Title}", "Call requirements_set first");
                }

                var unknown = marks?.FirstOrDefault(m => !stored.Any(item => string.Equals(item.Id, m.Id, StringComparison.OrdinalIgnoreCase) && item.Check == "manual"));
                if (unknown is not null)
                {
                    throw new DatumException(ErrorCode.InvalidArgument, $"{unknown.Id} isn't a manual requirement", "Only manual items can be marked; the others are measured");
                }

                var marked = stored.Select(item => marks?.FirstOrDefault(m => string.Equals(m.Id, item.Id, StringComparison.OrdinalIgnoreCase)) is { } mark ? item with { Mark = mark.Note } : item).ToList();
                if (marks is { Length: > 0 })
                {
                    workspace.Requirements.Save(target, marked);
                }

                var facts = new RequirementFacts(
                    await client.RunAsync(b => b.ReadModel(target.Id), ReadTimeout, cancellationToken).ConfigureAwait(false),
                    await client.RunAsync(b => b.ListDimensions(target.Id, null), ReadTimeout, cancellationToken).ConfigureAwait(false),
                    await client.RunAsync(b => b.ListProperties(target.Id, ""), ReadTimeout, cancellationToken).ConfigureAwait(false),
                    await client.RunAsync(b => b.ListFeatures(target.Id), ReadTimeout, cancellationToken).ConfigureAwait(false),
                    (await client.RunAsync(b => b.ReadFacts(target.Id), ReadTimeout, cancellationToken).ConfigureAwait(false)).Sketches);
                var results = new List<RequirementResult>();
                foreach (var item in marked)
                {
                    Measurement? measurement = null;
                    DatumException? failure = null;
                    if (item.Check == "measure")
                    {
                        try
                        {
                            var first = await CurrentHandleAsync(client, target, item.First!, item.FirstReference, cancellationToken).ConfigureAwait(false);
                            string[] entities = item.Second is null
                                ? [first]
                                : [first, await CurrentHandleAsync(client, target, item.Second, item.SecondReference, cancellationToken).ConfigureAwait(false)];
                            measurement = await client.RunAsync(b => b.Measure(target.Id, entities), ReadTimeout, cancellationToken).ConfigureAwait(false);
                        }
                        catch (DatumException e) when (e.Code is ErrorCode.NotFound or ErrorCode.InvalidArgument)
                        {
                            failure = e;
                        }
                    }

                    var itemFacts = item.Check == "flex" && workspace.IsEditable(target.Id)
                        ? facts with { Flex = await RunFlexAsync(solidWorks, client, target, null, item.Target ?? DefaultFlexPercent, cancellationToken).ConfigureAwait(false) }
                        : facts;
                    results.Add(RequirementChecks.Evaluate(item, itemFacts, quantity => failure is null ? Measured(measurement, quantity) : throw failure));
                }

                return RequirementChecks.Report(target.Title, results);
            },
            cancellationToken));

    private static async Task<FlexResult> RunFlexAsync(
        SolidWorksConnection solidWorks, SolidWorksClient client, DocumentInfo target, string[]? dimensions, double percent, CancellationToken cancellationToken)
    {
        var result = await client.RunAsync(b => b.FlexDimensions(target.Id, dimensions, percent, FlexResult.DefaultLimit), FlexTimeout, cancellationToken).ConfigureAwait(false);
        if (result.RestoreProblem is { } problem)
        {
            solidWorks.Checkpoints.Record(target.Id, "flex test that didn't restore", new NoReversal(problem));
        }

        return result;
    }

    private static async Task<string?> ReferenceAsync(SolidWorksClient client, DocumentInfo target, string? handle, CancellationToken cancellationToken) =>
        handle is null ? null : await client.RunAsync(b => b.ReferenceOf(target.Id, handle), ReadTimeout, cancellationToken).ConfigureAwait(false);

    private static async Task<string> CurrentHandleAsync(SolidWorksClient client, DocumentInfo target, string handle, string? reference, CancellationToken cancellationToken) =>
        reference is null ? handle : await client.RunAsync(b => b.HandleFromReference(target.Id, reference), ReadTimeout, cancellationToken).ConfigureAwait(false);

    private static double? Measured(Measurement? measurement, string quantity) => quantity switch
    {
        "distance" => measurement?.DistanceMm,
        "normal_distance" => measurement?.NormalDistanceMm,
        "centre_distance" => measurement?.CenterDistanceMm,
        "plane_offset" => measurement?.PlaneOffsetMm,
        "clearance" => measurement?.ClearanceMm,
        "angle" => measurement?.AngleDegrees,
        "length" => measurement?.LengthMm,
        "radius" => measurement?.RadiusMm,
        "area" => measurement?.AreaMm2,
        _ => null,
    };

    private static string Item(SketchSegmentInfo item)
    {
        var line = item.Kind switch
        {
            "point" => $"{item.Handle} point at {item.Start}",
            "circle" => $"{item.Handle} circle centre {item.Center} radius {Numbers.Format(item.Radius ?? 0)}",
            "arc" => $"{item.Handle} arc {item.Start} to {item.End} centre {item.Center} radius {Numbers.Format(item.Radius ?? 0)}",
            "line" => $"{item.Handle} line {item.Start} to {item.End}",
            _ => $"{item.Handle} {item.Kind}",
        };
        return item.Construction ? line + " (construction)" : line;
    }

    // The origin and the three default planes are in every part.
    private static List<FeatureInfo> Modelling(IReadOnlyList<FeatureInfo> tree)
    {
        var defaultPlanes = tree.Where(f => f.TypeName == "RefPlane").Take(3).ToHashSet();
        return [.. tree.Where(f => f.TypeName != "OriginProfileFeature" && !defaultPlanes.Contains(f))];
    }

    private static string Report(ModelInfo model, ModelFacts facts, List<FeatureInfo> features, IReadOnlyList<DimensionInfo> dimensions)
    {
        var report = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"{model.Title}: {model.Bodies} {(model.Bodies == 1 ? "body" : "bodies")}, document units {facts.LengthUnit}, configuration {facts.Configuration} of {facts.Configurations}\n")
            .Append(CultureInfo.InvariantCulture, $"Material {model.Material ?? "none"} · mass {Numbers.Format(model.MassGrams)} g · volume {Numbers.Format(model.VolumeMm3)} mm³");
        if (model.Box is { Size: var size })
        {
            report.Append(CultureInfo.InvariantCulture, $" · box {Numbers.Format(size.X)} x {Numbers.Format(size.Y)} x {Numbers.Format(size.Z)} mm");
        }

        report.Append(CultureInfo.InvariantCulture, $"\nFeatures ({features.Count}):\n");
        var sketches = facts.Sketches.ToDictionary(s => s.Handle!, s => s.Status, StringComparer.Ordinal);
        foreach (var feature in features)
        {
            report.Append(new string(' ', feature.Depth * 2)).Append(CultureInfo.InvariantCulture, $"{feature.Handle} {feature.Name} [{feature.TypeName}]");
            if (sketches.TryGetValue(feature.Handle, out var status))
            {
                report.Append(' ').Append(status);
            }

            foreach (var dimension in dimensions.Where(d => d.Feature == feature.Handle))
            {
                report.Append(CultureInfo.InvariantCulture, $" {dimension.Name.Split('@')[0]}={Numbers.Format(dimension.Value)}{dimension.Unit}");
            }

            report.Append(feature.Suppressed ? " suppressed\n" : "\n");
        }

        var review = ModelProblems.Find(features, facts.Sketches, model.Material, model.Bodies);
        report.Append(review.Problems.Count == 0 ? "No problems found\n" : $"Problems ({review.Problems.Count}):\n");
        foreach (var problem in review.Problems)
        {
            report.Append(CultureInfo.InvariantCulture, $"- {problem}\n");
        }

        foreach (var note in review.Notes)
        {
            report.Append(CultureInfo.InvariantCulture, $"Note: {note}\n");
        }

        return report.ToString();
    }

    private static async Task<DocumentInfo> PartAsync(SolidWorksClient client, string? doc, CancellationToken cancellationToken)
    {
        var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
        return target.Type == DocumentType.Part
            ? target
            : throw new DatumException(ErrorCode.InvalidArgument, $"{target.Id} is not a part", "This tool works on parts for now");
    }
}
