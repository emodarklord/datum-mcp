using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Datum.Core;

public enum ApiMemberKind
{
    Method,
    Property,
}

public sealed record ApiParameter(string Name, string Type, bool ByRef, bool IsOut);

public sealed record ApiMember(string Interface, string Name, ApiMemberKind Kind, IReadOnlyList<ApiParameter> Parameters, string Returns, bool CanRead, bool CanWrite)
{
    public override string ToString() => Kind == ApiMemberKind.Property
        ? $"{Name} {{ {(CanRead ? "get; " : "")}{(CanWrite ? "set; " : "")}}} {Returns}"
        : $"{Name}({string.Join(", ", Parameters.Select(p => $"{(p.IsOut ? "out " : p.ByRef ? "ref " : "")}{p.Type} {p.Name}"))}) → {Returns}";
}

// Value is set only when writing a property. As names the interface the caller expects an object result to have.
public sealed record ApiCall(string Target, string Member, IReadOnlyList<JsonElement> Args, JsonElement? Value, string? As);

public sealed record ApiResult(string Value, IReadOnlyList<string> Outs, bool Wrote, ChangeSummary? Change);

public static partial class Gateway
{
    // File, window and session operations have curated tools with their own safety rules; the gateway leaves them alone.
    private static readonly (string Pattern, string Hint)[] Denied =
    [
        ("^(ExitApp|CloseAllDocuments|CloseDoc|QuitDoc|CloseAndReopen.*)$", "Use doc_close"),
        ("^(Save|Save[0-9]|SaveAs.*|SaveBMP|SaveToFile.*|ExportTo.*|Export.*File.*)$", "Use doc_save or doc_export, which keep to the allowed folders"),
        ("^(OpenDoc.*|LoadFile.*|NewDocument|NewPart|NewAssembly|NewDrawing.*|ActivateDoc.*|CopyDocument|MoveDocument)$", "Use doc_open, doc_new or doc_activate"),
        ("^(RunMacro.*|RunCommand|LoadAddIn|UnloadAddIn|CallBack)$", "Macros, commands and add-ins aren't reachable through the gateway"),
        ("^(SetUserPreference.*|ResetUserPreference.*)$", "Datum changes SOLIDWORKS options only through journaled, restored settings"),
        ("^(DeleteSelection.*|EditDelete|DeleteAllRelations|DeleteFile)$", "Use feature_delete, which checks dependants"),
        ("^(SendMsgToUser.*|SetStatusBarText|ShowBubbleTooltip.*)$", "The gateway doesn't open message boxes in SOLIDWORKS"),
    ];

    private static readonly Dictionary<string, double> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mm"] = 0.001,
        ["cm"] = 0.01,
        ["m"] = 1,
        ["in"] = 0.0254,
        ["ft"] = 0.3048,
        ["deg"] = Math.PI / 180,
        ["°"] = Math.PI / 180,
        ["rad"] = 1,
    };

    // Reading a property, or calling a Get…/Is…/Has… method, leaves the model alone; anything else might change it.
    public static bool IsRead(ApiMemberKind kind, string member, bool setting) =>
        !setting && (kind == ApiMemberKind.Property || ReadMethod().IsMatch(member));

    public static string? DeniedHint(string member) =>
        Denied.FirstOrDefault(d => Regex.IsMatch(member, d.Pattern, RegexOptions.IgnoreCase)).Hint;

    public static void RequireNotDenied(string member)
    {
        if (DeniedHint(member) is { } hint)
        {
            throw new DatumException(ErrorCode.NotAllowed, $"{member} isn't available through invoke", hint);
        }
    }

    public static void RequireWrites(GatewayWrites writes, string member)
    {
        switch (writes)
        {
            case GatewayWrites.Disabled:
                throw new DatumException(ErrorCode.NotAllowed, $"{member} may change the model, and gateway writes are off", "Start datum with --allow-invoke-writes (or run datum install --allow-invoke-writes)");
            case GatewayWrites.NotOwned:
                throw new DatumException(ErrorCode.NotOwned, $"{member} may change a document Datum didn't create", "Call doc_allow_edit first; it makes a backup");
        }
    }

    // "3 mm" → 0.003 and "30 deg" → 0.5236: SOLIDWORKS's own units are metres and radians.
    public static bool TryParseUnit(string text, out double value)
    {
        value = 0;
        var match = UnitValue().Match(text.Trim());
        if (!match.Success || !Units.TryGetValue(match.Groups[2].Value, out var factor))
        {
            return false;
        }

        value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * factor;
        return true;
    }

    [GeneratedRegex("^(I?Get|Is|Has)[A-Z0-9]")]
    private static partial Regex ReadMethod();

    [GeneratedRegex(@"^([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*([a-zA-Z°]+)$")]
    private static partial Regex UnitValue();
}

// Whether a gateway call that might change the model may go ahead, decided by the server's options and document ownership.
public enum GatewayWrites
{
    Allowed,
    Disabled,
    NotOwned,
}
