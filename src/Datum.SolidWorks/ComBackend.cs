using System.Diagnostics;
using System.Runtime.InteropServices;
using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend : ISolidWorksBackend
{
    private const string ProgId = "SldWorks.Application";
    private const string ProcessName = "SLDWORKS";

    private ISldWorks? _app;
    private static int _processId;

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
        Call(() => { RestoreJournaledOptions(); return true; });
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
        // Started from a background process the window comes up minimized, and view captures need it on screen.
        app.FrameState = (int)swWindowState_e.swWindowMaximized;
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
        catch (COMException e) when (e.HResult is NativeMethods.RpcSServerUnavailable or NativeMethods.RpcEDisconnected or NativeMethods.CoEObjectNotConnected)
        {
            // A document closed under us disconnects its objects the same way SOLIDWORKS exiting does. Compare the
            // process, not just the name: after a crash and restart a new SOLIDWORKS is running, and this connection
            // has to be dropped so the next call attaches to it.
            throw Process.GetProcessesByName(ProcessName).Any(process => process.Id == _processId)
                ? new DatumException(ErrorCode.NotFound, "that document or entity was closed in SOLIDWORKS", "Call doc_list to see what is open")
                : new DatumException(ErrorCode.NotRunning, "SOLIDWORKS closed", "Start SOLIDWORKS again; the next call reconnects");
        }
        catch (COMException e)
        {
            throw new DatumException(ErrorCode.ComError, $"SOLIDWORKS returned 0x{e.HResult:X8}: {e.Message}");
        }
    }
}
