using Datum.Core;

namespace Datum.Tests;

public class DiagnoseReportTests
{
    [Fact]
    public void ListsVersionLicenseAndAddIns()
    {
        var info = new SolidWorksInfo(
            SolidWorksRevision.Parse("34.4.1"),
            LicenseType.Student,
            ProcessId: 12345,
            AddIns:
            [
                new AddInInfo(Guid.Parse("219180b0-7183-4fe2-b167-4e2bfe534004"), "3DEXPERIENCE Marketplace", Loaded: true),
                new AddInInfo(Guid.Parse("11ee348e-e286-4860-aeb9-1a6ed4138eae"), "Autotrace", Loaded: false),
            ]);

        var expected = """
            SOLIDWORKS 2026 SP4.1 (revision 34.4.1), supported
            License: Student (educational, files are watermarked)
            Process: 12345
            Add-ins: 2 registered, 1 loaded
              [x] 3DEXPERIENCE Marketplace
              [ ] Autotrace

            """;

        Assert.Equal(expected.ReplaceLineEndings("\n"), DiagnoseReport.Format(info));
    }

    [Fact]
    public void FlagsUnsupportedVersionsAndCommercialLicenses()
    {
        var info = new SolidWorksInfo(SolidWorksRevision.Parse("28.5.0"), LicenseType.Professional, 1, []);

        var report = DiagnoseReport.Format(info);

        Assert.StartsWith("SOLIDWORKS 2020 SP5.0 (revision 28.5.0), not supported (needs 2021 or later)\n", report, StringComparison.Ordinal);
        Assert.Contains("License: Professional\n", report, StringComparison.Ordinal);
        Assert.Contains("Add-ins: 0 registered, 0 loaded\n", report, StringComparison.Ordinal);
    }
}
