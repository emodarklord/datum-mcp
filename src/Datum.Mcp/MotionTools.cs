using System.ComponentModel;
using System.Globalization;
using System.Text;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class MotionTools
{
    private const string Toolset = "motion";
    private const int MaxFrames = 240;

    private static readonly TimeSpan MotionTimeout = TimeSpan.FromMinutes(10);

    [Toolset(Toolset)]
    [McpServerTool(Name = "motion_new", Destructive = false, OpenWorld = false)]
    [Description("Start a motion study on an assembly, driven by its real mates. Add motors or paths, then motion_run to prove it moves, then motion_export to make the video.")]
    public static Task<CallToolResult> New(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("How long the study runs, in seconds")] double seconds,
        [Description("Study name")] string? name = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                if (seconds <= 0)
                {
                    throw new DatumException(ErrorCode.InvalidArgument, "a motion study needs seconds above 0");
                }

                var target = await AssemblyTools.AssemblyAsync(client, doc, cancellationToken).ConfigureAwait(false);
                DocumentTools.RequireEditable(workspace, target);
                var made = await client.RunAsync(b => b.NewMotionStudy(target.Id, name, seconds), MotionTimeout, cancellationToken).ConfigureAwait(false);
                Record(solidWorks, target, $"added {made}");
                return $"{made}: {Numbers.Format(seconds)} s\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "motion_motor", Destructive = false, OpenWorld = false)]
    [Description("Drive a cylindrical face in a motion study: at a constant rpm, or swinging out to a number of degrees and back, which suits steering and pedals. A swing goes one way from where the part starts. Mates carry the movement to everything linked to it.")]
    public static Task<CallToolResult> Motor(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Study name")] string study,
        [Description("Cylindrical face#, from model_faces with component set")] string face,
        [Description("Constant speed in rpm")] double? rpm = null,
        [Description("Degrees out and back, instead of rpm")] double? swing = null,
        [Description("Seconds for one full swing, with swing")] double? period = null,
        [Description("Turn the other way")] bool reverse = false,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var spec = new MotionMotorSpec(face, rpm, swing, period, reverse);
                spec.Validate();
                var target = await AssemblyTools.AssemblyAsync(client, doc, cancellationToken).ConfigureAwait(false);
                DocumentTools.RequireEditable(workspace, target);
                var motor = await client.RunAsync(b => b.AddMotionMotor(target.Id, study, spec), MotionTimeout, cancellationToken).ConfigureAwait(false);
                Record(solidWorks, target, $"added {motor}");
                return $"{motor} on {face}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "motion_path", Destructive = false, OpenWorld = false)]
    [Description("Add a ready-made move to a motion study: a turntable turn of the whole model, or the assembly exploding or collapsing. Explode and collapse need an exploded view in the active configuration.")]
    public static Task<CallToolResult> AddPath(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Study name")] string study,
        [Description("turntable, explode or collapse")] string kind,
        [Description("How long it takes, in seconds")] double seconds,
        [Description("When it starts, in seconds")] double start = 0,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var spec = new MotionPathSpec(kind, seconds, start);
                spec.Validate();
                var target = await AssemblyTools.AssemblyAsync(client, doc, cancellationToken).ConfigureAwait(false);
                DocumentTools.RequireEditable(workspace, target);
                await client.RunAsync(b => { b.AddMotionPath(target.Id, study, spec); return true; }, MotionTimeout, cancellationToken).ConfigureAwait(false);
                Record(solidWorks, target, $"added a {spec.NormalizedKind} to {study}");
                return $"{study}: {spec.NormalizedKind} from {Numbers.Format(start)} s for {Numbers.Format(seconds)} s\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "motion_run", Destructive = false, OpenWorld = false)]
    [Description("Calculate a motion study and report how far each moving component has turned and moved at evenly spaced times. Use it to prove the mechanism moves as designed: a study can calculate with nothing moving.")]
    public static Task<CallToolResult> Run(
        SolidWorksConnection solidWorks,
        [Description("Study name")] string study,
        [Description("How many times to sample, 1 to 40")] int samples = 4,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                if (samples is < 1 or > 40)
                {
                    throw new DatumException(ErrorCode.InvalidArgument, "samples must be 1 to 40");
                }

                var target = await AssemblyTools.AssemblyAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var found = await client.RunAsync(b => b.RunMotionStudy(target.Id, study, samples), MotionTimeout, cancellationToken).ConfigureAwait(false);
                var moving = found.GroupBy(s => s.Component).Where(group => group.Any(s => !s.Stationary)).SelectMany(group => group).OrderBy(s => s.Seconds).ToList();
                var still = found.Select(s => s.Component).Distinct().Count() - moving.Select(s => s.Component).Distinct().Count();
                var report = new StringBuilder(moving.Count == 0 ? "NOTHING MOVED\n" : "");
                foreach (var sample in moving)
                {
                    report.Append(CultureInfo.InvariantCulture, $"{sample}\n");
                }

                return report.Append(CultureInfo.InvariantCulture, $"{still} components didn't move\n").ToString();
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "motion_export", Destructive = false, OpenWorld = false)]
    [Description("Render a calculated motion study to an animated GIF from a standard view. Takes a few seconds a frame, with the SOLIDWORKS window showing.")]
    public static Task<CallToolResult> Export(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Study name")] string study,
        [Description("Where to write the .gif")] string path,
        [Description("front, back, left, right, top, bottom, iso, trimetric or dimetric")] string view = "iso",
        [Description("Frames a second, 1 to 30")] int fps = 12,
        [Description("Width in pixels; height follows at 3:4")] int width = 960,
        [Description("Replace an existing file, backing it up")] bool overwrite = false,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var angle = Views.Parse(view);
                var full = workspace.ResolveWritePath(path);
                if (!string.Equals(Path.GetExtension(full), ".gif", StringComparison.OrdinalIgnoreCase))
                {
                    throw new DatumException(ErrorCode.InvalidArgument, "motion_export writes a .gif", "Give a path ending in .gif");
                }

                if (fps is < 1 or > 30)
                {
                    throw new DatumException(ErrorCode.InvalidArgument, "fps must be 1 to 30");
                }

                var target = await AssemblyTools.AssemblyAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var seconds = await client.RunAsync(b => b.MotionStudySeconds(target.Id, study), MotionTimeout, cancellationToken).ConfigureAwait(false);
                var frames = (int)Math.Ceiling(seconds * fps) + 1;
                if (frames > MaxFrames)
                {
                    throw new DatumException(ErrorCode.InvalidArgument, $"{frames} frames is too many; the most is {MaxFrames}", "Lower fps or shorten the study");
                }

                var backup = DocumentTools.Replace(workspace, full, overwrite);
                var rendered = await client.RunAsync(b => b.RenderMotionStudy(target.Id, study, angle, width, width * 3 / 4, frames), MotionTimeout, cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(full, Gif.Encode(rendered, 100 / fps), cancellationToken).ConfigureAwait(false);
                return $"Exported {rendered.Count} frames to {full}, {new FileInfo(full).Length / 1024} KB{DocumentTools.BackupNote(backup)}\n";
            },
            cancellationToken));

    private static void Record(SolidWorksConnection solidWorks, DocumentInfo target, string label) =>
        solidWorks.Checkpoints.Record(target.Id, label, new NoReversal("a motion study"));
}
