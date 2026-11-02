using Datum.Core;

namespace Datum.SolidWorks;

internal static class DialogFinder
{
    // A modal dialog disables its owner, so look for enabled windows whose owner is disabled.
    public static IReadOnlyList<BlockingDialog> Find(int processId)
    {
        var dialogs = new List<BlockingDialog>();
        NativeMethods.EnumWindows(
            (window, _) =>
            {
                if (NativeMethods.GetWindowThreadProcessId(window, out var owningProcess) == 0
                    || owningProcess != (uint)processId
                    || !NativeMethods.IsWindowVisible(window)
                    || !NativeMethods.IsWindowEnabled(window))
                {
                    return true;
                }

                var owner = NativeMethods.GetWindow(window, NativeMethods.GwOwner);
                if (owner != 0 && !NativeMethods.IsWindowEnabled(owner))
                {
                    dialogs.Add(new BlockingDialog(window, TitleOf(window)));
                }

                return true;
            },
            0);
        return dialogs;
    }

    public static void Close(nint window) => NativeMethods.PostMessageW(window, NativeMethods.WmClose, 0, 0);

    private static string TitleOf(nint window)
    {
        var buffer = new char[256];
        var length = NativeMethods.GetWindowTextW(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "(untitled)";
    }
}
