using System.Runtime.InteropServices;

namespace Datum.SolidWorks;

internal static class NativeMethods
{
    internal const int MkEUnavailable = unchecked((int)0x800401E3);

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern void CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);

    // Marshal.GetActiveObject is gone in .NET Core; this is the call it wrapped.
    [DllImport("oleaut32.dll", ExactSpelling = true, PreserveSig = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern void GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object active);
}
