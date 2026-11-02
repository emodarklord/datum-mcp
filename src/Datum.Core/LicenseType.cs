namespace Datum.Core;

public enum LicenseType
{
    Unknown = -1,
    Full = 0,
    Educational = 1,
    Student = 2,
    StudentDesignKit = 3,
    PersonalEdition = 4,
    Office = 5,
    Professional = 6,
    Premium = 7,
    Maker = 8,
    Ultimate = 9,
}

public static class LicenseTypes
{
    // Values match swLicenseType_e; newer releases may add members we don't know yet.
    public static LicenseType FromSolidWorks(int value) =>
        value is >= (int)LicenseType.Full and <= (int)LicenseType.Ultimate
            ? (LicenseType)value
            : LicenseType.Unknown;

    public static bool IsEducational(this LicenseType license) =>
        license is LicenseType.Educational or LicenseType.Student or LicenseType.StudentDesignKit or LicenseType.Maker;
}
