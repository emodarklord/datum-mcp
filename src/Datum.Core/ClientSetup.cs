using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Datum.Core;

public enum ConfigFormat
{
    McpServersJson,
    VsCodeJson,
    CodexToml,
}

public sealed record ClientConfig(string Name, string Label, string? ConfigFile, ConfigFormat Format);

public sealed record ClientPaths(string Home, string AppData, string LocalAppData, string? CodexHome)
{
    public static ClientPaths Current() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable("CODEX_HOME"));
}

public sealed record ServerEntry(string Command, IReadOnlyList<string> Args);

public enum ChangeKind
{
    NotFound,
    Unchanged,
    Added,
    Updated,
    Removed,
    Refused,
}

// NewText is the whole file to write; null when there is nothing to write.
public sealed record ClientChange(ClientConfig Client, ChangeKind Kind, string? NewText, string Message);

public static class ClientSetup
{
    public const string ServerName = "datum";

    public static readonly string[] ClientNames = ["claude-code", "claude-desktop", "cursor", "vscode", "codex"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // A client counts as installed when its own config folder exists; Datum never creates one.
    public static IReadOnlyList<ClientConfig> Detect(ClientPaths paths)
    {
        var claudeCode = Path.Combine(paths.Home, ".claude.json");
        var codexHome = paths.CodexHome ?? Path.Combine(paths.Home, ".codex");
        return
        [
            new("claude-code", "Claude Code", File.Exists(claudeCode) || Directory.Exists(Path.Combine(paths.Home, ".claude")) ? claudeCode : null, ConfigFormat.McpServersJson),
            new("claude-desktop", "Claude Desktop", ClaudeDesktopConfig(paths), ConfigFormat.McpServersJson),
            new("cursor", "Cursor", InFolder(Path.Combine(paths.Home, ".cursor"), "mcp.json"), ConfigFormat.McpServersJson),
            new("vscode", "VS Code", InFolder(Path.Combine(paths.AppData, "Code", "User"), "mcp.json"), ConfigFormat.VsCodeJson),
            new("codex", "Codex", InFolder(codexHome, "config.toml"), ConfigFormat.CodexToml),
        ];
    }

    // entry null removes Datum. force accepts that saving a JSON file drops its comments.
    public static ClientChange Plan(ClientConfig client, ServerEntry? entry, bool force)
    {
        if (client.ConfigFile is null)
        {
            return new(client, ChangeKind.NotFound, null, "not found, skipped");
        }

        var existing = File.Exists(client.ConfigFile) ? File.ReadAllText(client.ConfigFile) : null;
        return client.Format == ConfigFormat.CodexToml
            ? PlanToml(client, existing, entry)
            : PlanJson(client, existing, entry, force);
    }

    // backUp copies the current file somewhere safe and returns where, or null when there was no file.
    public static string? Apply(ClientChange change, Func<string, string?> backUp)
    {
        if (change.NewText is null || change.Client.ConfigFile is not { } file)
        {
            return null;
        }

        var backup = backUp(file);
        var temp = file + ".datum-tmp";
        File.WriteAllText(temp, change.NewText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, file, overwrite: true);
        return backup;
    }

    private static string? ClaudeDesktopConfig(ClientPaths paths)
    {
        // The Store (MSIX) build reads %APPDATA%\Claude through Windows' file redirection, so its real config
        // lives in the package folder; its own "Edit Config" opens the unredirected file, which it never reads.
        var packages = Path.Combine(paths.LocalAppData, "Packages");
        var store = Directory.Exists(packages)
            ? Directory.EnumerateDirectories(packages, "Claude_*")
                .Select(package => Path.Combine(package, "LocalCache", "Roaming", "Claude"))
                .FirstOrDefault(Directory.Exists)
            : null;
        return InFolder(store ?? Path.Combine(paths.AppData, "Claude"), "claude_desktop_config.json");
    }

    private static string? InFolder(string? folder, string file) =>
        folder is not null && Directory.Exists(folder) ? Path.Combine(folder, file) : null;

    private static ClientChange PlanJson(ClientConfig client, string? existing, ServerEntry? entry, bool force)
    {
        JsonObject root;
        try
        {
            root = existing is null || existing.Trim().Length == 0
                ? []
                : JsonNode.Parse(existing, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                    ?? throw new JsonException();
        }
        catch (JsonException)
        {
            return new(client, ChangeKind.Refused, null, $"{client.ConfigFile} isn't valid JSON; fix or remove it, then run again");
        }

        var key = client.Format == ConfigFormat.VsCodeJson ? "servers" : "mcpServers";
        var servers = root[key] as JsonObject;
        var current = servers?[ServerName];
        if (entry is null)
        {
            if (current is null)
            {
                return new(client, ChangeKind.Unchanged, null, "not installed");
            }

            servers!.Remove(ServerName);
        }
        else
        {
            JsonObject desired = client.Format == ConfigFormat.VsCodeJson
                ? new() { ["type"] = "stdio", ["command"] = entry.Command, ["args"] = new JsonArray([.. entry.Args.Select(a => (JsonNode?)a)]) }
                : new() { ["command"] = entry.Command, ["args"] = new JsonArray([.. entry.Args.Select(a => (JsonNode?)a)]) };
            if (JsonNode.DeepEquals(current, desired))
            {
                return new(client, ChangeKind.Unchanged, null, "already up to date");
            }

            if (servers is null)
            {
                servers = [];
                root[key] = servers;
            }

            servers[ServerName] = desired;
        }

        if (existing is not null && HasComments(existing) && !force)
        {
            return new(client, ChangeKind.Refused, null, $"{client.ConfigFile} has comments that saving would drop; run again with --force (a backup is kept)");
        }

        var kind = entry is null ? ChangeKind.Removed : current is null ? ChangeKind.Added : ChangeKind.Updated;
        return new(client, kind, root.ToJsonString(Indented) + "\n", Describe(kind));
    }

    private static bool HasComments(string json)
    {
        try
        {
            JsonNode.Parse(json, documentOptions: new() { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = true });
            return false;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    // Only Datum's own table ([mcp_servers.datum] and its sub-tables) is touched; every other line stays as it was.
    private static ClientChange PlanToml(ClientConfig client, string? existing, ServerEntry? entry)
    {
        var lines = (existing ?? "").ReplaceLineEndings("\n").Split('\n').ToList();
        var start = lines.FindIndex(IsDatumHeader);
        var end = start;
        if (start >= 0)
        {
            end = start + 1;
            while (end < lines.Count && !(IsHeader(lines[end]) && !IsDatumHeader(lines[end])))
            {
                end++;
            }

            while (end > start + 1 && lines[end - 1].Trim().Length == 0)
            {
                end--;
            }
        }

        if (entry is null)
        {
            if (start < 0)
            {
                return new(client, ChangeKind.Unchanged, null, "not installed");
            }

            lines.RemoveRange(start, end - start);
            return new(client, ChangeKind.Removed, string.Join("\n", lines).TrimEnd('\n') + "\n", Describe(ChangeKind.Removed));
        }

        string[] desired =
        [
            $"[mcp_servers.{ServerName}]",
            $"command = {TomlString(entry.Command)}",
            $"args = [{string.Join(", ", entry.Args.Select(TomlString))}]",
        ];
        if (start >= 0 && lines.GetRange(start, end - start).Select(l => l.Trim()).SequenceEqual(desired))
        {
            return new(client, ChangeKind.Unchanged, null, "already up to date");
        }

        if (start >= 0)
        {
            lines.RemoveRange(start, end - start);
            lines.InsertRange(start, desired);
        }
        else
        {
            while (lines.Count > 0 && lines[^1].Trim().Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            if (lines.Count > 0)
            {
                lines.Add("");
            }

            lines.AddRange(desired);
        }

        var kind = start >= 0 ? ChangeKind.Updated : ChangeKind.Added;
        return new(client, kind, string.Join("\n", lines) + "\n", Describe(kind));
    }

    private static bool IsHeader(string line) => line.TrimStart().StartsWith('[');

    private static bool IsDatumHeader(string line)
    {
        var header = line.Trim().Replace("\"", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        return header == $"[mcp_servers.{ServerName}]" || header.StartsWith($"[mcp_servers.{ServerName}.", StringComparison.Ordinal);
    }

    private static string TomlString(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Describe(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "added",
        ChangeKind.Updated => "updated",
        ChangeKind.Removed => "removed",
        _ => kind.ToString(),
    };
}
