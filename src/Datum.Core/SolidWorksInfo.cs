namespace Datum.Core;

public sealed record SolidWorksInfo(
    SolidWorksRevision Revision,
    LicenseType License,
    int ProcessId,
    IReadOnlyList<AddInInfo> AddIns);

public sealed record AddInInfo(Guid Id, string Title, bool Loaded);
