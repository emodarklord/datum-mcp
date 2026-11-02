namespace Datum.Core;

public interface ISolidWorksBackend : IDisposable
{
    // Connect and ReadInfo run on the STA dispatcher thread.
    void Connect(bool launch);

    SolidWorksInfo ReadInfo();

    // Called from any thread while the dispatcher may be stuck, so it must never touch COM.
    IReadOnlyList<BlockingDialog> FindBlockingDialogs();
}

public sealed record BlockingDialog(nint Handle, string Title);
