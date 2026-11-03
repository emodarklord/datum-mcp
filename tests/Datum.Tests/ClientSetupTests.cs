using System.Text.Json.Nodes;
using Datum.Core;

namespace Datum.Tests;

public sealed class ClientSetupTests : IDisposable
{
    private static readonly ServerEntry Entry = new(@"C:\Tools\datum\datum.exe", ["--allow", @"D:\Parts"]);

    private readonly string _root = Directory.CreateTempSubdirectory("datum-clients-").FullName;
    private readonly ClientPaths _paths;

    public ClientSetupTests() =>
        _paths = new ClientPaths(Path.Combine(_root, "home"), Path.Combine(_root, "roaming"), Path.Combine(_root, "local"), null);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void SkipsClientsThatAreNotInstalledAndCreatesNothing()
    {
        Directory.CreateDirectory(_paths.Home);

        var changes = ClientSetup.Detect(_paths).Select(c => ClientSetup.Plan(c, Entry, force: false)).ToList();

        Assert.All(changes, c => Assert.Equal(ChangeKind.NotFound, c.Kind));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_paths.Home));
    }

    [Fact]
    public void AddsDatumBesideOtherServersAndIsIdempotent()
    {
        var file = Write(Path.Combine(_paths.Home, ".cursor", "mcp.json"), """{ "mcpServers": { "other": { "command": "x" } }, "keep": 1 }""");
        var cursor = Client("cursor");

        var first = ClientSetup.Plan(cursor, Entry, force: false);
        var backup = ClientSetup.Apply(first, Backup);
        var second = ClientSetup.Plan(cursor, Entry, force: false);

        var json = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal(ChangeKind.Added, first.Kind);
        Assert.Equal("x", (string?)json["mcpServers"]!["other"]!["command"]);
        Assert.Equal(1, (int?)json["keep"]);
        Assert.Equal(Entry.Command, (string?)json["mcpServers"]!["datum"]!["command"]);
        Assert.Equal(["--allow", @"D:\Parts"], json["mcpServers"]!["datum"]!["args"]!.AsArray().Select(a => (string?)a));
        Assert.NotNull(backup);
        Assert.Contains("\"other\"", File.ReadAllText(backup), StringComparison.Ordinal);
        Assert.Equal(ChangeKind.Unchanged, second.Kind);
        Assert.Null(second.NewText);
    }

    [Fact]
    public void UninstallRemovesOnlyDatum()
    {
        var file = Write(Path.Combine(_paths.Home, ".cursor", "mcp.json"), """{ "mcpServers": { "other": { "command": "x" } } }""");
        var cursor = Client("cursor");
        ClientSetup.Apply(ClientSetup.Plan(cursor, Entry, force: false), Backup);

        var removed = ClientSetup.Plan(cursor, null, force: false);
        ClientSetup.Apply(removed, Backup);
        var again = ClientSetup.Plan(cursor, null, force: false);

        var servers = JsonNode.Parse(File.ReadAllText(file))!["mcpServers"]!.AsObject();
        Assert.Equal(ChangeKind.Removed, removed.Kind);
        Assert.Equal(["other"], servers.Select(p => p.Key));
        Assert.Equal(ChangeKind.Unchanged, again.Kind);
    }

    [Fact]
    public void WritesVsCodesServersShape()
    {
        Directory.CreateDirectory(Path.Combine(_paths.AppData, "Code", "User"));

        var change = ClientSetup.Plan(Client("vscode"), Entry, force: false);

        var datum = JsonNode.Parse(change.NewText!)!["servers"]!["datum"]!;
        Assert.Equal(ChangeKind.Added, change.Kind);
        Assert.Equal("stdio", (string?)datum["type"]);
        Assert.Equal(Entry.Command, (string?)datum["command"]);
    }

    [Fact]
    public void RefusesInvalidJsonAndLeavesItAlone()
    {
        var file = Write(Path.Combine(_paths.Home, ".cursor", "mcp.json"), "{ not json");

        var change = ClientSetup.Plan(Client("cursor"), Entry, force: false);

        Assert.Equal(ChangeKind.Refused, change.Kind);
        Assert.Contains("isn't valid JSON", change.Message, StringComparison.Ordinal);
        Assert.Null(ClientSetup.Apply(change, Backup));
        Assert.Equal("{ not json", File.ReadAllText(file));
    }

    [Fact]
    public void NeedsForceToDropComments()
    {
        Write(Path.Combine(_paths.AppData, "Code", "User", "mcp.json"), "{\n  // mine\n  \"servers\": {}\n}");

        var refused = ClientSetup.Plan(Client("vscode"), Entry, force: false);
        var forced = ClientSetup.Plan(Client("vscode"), Entry, force: true);

        Assert.Equal(ChangeKind.Refused, refused.Kind);
        Assert.Contains("--force", refused.Message, StringComparison.Ordinal);
        Assert.Equal(ChangeKind.Added, forced.Kind);
    }

    [Fact]
    public void PrefersTheStoreBuildOfClaudeDesktop()
    {
        Directory.CreateDirectory(Path.Combine(_paths.AppData, "Claude"));
        var store = Path.Combine(_paths.LocalAppData, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude");
        Directory.CreateDirectory(store);

        Assert.Equal(Path.Combine(store, "claude_desktop_config.json"), Client("claude-desktop").ConfigFile);
    }

    [Fact]
    public void FallsBackToAppDataForClaudeDesktop()
    {
        Directory.CreateDirectory(Path.Combine(_paths.AppData, "Claude"));

        Assert.Equal(Path.Combine(_paths.AppData, "Claude", "claude_desktop_config.json"), Client("claude-desktop").ConfigFile);
    }

    [Fact]
    public void ReplacesOnlyDatumsCodexTable()
    {
        var file = Write(Path.Combine(_paths.Home, ".codex", "config.toml"), """
            model = "gpt"

            [mcp_servers.node_repl]
            command = "node"

            [mcp_servers.datum]
            command = "old.exe"
            args = []

            [mcp_servers.datum.env]
            X = "1"

            [mcp_servers.ableton]
            command = "ab"
            """.ReplaceLineEndings("\n"));
        var codex = Client("codex");

        var updated = ClientSetup.Plan(codex, Entry, force: false);
        ClientSetup.Apply(updated, Backup);
        var text = File.ReadAllText(file);
        var again = ClientSetup.Plan(codex, Entry, force: false);
        ClientSetup.Apply(ClientSetup.Plan(codex, null, force: false), Backup);
        var removed = File.ReadAllText(file);

        Assert.Equal(ChangeKind.Updated, updated.Kind);
        Assert.Contains("[mcp_servers.datum]\ncommand = \"C:\\\\Tools\\\\datum\\\\datum.exe\"\nargs = [\"--allow\", \"D:\\\\Parts\"]\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("old.exe", text, StringComparison.Ordinal);
        Assert.DoesNotContain("datum.env", text, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.node_repl]\ncommand = \"node\"", text, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.ableton]\ncommand = \"ab\"", text, StringComparison.Ordinal);
        Assert.Equal(ChangeKind.Unchanged, again.Kind);
        Assert.DoesNotContain("datum", removed, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.ableton]", removed, StringComparison.Ordinal);
    }

    [Fact]
    public void AddsACodexTableToTheEnd()
    {
        Write(Path.Combine(_paths.Home, ".codex", "config.toml"), "model = \"gpt\"\n");

        var change = ClientSetup.Plan(Client("codex"), new ServerEntry("datum.exe", []), force: false);

        Assert.Equal("model = \"gpt\"\n\n[mcp_servers.datum]\ncommand = \"datum.exe\"\nargs = []\n", change.NewText);
    }

    [Fact]
    public void PlanningWritesNothing()
    {
        var file = Write(Path.Combine(_paths.Home, ".cursor", "mcp.json"), "{}");

        ClientSetup.Plan(Client("cursor"), Entry, force: false);

        Assert.Equal("{}", File.ReadAllText(file));
    }

    private ClientConfig Client(string name) => ClientSetup.Detect(_paths).Single(c => c.Name == name);

    private string? Backup(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var backup = Path.Combine(_root, "backups", Guid.NewGuid().ToString("N"), Path.GetFileName(path));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(path, backup);
        return backup;
    }

    private static string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
}
