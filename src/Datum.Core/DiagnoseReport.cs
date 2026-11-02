using System.Globalization;
using System.Text;

namespace Datum.Core;

public static class DiagnoseReport
{
    public static string Format(SolidWorksInfo info)
    {
        var culture = CultureInfo.InvariantCulture;
        var revision = info.Revision;
        var support = revision.IsSupported
            ? "supported"
            : $"not supported (needs {SolidWorksRevision.OldestSupportedYear} or later)";
        var license = info.License.IsEducational()
            ? $"{info.License} (educational, files are watermarked)"
            : info.License.ToString();

        var report = new StringBuilder()
            .Append(culture, $"{revision} (revision {revision.Major}.{revision.ServicePack}.{revision.Minor}), {support}\n")
            .Append(culture, $"License: {license}\n")
            .Append(culture, $"Process: {info.ProcessId}\n")
            .Append(culture, $"Add-ins: {info.AddIns.Count} registered, {info.AddIns.Count(a => a.Loaded)} loaded\n");

        foreach (var addIn in info.AddIns)
        {
            report.Append(culture, $"  [{(addIn.Loaded ? 'x' : ' ')}] {addIn.Title}\n");
        }

        return report.ToString();
    }
}
