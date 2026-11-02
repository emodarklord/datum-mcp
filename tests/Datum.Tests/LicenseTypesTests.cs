using Datum.Core;

namespace Datum.Tests;

public class LicenseTypesTests
{
    [Theory]
    [InlineData(0, LicenseType.Full)]
    [InlineData(1, LicenseType.Educational)]
    [InlineData(2, LicenseType.Student)]
    [InlineData(3, LicenseType.StudentDesignKit)]
    [InlineData(4, LicenseType.PersonalEdition)]
    [InlineData(5, LicenseType.Office)]
    [InlineData(6, LicenseType.Professional)]
    [InlineData(7, LicenseType.Premium)]
    [InlineData(8, LicenseType.Maker)]
    [InlineData(9, LicenseType.Ultimate)]
    public void MapsSolidWorksLicenseValues(int raw, LicenseType expected)
    {
        Assert.Equal(expected, LicenseTypes.FromSolidWorks(raw));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    [InlineData(99)]
    public void UnrecognisedValuesAreUnknown(int raw)
    {
        Assert.Equal(LicenseType.Unknown, LicenseTypes.FromSolidWorks(raw));
    }

    [Theory]
    [InlineData(LicenseType.Educational, true)]
    [InlineData(LicenseType.Student, true)]
    [InlineData(LicenseType.StudentDesignKit, true)]
    [InlineData(LicenseType.Maker, true)]
    [InlineData(LicenseType.Full, false)]
    [InlineData(LicenseType.Professional, false)]
    [InlineData(LicenseType.Premium, false)]
    [InlineData(LicenseType.Unknown, false)]
    public void FlagsEducationalLicenses(LicenseType license, bool educational)
    {
        Assert.Equal(educational, license.IsEducational());
    }
}
