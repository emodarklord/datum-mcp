using System.Runtime.InteropServices;

namespace Datum.SolidWorks;

internal static class NativeMethods
{
    internal const int MkEUnavailable = unchecked((int)0x800401E3);
    internal const int RpcECallRejected = unchecked((int)0x80010001);
    internal const int RpcEServerCallRetryLater = unchecked((int)0x8001010A);
    internal const int RpcSServerUnavailable = unchecked((int)0x800706BA);
    internal const int RpcEDisconnected = unchecked((int)0x80010108);
    internal const int CoEObjectNotConnected = unchecked((int)0x800401FD);
    internal const uint GwOwner = 4;
    internal const uint WmClose = 0x0010;

    internal delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern void CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);

    // Marshal.GetActiveObject is gone in .NET Core; this is the call it wrapped.
    [DllImport("oleaut32.dll", ExactSpelling = true, PreserveSig = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern void GetActiveObject(ref Guid clsid, nint reserved, [MarshalAs(UnmanagedType.IUnknown)] out object active);

    [DllImport("ole32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int CoRegisterMessageFilter(IOleMessageFilter? newFilter, out IOleMessageFilter? oldFilter);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowEnabled(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int GetWindowTextW(nint window, [Out] char[] text, int maxCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint window, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
