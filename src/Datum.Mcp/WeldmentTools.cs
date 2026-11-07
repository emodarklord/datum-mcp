using System.ComponentModel;
using System.Globalization;
using System.Text;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class WeldmentTools
{
    private const string Toolset = "weldment";

    private static readonly TimeSpan WeldmentTimeout = TimeSpan.FromMinutes(3);

    [Toolset(Toolset)]
    [McpServerTool(Name = "weldment_profiles", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List the structural profiles installed with SOLIDWORKS. With no filter it gives standards and types. Give a type, such as 'iso/square tube', for its sizes and the file to pass to weldment_member.")]
    public static Task<CallToolResult> Profiles(
        SolidWorksConnection solidWorks,
        [Description("standard/type, e.g. iso/square tube")] string? type = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var profiles = await client.RunAsync(b => b.WeldmentProfiles(), cancellationToken: cancellationToken).ConfigureAwait(false);
                if (type is null)
                {
                    return profiles.Count == 0
                        ? "No profiles found. Check the weldment profiles folder in the SOLIDWORKS file locations\n"
                        : string.Concat(profiles.GroupBy(p => p.Standard).Select(group => $"{group.Key}: {string.Join(", ", group.Select(p => p.Type))}\n"));
                }

                var picked = profiles.FirstOrDefault(p => string.Equals($"{p.Standard}/{p.Type}", type.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new DatumException(ErrorCode.NotFound, $"no profile '{type}'", "Call weldment_profiles with no type for the list");
                var sizes = await client.RunAsync(b => b.WeldmentSizes(picked.Path), cancellationToken: cancellationToken).ConfigureAwait(false);
                return $"{picked.Path}\n{string.Join(", ", sizes)}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "weldment_member", Destructive = false, OpenWorld = false)]
    [Description("Add structural members along a sketch of centrelines, 2D or 3D. Connected lines become one mitred run, and members meeting at a junction in the same sketch trim to each other. Use weldment_trim where members from separate sketches meet.")]
    public static Task<CallToolResult> Member(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Path sketch handle, feat#N")] string sketch,
        [Description("Profile file from weldment_profiles")] string profile,
        [Description("Size from weldment_profiles")] string size,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        FeatureTools.CreateAsync(solidWorks, workspace, doc, new StructuralMemberSpec(sketch, profile, size), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "weldment_trim", Destructive = false, OpenWorld = false)]
    [Description("Trim members from separate weldment_member calls where they run into each other. Body names come from cut_list.")]
    public static Task<CallToolResult> Trim(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Bodies to shorten")] string[] trim,
        [Description("Bodies they stop against")] string[] by,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        FeatureTools.CreateAsync(solidWorks, workspace, doc, new WeldTrimSpec(trim, by), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "cut_list", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Update and list a weldment's cut list: each group of identical members with its quantity, length, description and body names.")]
    public static Task<CallToolResult> CutList(
        SolidWorksConnection solidWorks,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var items = await client.RunAsync(b => b.CutList(target.Id), WeldmentTimeout, cancellationToken).ConfigureAwait(false);
                if (items.Count == 0)
                {
                    return "No cut list items\n";
                }

                var report = new StringBuilder();
                foreach (var item in items)
                {
                    report.Append(CultureInfo.InvariantCulture, $"{item.Quantity} x {Numbers.Format(item.LengthMm)} mm {item.Description} ({string.Join(", ", item.Bodies)})\n");
                }

                return report.ToString();
            },
            cancellationToken));
}
