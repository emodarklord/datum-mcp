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

    [Toolset(Toolset)]
    [McpServerTool(Name = "model_measure", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Measure one entity, or the distance, angle and clearance between two. Takes face#, edge# and plane feat# handles. "
        + "Use it to verify a size or a gap rather than trusting a rebuild. Results are in mm and degrees.")]
    public static Task<CallToolResult> Measure(
        SolidWorksConnection solidWorks,
        [Description("A face#, edge# or plane feat# handle")] string first,
        [Description("A second handle, to measure between the two")] string? second = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await PartAsync(client, doc, cancellationToken).ConfigureAwait(false);
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
