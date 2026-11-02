namespace Datum.Core;

// Add-ins count as available only while loaded: a tool needs the add-in live, and that is
// not the same as its Tools > Add-Ins checkbox.
public sealed record SolidWorksCapabilities(int Year, LicenseType License, IReadOnlySet<Guid> LoadedAddIns)
{
    public static SolidWorksCapabilities From(SolidWorksInfo info) =>
        new(info.Revision.Year, info.License, info.AddIns.Where(a => a.Loaded).Select(a => a.Id).ToHashSet());
}
