using Datum.Core;

namespace Datum.Tests;

public sealed class WorkspaceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-ws-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ResolvesRelativePathsIntoTheFirstAllowedFolder()
    {
        var workspace = Create();

        Assert.Equal(Path.Combine(_root, "Datum", "bracket.SLDPRT"), workspace.ResolveWritePath("bracket.SLDPRT"));
        Assert.True(Directory.Exists(Path.Combine(_root, "Datum")));
    }

    [Theory]
    [InlineData(@"..\escape.SLDPRT")]
    [InlineData(@"sub\..\..\escape.SLDPRT")]
    [InlineData(@"..\Datum2\lookalike.SLDPRT")]
    [InlineData(@"Z:\elsewhere\part.SLDPRT")]
    public void RejectsWritesOutsideTheAllowedFolders(string path)
    {
        var error = Assert.Throws<DatumException>(() => Create().ResolveWritePath(path));

        Assert.Equal(ErrorCode.PathNotAllowed, error.Code);
    }

    [Fact]
    public void AllowsSubfoldersOfAnyAllowedFolder()
    {
        var second = Path.Combine(_root, "Second");
        var workspace = new Workspace([Path.Combine(_root, "Datum"), second], Path.Combine(_root, "backups"));

        Assert.Equal(Path.Combine(second, "deep", "a.step"), workspace.ResolveWritePath(Path.Combine(second, "deep", "a.step")));
    }

    [Fact]
    public void ReadsMayComeFromAnywhere()
    {
        Assert.Equal(@"Z:\elsewhere\part.SLDPRT", Create().ResolveReadPath(@"Z:\elsewhere\part.SLDPRT"));
    }

    [Fact]
    public void ParsesTheEnvironmentFolderList()
    {
        Assert.Equal([@"C:\a", @"D:\b c"], Workspace.ParseFolderList(@" C:\a ;; D:\b c ;"));
        Assert.Empty(Workspace.ParseFolderList(null));
    }

    [Fact]
    public void CombinesFlagAndEnvironmentFoldersInOrder()
    {
        var workspace = Workspace.Create([Path.Combine(_root, "flag")], Path.Combine(_root, "env"));

        Assert.Equal([Path.Combine(_root, "flag"), Path.Combine(_root, "env")], workspace.AllowedFolders);
    }

    [Fact]
    public void BacksUpACopyAndLeavesTheOriginal()
    {
        var workspace = Create();
        var original = workspace.ResolveWritePath("part.SLDPRT");
        File.WriteAllText(original, "v1");

        var backup = workspace.BackUp(original);

        Assert.NotNull(backup);
        Assert.StartsWith(Path.Combine(_root, "backups"), backup, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("v1", File.ReadAllText(backup));
        Assert.Equal("v1", File.ReadAllText(original));
        Assert.Null(workspace.BackUp(Path.Combine(_root, "missing.SLDPRT")));
    }

    [Fact]
    public void TracksEditableDocuments()
    {
        var workspace = Create();

        workspace.AllowEdits("d7");

        Assert.True(workspace.IsEditable("d7"));
        Assert.False(workspace.IsEditable("d8"));
    }

    private Workspace Create() => new([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));
}
