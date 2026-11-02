using System.Runtime.InteropServices;
using Datum.Core;
using SolidWorks.Interop.sldworks;

namespace Datum.SolidWorks;

public sealed class SolidWorksSession : IDisposable
{
    private const string ProgId = "SldWorks.Application";

    private ISldWorks? _app;

    private SolidWorksSession(ISldWorks app) => _app = app;

    private ISldWorks App => _app ?? throw new ObjectDisposedException(nameof(SolidWorksSession));

    public static SolidWorksSession? TryAttach()
    {
        NativeMethods.CLSIDFromProgID(ProgId, out var clsid);
        try
        {
            NativeMethods.GetActiveObject(ref clsid, IntPtr.Zero, out var app);
            return new SolidWorksSession((ISldWorks)app);
        }
        catch (COMException e) when (e.HResult == NativeMethods.MkEUnavailable)
        {
            return null;
        }
    }

    public static SolidWorksSession Launch()
    {
        var type = Type.GetTypeFromProgID(ProgId, throwOnError: true)!;
        var app = (ISldWorks)Activator.CreateInstance(type)!;
        app.Visible = true;
        return new SolidWorksSession(app);
    }

    public SolidWorksInfo ReadInfo() => SolidWorksProbe.Read(App);

    public void Dispose()
    {
        if (_app is not null)
        {
            Marshal.ReleaseComObject(_app);
            _app = null;
        }
    }
}
