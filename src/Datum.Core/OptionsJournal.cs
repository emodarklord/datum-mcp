using System.Text.Json;

namespace Datum.Core;

// Records SOLIDWORKS options Datum switches temporarily (toggles as 0 or 1), so a crash mid-change can be undone
// the next time Datum connects instead of leaving the user's setting altered.
public sealed class OptionsJournal(string path)
{
    private readonly Lock _lock = new();

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Datum", "options-journal.json");

    public IReadOnlyDictionary<string, int> Pending()
    {
        lock (_lock)
        {
            return Read();
        }
    }

    public void Record(string option, int originalValue)
    {
        lock (_lock)
        {
            var entries = Read();
            entries.TryAdd(option, originalValue);
            Write(entries);
        }
    }

    public void Clear(string option)
    {
        lock (_lock)
        {
            var entries = Read();
            if (entries.Remove(option))
            {
                Write(entries);
            }
        }
    }

    private Dictionary<string, int> Read()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (JsonException)
        {
            // A corrupt journal can't tell us the original values; starting fresh beats failing every connect.
            return [];
        }
    }

    private void Write(Dictionary<string, int> entries)
    {
        if (entries.Count == 0)
        {
            File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries));
    }
}
