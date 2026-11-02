using System.Runtime.InteropServices;

namespace Datum.SolidWorks;

[ComImport]
[Guid("00000016-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleMessageFilter
{
    [PreserveSig]
    int HandleInComingCall(int callType, nint callerTask, int tickCount, nint interfaceInfo);

    [PreserveSig]
    int RetryRejectedCall(nint calleeTask, int tickCount, int rejectType);

    [PreserveSig]
    int MessagePending(nint calleeTask, int tickCount, int pendingType);
}

// Without a filter, a call SOLIDWORKS rejects while it is starting fails at once. Retry briefly,
// then give up so a modal dialog surfaces as RPC_E_CALL_REJECTED instead of an endless retry.
[ComVisible(true)]
internal sealed class MessageFilter : IOleMessageFilter
{
    private const int ServerCallIsHandled = 0;
    private const int ServerCallRetryLater = 2;
    private const int PendingMessageWaitDefaultProcess = 2;
    private const int RetryWindowMilliseconds = 2000;
    private const int RetryDelayMilliseconds = 100;
    private const int CancelCall = -1;

    public static void RegisterOnCurrentThread()
    {
        var result = NativeMethods.CoRegisterMessageFilter(new MessageFilter(), out _);
        Marshal.ThrowExceptionForHR(result);
    }

    public int HandleInComingCall(int callType, nint callerTask, int tickCount, nint interfaceInfo) => ServerCallIsHandled;

    public int RetryRejectedCall(nint calleeTask, int tickCount, int rejectType) =>
        rejectType == ServerCallRetryLater && tickCount < RetryWindowMilliseconds ? RetryDelayMilliseconds : CancelCall;

    public int MessagePending(nint calleeTask, int tickCount, int pendingType) => PendingMessageWaitDefaultProcess;
}
