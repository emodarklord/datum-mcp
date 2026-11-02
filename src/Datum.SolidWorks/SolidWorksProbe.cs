using System.Runtime.InteropServices;
using Datum.Core;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;

namespace Datum.SolidWorks;

internal static class SolidWorksProbe
{
    public static SolidWorksInfo Read(ISldWorks app)
    {
        var revision = SolidWorksRevision.Parse(app.RevisionNumber());
        var license = LicenseTypes.FromSolidWorks(app.GetCurrentLicenseType());
        var addIns = RegisteredAddIns(revision.Year)
            .Select(addIn => addIn with { Loaded = IsLoaded(app, addIn.Id) })
            .OrderBy(addIn => addIn.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SolidWorksInfo(revision, license, app.GetProcessID(), addIns);
    }

    // The API can't list add-ins. Built-ins like FeatureWorks appear only in the per-user
    // startup list, and their display names live inside SOLIDWORKS, so titles are best effort.
    private static IEnumerable<AddInInfo> RegisteredAddIns(int year)
    {
        (RegistryKey Hive, string Path)[] sources =
        [
            (Registry.LocalMachine, @"SOFTWARE\SolidWorks\Addins"),
            (Registry.LocalMachine, $@"SOFTWARE\SolidWorks\SOLIDWORKS {year}\Addins"),
            (Registry.CurrentUser, @"Software\SolidWorks\AddInsStartup"),
        ];
        var seen = new HashSet<Guid>();

        foreach (var (hive, path) in sources)
        {
            using var key = hive.OpenSubKey(path);
            if (key is null)
            {
                continue;
            }

            foreach (var name in key.GetSubKeyNames())
            {
                if (!Guid.TryParse(name, out var id) || !seen.Add(id))
                {
                    continue;
                }

                using var entry = key.OpenSubKey(name);
                yield return new AddInInfo(id, entry?.GetValue("Title") as string ?? ComClassName(id) ?? name, Loaded: false);
            }
        }
    }

    private static string? ComClassName(Guid id)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{id:B}");
        return key?.GetValue(null) is string { Length: > 0 } name ? name : null;
    }

    private static bool IsLoaded(ISldWorks app, Guid id)
    {
        var addIn = app.GetAddInObject(id.ToString("B"));
        if (addIn is null)
        {
            return false;
        }

        if (Marshal.IsComObject(addIn))
        {
            Marshal.ReleaseComObject(addIn);
        }

        return true;
    }
}
