using System.Diagnostics;
using System.Runtime.InteropServices;
using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed class ComBackend : ISolidWorksBackend
{
    private const string ProgId = "SldWorks.Application";
    private const string ProcessName = "SLDWORKS";

    private ISldWorks? _app;
    private int _processId;

    private ISldWorks App => _app ?? throw new InvalidOperationException("Connect has not been called.");

    public void Connect(bool launch)
    {
        MessageFilter.RegisterOnCurrentThread();
        _app = Call(() => TryAttach() ?? (launch ? Launch() : null));
        if (_app is null)
        {
            throw Process.GetProcessesByName(ProcessName).Length > 0
                ? new DatumException(
                    ErrorCode.Unreachable,
                    "SOLIDWORKS is running but can't be reached",
                    "If it is still starting, wait and retry; otherwise run datum and SOLIDWORKS at the same privilege level")
                : new DatumException(ErrorCode.NotRunning, "SOLIDWORKS is not running", "Start SOLIDWORKS and wait until it has finished loading");
        }

        _processId = Call(App.GetProcessID);
    }

    public SolidWorksInfo ReadInfo() => Call(() => SolidWorksProbe.Read(App));

    public IReadOnlyList<BlockingDialog> FindBlockingDialogs()
    {
        var processId = _processId != 0 ? _processId : Process.GetProcessesByName(ProcessName).FirstOrDefault()?.Id ?? 0;
        return processId == 0 ? [] : DialogFinder.Find(processId);
    }

    public void Dispose()
    {
        if (_app is not null)
        {
            Marshal.ReleaseComObject(_app);
            _app = null;
        }
    }

    internal int ShowMessageBox(string text) =>
        Call(() => App.SendMsgToUser2(text, (int)swMessageBoxIcon_e.swMbInformation, (int)swMessageBoxBtn_e.swMbOk));

    private static ISldWorks? TryAttach()
    {
        NativeMethods.CLSIDFromProgID(ProgId, out var clsid);
        try
        {
            NativeMethods.GetActiveObject(ref clsid, 0, out var app);
            return (ISldWorks)app;
        }
        catch (COMException e) when (e.HResult == NativeMethods.MkEUnavailable)
        {
            return null;
        }
    }

    private static ISldWorks Launch()
    {
        var type = Type.GetTypeFromProgID(ProgId, throwOnError: true)!;
        var app = (ISldWorks)Activator.CreateInstance(type)!;
        app.Visible = true;
        return app;
    }

    private static T Call<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (COMException e) when (e.HResult is NativeMethods.RpcECallRejected or NativeMethods.RpcEServerCallRetryLater)
        {
            throw new DatumException(ErrorCode.DialogBlocking, "SOLIDWORKS rejected the call because it is busy or showing a dialog");
        }
        catch (COMException e)
        {
            throw new DatumException(ErrorCode.ComError, $"SOLIDWORKS returned 0x{e.HResult:X8}: {e.Message}");
        }
    }
}
