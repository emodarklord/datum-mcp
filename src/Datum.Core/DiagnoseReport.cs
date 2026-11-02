using System.Globalization;
using System.Text;

namespace Datum.Core;

public static class DiagnoseReport
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Format(SolidWorksInfo info)
    {
        var report = new StringBuilder(Header(info))
            .Append(Culture, $"Process: {info.ProcessId}\n")
            .Append(Culture, $"Add-ins: {info.AddIns.Count} registered, {info.AddIns.Count(a => a.Loaded)} loaded\n");

        foreach (var addIn in info.AddIns)
        {
            report.Append(Culture, $"  [{(addIn.Loaded ? 'x' : ' ')}] {addIn.Title}\n");
        }

        return report.ToString();
    }

    public static string Header(SolidWorksInfo info)
    {
        var revision = info.Revision;
        var support = revision.IsSupported
            ? "supported"
            : $"not supported (needs {SolidWorksRevision.OldestSupportedYear} or later)";
        var license = info.License.IsEducational()
            ? $"{info.License} (educational, files are watermarked)"
            : info.License.ToString();

        return string.Create(
            Culture,
            $"{revision} (revision {revision.Major}.{revision.ServicePack}.{revision.Minor}), {support}\nLicense: {license}\n");
    }
}
