using System.Globalization;

namespace Datum.Core;

// The write-safety policy: where Datum may write, which documents it may change, and the
// backups it takes before touching a file it didn't create.
public sealed class Workspace
{
    private readonly HashSet<string> _editable = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ownFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unsavedModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private readonly string _backupRoot;

    public Workspace(IReadOnlyList<string> allowedFolders, string backupRoot)
    {
        if (allowedFolders.Count == 0)
        {
            throw new ArgumentException("At least one allowed folder is required.", nameof(allowedFolders));
        }

        AllowedFolders = [.. allowedFolders.Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)))];
        _backupRoot = backupRoot;
        Requirements = new RequirementStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(backupRoot))!, "requirements"));
    }

    public IReadOnlyList<string> AllowedFolders { get; }

    public RequirementStore Requirements { get; }

    public static Workspace Create(IReadOnlyList<string> cliFolders, string? environmentValue)
    {
        var folders = cliFolders.Concat(ParseFolderList(environmentValue)).ToList();
        if (folders.Count == 0)
        {
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Datum"));
        }

        var backups = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Datum", "backups");
        return new Workspace(folders, backups);
    }

    public static IReadOnlyList<string> ParseFolderList(string? value) =>
        value?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    public string ResolveReadPath(string path) => Path.GetFullPath(Path.Combine(AllowedFolders[0], path));

    public string ResolveWritePath(string path)
    {
        var full = ResolveReadPath(path);
        var folder = AllowedFolders.FirstOrDefault(f => IsInside(full, f));
        if (folder is null)
        {
            throw new DatumException(
                ErrorCode.PathNotAllowed,
                $"'{full}' is outside the folders Datum may write to",
                $"Write inside {string.Join(" or ", AllowedFolders)}, or start datum with --allow <folder>");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    public bool IsEditable(string documentId)
    {
        lock (_lock)
        {
            return _editable.Contains(documentId);
        }
    }

    public void AllowEdits(string documentId)
    {
        lock (_lock)
        {
            _editable.Add(documentId);
        }
    }

    // Set when a change made from a drawing landed in its model. SOLIDWORKS's own flag isn't reliable for this.
    public void MarkModelUnsaved(string path)
    {
        lock (_lock)
        {
            _unsavedModels.Add(path);
        }
    }

    public void MarkSaved(string path)
    {
        lock (_lock)
        {
            _unsavedModels.Remove(path);
        }
    }

    public IReadOnlyList<string> UnsavedModels()
    {
        lock (_lock)
        {
            return [.. _unsavedModels];
        }
    }

    // A file Datum saved stays its own when reopened later in the same run, though it gets a new document id.
    public void RememberOwnFile(string path)
    {
        lock (_lock)
        {
            _ownFiles.Add(path);
        }
    }

    public bool IsOwnFile(string path)
    {
        lock (_lock)
        {
            return _ownFiles.Contains(path);
        }
    }

    // Returns the backup's path, or null when there was nothing on disk to back up.
    public string? BackUp(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var folder = Path.Combine(_backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        var backup = Path.Combine(folder, Path.GetFileName(path));
        File.Copy(path, backup);
        return backup;
    }

    private static bool IsInside(string path, string folder) =>
        path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
