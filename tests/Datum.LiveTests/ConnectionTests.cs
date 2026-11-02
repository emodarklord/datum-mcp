using System.Diagnostics;
using Datum.Core;
using Datum.SolidWorks;

namespace Datum.LiveTests;

public class ConnectionTests
{
    [Fact]
    public void ReadsInfoFromRunningSolidWorks()
    {
        using var session = SolidWorksSession.TryAttach()
            ?? throw new InvalidOperationException("Start SOLIDWORKS before running the live tests.");

        var info = session.ReadInfo();

        Assert.True(info.Revision.IsSupported, $"Unsupported revision {info.Revision}");
        Assert.NotEqual(LicenseType.Unknown, info.License);
        Assert.Contains(Process.GetProcessesByName("SLDWORKS"), p => p.Id == info.ProcessId);
    }
}
