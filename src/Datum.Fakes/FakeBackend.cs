using Datum.Core;

namespace Datum.Fakes;

public sealed class FakeBackend : ISolidWorksBackend
{
    private readonly object _gate = new();
    private bool _hung;
    private int _rejectNext;

    public SolidWorksInfo Info { get; set; } = new(SolidWorksRevision.Parse("34.4.1"), LicenseType.Student, 4242, []);

    public ErrorCode? ConnectFailure { get; set; }

    public IReadOnlyList<BlockingDialog> Dialogs { get; set; } = [];

    public bool Connected { get; private set; }

    public bool Disposed { get; private set; }

    public void Hang()
    {
        lock (_gate)
        {
            _hung = true;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            _hung = false;
            Monitor.PulseAll(_gate);
        }
    }

    public void RejectNextCall() => Volatile.Write(ref _rejectNext, 1);

    public void Connect(bool launch)
    {
        Enter();
        if (ConnectFailure is { } code)
        {
            throw new DatumException(code, "Fake connect failure");
        }

        Connected = true;
    }

    public SolidWorksInfo ReadInfo()
    {
        Enter();
        return Info;
    }

    public IReadOnlyList<BlockingDialog> FindBlockingDialogs() => Dialogs;

    public void Dispose()
    {
        Disposed = true;
        Release();
    }

    private void Enter()
    {
        lock (_gate)
        {
            while (_hung)
            {
                Monitor.Wait(_gate);
            }
        }

        if (Interlocked.Exchange(ref _rejectNext, 0) == 1)
        {
            throw new DatumException(ErrorCode.DialogBlocking, "SOLIDWORKS rejected the call");
        }
    }
}
