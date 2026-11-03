using System.Reflection;
using System.Runtime.Loader;
using Datum.Core;

namespace Datum.SolidWorks;

// The whole SOLIDWORKS API surface, read from the interop assemblies in the user's own install. Datum's binary
// embeds only the types it uses itself, so it can't describe the rest; these files always match the running
// version. Only their metadata is used: calls go through COM late binding.
internal sealed class ApiCatalog
{
    private readonly Dictionary<string, Type> _interfaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _enumValues = new(StringComparer.OrdinalIgnoreCase);

    private ApiCatalog(Assembly sldworks, Assembly swconst)
    {
        foreach (var type in sldworks.GetExportedTypes().Where(t => t.IsInterface))
        {
            _interfaces.TryAdd(type.Name, type);
        }

        foreach (var type in swconst.GetExportedTypes().Where(t => t.IsEnum))
        {
            foreach (var name in Enum.GetNames(type))
            {
                _enumValues.TryAdd(name, Convert.ToInt32(Enum.Parse(type, name), System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    public IEnumerable<Type> Interfaces => _interfaces.Values;

    public static ApiCatalog Load(string redistFolder)
    {
        var sldworks = Path.Combine(redistFolder, "SolidWorks.Interop.sldworks.dll");
        var swconst = Path.Combine(redistFolder, "SolidWorks.Interop.swconst.dll");
        if (!File.Exists(sldworks) || !File.Exists(swconst))
        {
            throw new DatumException(
                ErrorCode.SolidworksError,
                $"the SOLIDWORKS API files aren't in {redistFolder}",
                "Repair the SOLIDWORKS install with its API component, or use the curated tools");
        }

        var context = new AssemblyLoadContext("solidworks-api");
        return new ApiCatalog(context.LoadFromAssemblyPath(sldworks), context.LoadFromAssemblyPath(swconst));
    }

    public Type? Interface(string name) =>
        _interfaces.GetValueOrDefault(name) ?? _interfaces.GetValueOrDefault("I" + name);

    public bool TryEnum(string name, out int value) => _enumValues.TryGetValue(name, out value);

    public static IReadOnlyList<ApiMember> Members(Type type)
    {
        var methods = AllMethods(type)
            .Select(m => new ApiMember(type.Name, m.Name, ApiMemberKind.Method, [.. m.GetParameters().Select(Describe)], TypeName(m.ReturnType), true, false));
        var properties = AllProperties(type)
            .Select(p => new ApiMember(type.Name, p.Name, ApiMemberKind.Property, [.. p.GetIndexParameters().Select(Describe)], TypeName(p.PropertyType), p.CanRead, p.CanWrite));
        return [.. properties.Concat(methods).DistinctBy(m => m.Name).OrderBy(m => m.Name, StringComparer.Ordinal)];
    }

    public static (MethodInfo? Method, PropertyInfo? Property) Find(Type type, string member) =>
        (AllMethods(type).FirstOrDefault(m => string.Equals(m.Name, member, StringComparison.OrdinalIgnoreCase)),
         AllProperties(type).FirstOrDefault(p => string.Equals(p.Name, member, StringComparison.OrdinalIgnoreCase)));

    public static string TypeName(Type type)
    {
        var plain = type.IsByRef ? type.GetElementType()! : type;
        if (plain.IsArray)
        {
            return TypeName(plain.GetElementType()!) + "[]";
        }

        return Type.GetTypeCode(plain) switch
        {
            TypeCode.Int32 => "int",
            TypeCode.Int16 => "short",
            TypeCode.Int64 => "long",
            TypeCode.Double => "double",
            TypeCode.Single => "float",
            TypeCode.Boolean => "bool",
            TypeCode.String => "string",
            _ => plain == typeof(void) || plain.FullName == "System.Void" ? "void" : plain.FullName == "System.Object" ? "object" : plain.Name,
        };
    }

    private static ApiParameter Describe(ParameterInfo p) => new(p.Name ?? "value", TypeName(p.ParameterType), p.ParameterType.IsByRef, p.IsOut);

    // Coclass interfaces (Feature) only re-declare their I-interface (IFeature), so both are searched.
    private static IEnumerable<MethodInfo> AllMethods(Type type) =>
        new[] { type }.Concat(type.GetInterfaces()).SelectMany(t => t.GetMethods()).Where(m => !m.IsSpecialName);

    private static IEnumerable<PropertyInfo> AllProperties(Type type) =>
        new[] { type }.Concat(type.GetInterfaces()).SelectMany(t => t.GetProperties());
}
