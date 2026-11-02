using Datum.Core;
using Datum.Mcp;

namespace Datum.Tests;

public class ToolRequirementsTests
{
    private static readonly Guid Simulation = Guid.NewGuid();

    [Theory]
    [InlineData(2022, null)]
    [InlineData(2023, null)]
    [InlineData(2024, "needs SOLIDWORKS 2024 or later (running 2023)")]
    public void ChecksTheSolidWorksYear(int minYear, string? reason)
    {
        Assert.Equal(reason, new RequiresSolidWorksAttribute(minYear).Check(Capabilities(2023, LicenseType.Professional)));
    }

    [Theory]
    [InlineData(LicenseType.Professional, null)]
    [InlineData(LicenseType.Full, null)]
    [InlineData(LicenseType.Student, "not available on Student licenses")]
    [InlineData(LicenseType.Maker, "not available on Maker licenses")]
    public void ChecksForACommercialLicense(LicenseType license, string? reason)
    {
        Assert.Equal(reason, new RequiresCommercialLicenseAttribute().Check(Capabilities(2026, license)));
    }

    [Fact]
    public void ChecksThatTheAddInIsLoaded()
    {
        var requirement = new RequiresAddInAttribute(Simulation.ToString("B"), "Simulation");

        Assert.Null(requirement.Check(Capabilities(2026, LicenseType.Premium, Simulation)));
        Assert.Equal(
            "needs the Simulation add-in loaded (tick it in SOLIDWORKS Tools > Add-Ins)",
            requirement.Check(Capabilities(2026, LicenseType.Premium)));
    }

    [Fact]
    public void BuildsCapabilitiesFromLoadedAddInsOnly()
    {
        var loaded = Guid.NewGuid();
        var info = new SolidWorksInfo(
            SolidWorksRevision.Parse("34.4.1"),
            LicenseType.Student,
            1,
            [new AddInInfo(loaded, "Loaded", Loaded: true), new AddInInfo(Guid.NewGuid(), "Not loaded", Loaded: false)]);

        var capabilities = SolidWorksCapabilities.From(info);

        Assert.Equal(2026, capabilities.Year);
        Assert.Equal(LicenseType.Student, capabilities.License);
        Assert.Equal([loaded], capabilities.LoadedAddIns);
    }

    private static SolidWorksCapabilities Capabilities(int year, LicenseType license, params Guid[] loaded) =>
        new(year, license, loaded.ToHashSet());
}
