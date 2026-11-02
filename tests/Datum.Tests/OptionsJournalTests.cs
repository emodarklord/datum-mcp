using Datum.Core;

namespace Datum.Tests;

public sealed class OptionsJournalTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("datum-journal-").FullName;

    private string JournalPath => Path.Combine(_folder, "sub", "options-journal.json");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void RecordsAndClearsTheOriginalValue()
    {
        var journal = new OptionsJournal(JournalPath);

        journal.Record("swInputDimValOnCreate", originalValue: true);

        Assert.Equal(new Dictionary<string, bool> { ["swInputDimValOnCreate"] = true }, journal.Pending());

        journal.Clear("swInputDimValOnCreate");

        Assert.Empty(journal.Pending());
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void KeepsTheFirstOriginalWhenRecordedTwice()
    {
        var journal = new OptionsJournal(JournalPath);

        journal.Record("option", originalValue: true);
        journal.Record("option", originalValue: false);

        Assert.True(journal.Pending()["option"]);
    }

    [Fact]
    public void SurvivesARestart()
    {
        new OptionsJournal(JournalPath).Record("option", originalValue: false);

        Assert.False(new OptionsJournal(JournalPath).Pending()["option"]);
    }

    [Fact]
    public void MissingOrCorruptFilesMeanNothingPending()
    {
        Assert.Empty(new OptionsJournal(JournalPath).Pending());

        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        File.WriteAllText(JournalPath, "{ not json");

        Assert.Empty(new OptionsJournal(JournalPath).Pending());
    }
}
