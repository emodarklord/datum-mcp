using Datum.Core;

namespace Datum.Mcp;

[AttributeUsage(AttributeTargets.Method)]
public sealed class ToolsetAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

public interface IToolRequirement
{
    string? Check(SolidWorksCapabilities capabilities);
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresSolidWorksAttribute(int minYear) : Attribute, IToolRequirement
{
    public int MinYear { get; } = minYear;

    public string? Check(SolidWorksCapabilities capabilities) =>
        capabilities.Year >= MinYear ? null : $"needs SOLIDWORKS {MinYear} or later (running {capabilities.Year})";
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequiresAddInAttribute(string id, string name) : Attribute, IToolRequirement
{
    public Guid Id { get; } = Guid.Parse(id);

    public string Name { get; } = name;

    public string? Check(SolidWorksCapabilities capabilities) =>
        capabilities.LoadedAddIns.Contains(Id) ? null : $"needs the {Name} add-in loaded (tick it in SOLIDWORKS Tools > Add-Ins)";
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresCommercialLicenseAttribute : Attribute, IToolRequirement
{
    public string? Check(SolidWorksCapabilities capabilities) =>
        capabilities.License.IsEducational() ? $"not available on {capabilities.License} licenses" : null;
}
