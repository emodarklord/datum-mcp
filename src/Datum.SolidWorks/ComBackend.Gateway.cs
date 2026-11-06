using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Datum.Core;
using SolidWorks.Interop.sldworks;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    private const string AppTarget = "app";
    private const string DocTarget = "doc";
    private const int DispUnknownName = unchecked((int)0x80020006);
    private const int DispTypeMismatch = unchecked((int)0x80020005);
    private const int DispBadParamCount = unchecked((int)0x8002000E);
    private const int DispParamNotFound = unchecked((int)0x80020004);

    // Returned as plain object by so many members that they're tried before the whole catalog.
    private static readonly string[] CommonInterfaces =
    [
        "IFeature", "IFace2", "IEdge", "IVertex", "IBody2", "ISketch", "ISketchSegment", "ISketchPoint", "IModelDoc2",
        "IPartDoc", "IAssemblyDoc", "IDrawingDoc", "IComponent2", "IConfiguration", "IConfigurationManager", "IDimension",
        "IDisplayDimension", "IMathTransform", "IMathPoint", "IMathVector", "IMassProperty", "IModelDocExtension",
        "IFeatureManager", "ISketchManager", "ISelectionMgr", "IView", "ISheet", "IAnnotation", "ICustomPropertyManager",
        "IMate2", "ILoop2", "ICurve", "ISurface", "IEquationMgr", "IRefPlane", "IRefAxis",
    ];

    private readonly Dictionary<string, string> _objectInterfaces = new(StringComparer.OrdinalIgnoreCase);
    private ApiCatalog? _catalog;

    private ApiCatalog Catalog => _catalog ??= ApiCatalog.Load(RedistFolder());

    public IReadOnlyList<ApiMember> DescribeMembers(string? id, string typeOrHandle, string? query) => Call<IReadOnlyList<ApiMember>>(() =>
    {
        var name = typeOrHandle.Contains('#', StringComparison.Ordinal) ? InterfaceOfHandle(typeOrHandle.Trim()) : typeOrHandle.Trim();
        var type = Catalog.Interface(name)
            ?? throw new DatumException(ErrorCode.NotFound, $"no interface '{name}' in this SOLIDWORKS API", "Interface names look like IFeature, IModelDoc2 or ISimpleFilletFeatureData2");
        return [.. ApiCatalog.Members(type).Where(m => query is null || m.Name.Contains(query, StringComparison.OrdinalIgnoreCase))];
    });

    public ApiResult Invoke(string? id, ApiCall request, GatewayWrites writes) => Call(() =>
    {
        Gateway.RequireNotDenied(request.Member);
        var document = id is null ? null : Find(id);
        var (target, interfaceName) = Target(id, document, request.Target.Trim());
        var type = Catalog.Interface(interfaceName);
        var (method, property) = type is null ? (null, null) : ApiCatalog.Find(type, request.Member);
        if (type is not null && method is null && property is null)
        {
            throw new DatumException(ErrorCode.NotFound, $"{interfaceName} has no member {request.Member}", $"Call api_members {interfaceName}");
        }

        var setting = request.Value is not null;
        var kind = property is not null ? ApiMemberKind.Property : ApiMemberKind.Method;
        var write = !Gateway.IsRead(kind, request.Member, setting);
        if (write)
        {
            if (request.Target.Trim().Equals(AppTarget, StringComparison.OrdinalIgnoreCase))
            {
                throw new DatumException(ErrorCode.NotAllowed, $"{request.Member} would change SOLIDWORKS itself", "The gateway only writes to documents");
            }

            Gateway.RequireWrites(writes, request.Member);
        }

        var parameters = method?.GetParameters() ?? property?.GetIndexParameters() ?? [];
        var signature = type is null ? request.Member : ApiCatalog.Members(type).First(m => string.Equals(m.Name, request.Member, StringComparison.OrdinalIgnoreCase)).ToString();
        var args = setting
            ? [Coerce(request.Value!.Value, property?.PropertyType, id, document)]
            : Arguments(parameters, request.Args, id, document, signature);
        var modifiers = parameters.Any(p => p.ParameterType.IsByRef) ? [ByRefModifier(parameters)] : (ParameterModifier[]?)null;
        var flags = setting ? BindingFlags.SetProperty : property is not null ? BindingFlags.GetProperty : BindingFlags.InvokeMethod;
        var volumeBefore = write && document is not null ? VolumeOf(document) : 0;
        object? value;
        try
        {
            value = target.GetType().InvokeMember(request.Member, flags, null, target, args, modifiers, CultureInfo.InvariantCulture, null);
        }
        catch (Exception e) when (ComFailure(e) is not null)
        {
            // Late binding wraps the COM error in a TargetInvocationException.
            var failure = ComFailure(e)!;
            throw failure.HResult switch
            {
                DispUnknownName => new DatumException(ErrorCode.NotFound, $"{interfaceName}.{request.Member} isn't in this SOLIDWORKS", $"Call api_members {interfaceName}"),
                DispTypeMismatch or DispBadParamCount or DispParamNotFound => new DatumException(
                    ErrorCode.InvalidArgument,
                    $"SOLIDWORKS didn't accept the arguments for {request.Member}",
                    $"It takes {signature}; numbers are metres and radians unless written like \"3 mm\""),
                _ => failure,
            };
        }

        var outs = parameters.Select((p, i) => (p, i)).Where(x => x.p.ParameterType.IsByRef)
            .Select(x => $"{(x.p.IsOut ? "out" : "ref")} {x.p.Name} = {Describe(args[x.i], null, null, id, document, request.Member, target)}")
            .ToList();
        ChangeSummary? change = null;
        if (write && document is not null)
        {
            document.EditRebuild3();
            change = Summary(document, volumeBefore);
        }

        var declared = setting ? null : method?.ReturnType ?? property?.PropertyType;
        return new ApiResult(setting ? "set" : Describe(value, declared, request.As, id, document, request.Member, target), outs, write, change);
    });

    private static COMException? ComFailure(Exception e) => e as COMException ?? (e as TargetInvocationException)?.InnerException as COMException;

    private static string RedistFolder()
    {
        using var process = Process.GetProcessById(_processId);
        var exe = process.MainModule?.FileName ?? throw new DatumException(ErrorCode.SolidworksError, "can't tell where SOLIDWORKS is installed");
        return Path.Combine(Path.GetDirectoryName(exe)!, "api", "redist");
    }

    private (object Target, string Interface) Target(string? id, IModelDoc2? document, string text)
    {
        if (text.Equals(AppTarget, StringComparison.OrdinalIgnoreCase))
        {
            return (App, "ISldWorks");
        }

        if (text.Equals(DocTarget, StringComparison.OrdinalIgnoreCase))
        {
            return (document ?? throw new DatumException(ErrorCode.NotFound, "no document is active", "Open or create one, or pass doc"), "IModelDoc2");
        }

        return (Entity(id, document, text), InterfaceOfHandle(text));
    }

    private object Entity(string? id, IModelDoc2? document, string handle)
    {
        var entry = _handles.Find(handle);
        if (entry.Live is { } live)
        {
            return live;
        }

        if (document is null || entry.DocumentId != id)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"{handle} belongs to document {entry.DocumentId}", $"Pass doc: {entry.DocumentId}");
        }

        return Resolve(id!, document, handle);
    }

    private string InterfaceOfHandle(string handle) => _handles.Find(handle).Kind switch
    {
        "feat" => "IFeature",
        "face" => "IFace2",
        "edge" => "IEdge",
        "seg" => "ISketchSegment",
        "pt" => "ISketchPoint",
        _ => _objectInterfaces.GetValueOrDefault(handle) ?? "object",
    };

    private object?[] Arguments(ParameterInfo[] parameters, IReadOnlyList<JsonElement> given, string? id, IModelDoc2? document, string signature)
    {
        if (parameters.Length == 0)
        {
            return [.. given.Select(g => Coerce(g, null, id, document))];
        }

        if (given.Count == parameters.Length)
        {
            return [.. parameters.Select((p, i) => Coerce(given[i], p.ParameterType, id, document))];
        }

        // Out parameters can be left out wherever they sit (GetMassProperties2 has one in the middle).
        var inputs = parameters.Count(p => !p.IsOut);
        if (given.Count != inputs)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"{given.Count} arguments given", $"It takes {signature}; out parameters can be left off");
        }

        var next = 0;
        return [.. parameters.Select(p => p.IsOut ? Default(p.ParameterType) : Coerce(given[next++], p.ParameterType, id, document))];
    }

    private object? Coerce(JsonElement value, Type? declared, string? id, IModelDoc2? document)
    {
        var type = declared is { IsByRef: true } ? declared.GetElementType() : declared;
        var code = type is null ? TypeCode.Object : Type.GetTypeCode(type);
        switch (value.ValueKind)
        {
            // SOLIDWORKS wants Nothing (a null object pointer) where VBA would pass it; a bare null arrives as an empty VARIANT and fails.
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return code is TypeCode.Object ? new DispatchWrapper(null) : null;
            case JsonValueKind.True or JsonValueKind.False:
                return value.GetBoolean();
            case JsonValueKind.Number when code is TypeCode.Int32 or TypeCode.Int16 or TypeCode.Int64:
                return value.TryGetInt32(out var whole) ? whole : throw new DatumException(ErrorCode.InvalidArgument, $"{value} isn't a whole number", "This parameter takes an integer or an enum name");
            case JsonValueKind.Number when code is TypeCode.Double or TypeCode.Single:
                return value.GetDouble();
            case JsonValueKind.Number:
                return value.TryGetInt32(out var integer) ? integer : value.GetDouble();
            case JsonValueKind.String:
                return CoerceText(value.GetString()!, code, id, document);
            case JsonValueKind.Array:
                var element = type is { IsArray: true } ? type.GetElementType() : null;
                var items = value.EnumerateArray().Select(v => Coerce(v, element, id, document)).ToList();
                return element is not null && element.FullName != "System.Object"
                    ? ToTypedArray(items, Type.GetTypeCode(element))
                    : items.All(v => v is double or int) ? items.Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray() : items.ToArray();
            default:
                throw new DatumException(ErrorCode.InvalidArgument, "objects can't be passed to invoke", "Pass a handle such as feat#3 or obj#7 for an API object");
        }
    }

    private object? CoerceText(string text, TypeCode code, string? id, IModelDoc2? document)
    {
        if (code is TypeCode.Double or TypeCode.Single or TypeCode.Object && Gateway.TryParseUnit(text, out var si))
        {
            return si;
        }

        if (code is TypeCode.Int32 or TypeCode.Int16 or TypeCode.Object && Catalog.TryEnum(text, out var number))
        {
            return number;
        }

        if (code is TypeCode.Object && IsTarget(text))
        {
            return Target(id, document, text).Target;
        }

        if (code is TypeCode.String or TypeCode.Object)
        {
            return text;
        }

        throw new DatumException(ErrorCode.InvalidArgument, $"'{text}' doesn't fit a {code} parameter", "Numbers can be written with a unit like \"3 mm\"; enums by name like swEndCondBlind");
    }

    private static bool IsTarget(string text) =>
        text.Equals(AppTarget, StringComparison.OrdinalIgnoreCase) || text.Equals(DocTarget, StringComparison.OrdinalIgnoreCase)
        || (text.Contains('#', StringComparison.Ordinal) && !text.Contains(' ', StringComparison.Ordinal));

    private static Array ToTypedArray(List<object?> items, TypeCode code) => code switch
    {
        TypeCode.Double => items.Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray(),
        TypeCode.Int32 => items.Select(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToArray(),
        TypeCode.Boolean => items.Select(v => v is true).ToArray(),
        TypeCode.String => items.Select(v => v?.ToString() ?? "").ToArray(),
        _ => items.ToArray(),
    };

    private static object? Default(Type declared)
    {
        var type = declared.IsByRef ? declared.GetElementType()! : declared;
        return Type.GetTypeCode(type) switch
        {
            TypeCode.Int32 => 0,
            TypeCode.Int16 => (short)0,
            TypeCode.Double => 0.0,
            TypeCode.Boolean => false,
            TypeCode.String => "",
            _ => null,
        };
    }

    private static ParameterModifier ByRefModifier(ParameterInfo[] parameters)
    {
        var modifier = new ParameterModifier(parameters.Length);
        for (var i = 0; i < parameters.Length; i++)
        {
            modifier[i] = parameters[i].ParameterType.IsByRef;
        }

        return modifier;
    }

    private string Describe(object? value, Type? declared, string? asInterface, string? id, IModelDoc2? document, string member, object target) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => JsonSerializer.Serialize(s),
        IConvertible number when value.GetType().IsPrimitive => Convert.ToString(number, CultureInfo.InvariantCulture)!,
        Array array => $"[{string.Join(", ", array.Cast<object?>().Take(50).Select(v => Describe(v, null, null, id, document, member, target)))}{(array.Length > 50 ? $", … ({array.Length} items)" : "")}]",
        _ when Marshal.IsComObject(value) => HandleFor(value, declared, asInterface, id, document, member, target),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private string HandleFor(object entity, Type? declared, string? asInterface, string? id, IModelDoc2? document, string member, object target)
    {
        if (entity is IModelDoc2 model)
        {
            return $"doc {IdOf(model)} ({model.GetTitle()})";
        }

        if (document is not null)
        {
            switch (entity)
            {
                case IFeature:
                    return $"{HandleOf(id!, document, "feat", entity)} (IFeature)";
                case IFace2:
                    return $"{HandleOf(id!, document, "face", entity)} (IFace2)";
                case IEdge:
                    return $"{HandleOf(id!, document, "edge", entity)} (IEdge)";
            }
        }

        var (name, note) = Identify(entity, declared, asInterface, member, target);
        var handle = _handles.HandleForLive(id ?? AppTarget, "obj", entity);
        _objectInterfaces[handle] = name;
        return $"{handle} ({name}){note}";
    }

    // COM objects from SOLIDWORKS don't say which interface they are, so it's found by asking: the declared return
    // type when there is one, then the likeliest candidates, then (slowly) every interface in the catalog.
    private (string Name, string Note) Identify(object entity, Type? declared, string? asInterface, string member, object target)
    {
        if (asInterface is not null)
        {
            var asked = Catalog.Interface(asInterface) ?? throw new DatumException(ErrorCode.NotFound, $"no interface '{asInterface}' in this SOLIDWORKS API");
            return Supports(entity, asked) ? (asked.Name, "") : throw new DatumException(ErrorCode.InvalidArgument, $"the result isn't an {asked.Name}", "Leave out as to let Datum find its interface");
        }

        if (declared is { IsInterface: true } && Catalog.Interface(declared.Name) is { } known)
        {
            return (known.Name, "");
        }

        var hint = member.Equals("GetDefinition", StringComparison.OrdinalIgnoreCase) && target is IFeature feature
            ? feature.GetTypeName2().TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9')
            : null;
        var likely = Catalog.Interfaces
            .Where(t => hint is not null && t.Name.StartsWith('I') && t.Name.Contains("FeatureData", StringComparison.Ordinal) && t.Name.Contains(hint, StringComparison.OrdinalIgnoreCase))
            // Objects also answer to the older versions of their interface (ISimpleFilletFeatureData), so the newest goes first.
            .OrderByDescending(t => t.Name, StringComparer.Ordinal)
            .Concat(CommonInterfaces.Select(Catalog.Interface).OfType<Type>());
        if (likely.FirstOrDefault(t => Supports(entity, t)) is { } found)
        {
            return (found.Name, "");
        }

        return Catalog.Interfaces.Where(t => t.Name.StartsWith('I')).FirstOrDefault(t => Supports(entity, t)) is { } any
            ? (any.Name, $" · found by trying every interface; pass as: \"{any.Name}\" to skip that")
            : ("object", " · interface unknown; pass as: with the interface from the API help");
    }

    private static bool Supports(object entity, Type type)
    {
        var unknown = Marshal.GetIUnknownForObject(entity);
        try
        {
            var iid = type.GUID;
            if (Marshal.QueryInterface(unknown, in iid, out var pointer) != 0)
            {
                return false;
            }

            Marshal.Release(pointer);
            return true;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
