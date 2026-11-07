namespace Datum.Core;

public sealed record WeldmentProfile(string Standard, string Type, string Path);

public sealed record CutListItem(string Name, int Quantity, double LengthMm, string Description, IReadOnlyList<string> Bodies);

public sealed record MemberSegment(int Index, Point3 Start, Point3 End);

public sealed record StructuralMemberSpec(string Path, string Profile, string Size) : FeatureSpec
{
    public override string Kind => "structural member";

    public override string FailureHint => "Check the sketch is lines and arcs only, and that the profile and size exist in weldment_profiles.";

    public override void Validate()
    {
        Require(Path.StartsWith("feat#", StringComparison.OrdinalIgnoreCase), "a path sketch handle (feat#N from sketch_end)");
        Require(!string.IsNullOrWhiteSpace(Profile), "a profile file from weldment_profiles");
        Require(!string.IsNullOrWhiteSpace(Size), "a size from weldment_profiles");
    }
}

public sealed record WeldTrimSpec(IReadOnlyList<string> Trim, IReadOnlyList<string> By) : FeatureSpec
{
    public override string Kind => "weld trim";

    public override string FailureHint => "The bodies must touch or overlap. Body names come from cut_list.";

    public override void Validate()
    {
        Require(Trim.Count > 0, "at least one body to trim");
        Require(By.Count > 0, "at least one body to trim against");
        Require(!Trim.Intersect(By, StringComparer.OrdinalIgnoreCase).Any(), "different bodies to trim and to trim against");
    }
}

public static class WeldmentPaths
{
    private const double Tolerance = 1e-6;

    // A chain runs through corners where exactly two segments meet, and those corners get mitred. It stops at a junction
    // of three or more, because a member can't branch.
    public static IReadOnlyList<IReadOnlyList<int>> Chains(IReadOnlyList<MemberSegment> segments)
    {
        static bool Same(Point3 a, Point3 b) => Math.Abs(a.X - b.X) < Tolerance && Math.Abs(a.Y - b.Y) < Tolerance && Math.Abs(a.Z - b.Z) < Tolerance;
        List<MemberSegment> At(Point3 point) => [.. segments.Where(s => Same(s.Start, point) || Same(s.End, point))];

        var left = segments.ToList();
        var chains = new List<IReadOnlyList<int>>();
        while (left.Count > 0)
        {
            var chain = new List<MemberSegment> { left[0] };
            left.RemoveAt(0);
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var ends in chain.ToList().SelectMany(s => new[] { s.Start, s.End }))
                {
                    var meeting = At(ends);
                    var next = meeting.Count == 2 ? meeting.FirstOrDefault(left.Contains) : null;
                    if (next is not null)
                    {
                        chain.Add(next);
                        left.Remove(next);
                        grew = true;
                    }
                }
            }

            chains.Add([.. chain.Select(s => s.Index).Order()]);
        }

        return chains;
    }
}
