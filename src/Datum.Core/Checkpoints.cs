using System.Globalization;

namespace Datum.Core;

// How to undo one of Datum's own writes. SOLIDWORKS's undo stack also holds the user's GUI
// actions and splits API calls into several entries, so Datum keeps its own record instead.
public abstract record Reversal;

public sealed record DeleteCreated(IReadOnlyList<string> Features) : Reversal;

public sealed record RestoreDimension(string Name, double Value) : Reversal;

public sealed record RestoreSuppression(IReadOnlyList<string> Features, bool Suppressed) : Reversal;

// Deleted features can't be recreated, so a restore may not cross one.
public sealed record DeletionBarrier : Reversal;

public sealed record CheckpointEntry(string Id, string Label, Reversal? Reversal);

public sealed class Checkpoints
{
    private static int _next;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<CheckpointEntry>> _journals = [];

    // The checkpoint sits just before the step, so restoring it undoes the step too.
    public string Record(string documentId, string label, Reversal reversal) => Add(documentId, label, reversal);

    public string Mark(string documentId, string label) => Add(documentId, label, null);

    public IReadOnlyList<CheckpointEntry> List(string documentId)
    {
        lock (_lock)
        {
            return _journals.TryGetValue(documentId, out var journal) ? [.. Enumerable.Reverse(journal)] : [];
        }
    }

    // Newest first. Throws instead of returning a partial list, so a blocked restore changes nothing.
    public IReadOnlyList<CheckpointEntry> StepsToUndo(string documentId, string checkpointId)
    {
        lock (_lock)
        {
            var journal = _journals.GetValueOrDefault(documentId) ?? [];
            var index = journal.FindIndex(e => string.Equals(e.Id, checkpointId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new DatumException(ErrorCode.NotFound, $"no checkpoint '{checkpointId}' for {documentId}", "Call checkpoint_list");
            }

            var steps = journal.Skip(index).Where(e => e.Reversal is not null).Reverse().ToList();
            if (steps.FirstOrDefault(e => e.Reversal is DeletionBarrier) is { } barrier)
            {
                throw new DatumException(
                    ErrorCode.CannotRestore,
                    $"'{barrier.Label}' happened after {checkpointId}, and Datum can't bring deleted features back",
                    "Restore a later checkpoint, and suppress instead of deleting while experimenting");
            }

            return steps;
        }
    }

    // Drops the undone steps; a named checkpoint stays so it can be restored to again.
    public void Truncate(string documentId, string checkpointId)
    {
        lock (_lock)
        {
            if (_journals.TryGetValue(documentId, out var journal))
            {
                var index = journal.FindIndex(e => string.Equals(e.Id, checkpointId, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    var keep = journal[index].Reversal is null ? index + 1 : index;
                    journal.RemoveRange(keep, journal.Count - keep);
                }
            }
        }
    }

    public void Forget(string documentId)
    {
        lock (_lock)
        {
            _journals.Remove(documentId);
        }
    }

    private string Add(string documentId, string label, Reversal? reversal)
    {
        var id = string.Create(CultureInfo.InvariantCulture, $"cp{Interlocked.Increment(ref _next)}");
        lock (_lock)
        {
            if (!_journals.TryGetValue(documentId, out var journal))
            {
                _journals[documentId] = journal = [];
            }

            journal.Add(new CheckpointEntry(id, label, reversal));
        }

        return id;
    }
}
