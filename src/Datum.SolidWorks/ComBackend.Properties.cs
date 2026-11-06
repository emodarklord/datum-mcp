using System.Xml.Linq;
using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    // Ambient, diffuse, specular, shininess, transparency and emission, after the three colour values.
    private static readonly double[] PaintFinish = [1, 1, 0.5, 0.4, 0, 0];

    public string SetAppearance(string id, string? target, Colour colour) => Call(() =>
    {
        var document = Find(id);
        double[] values = [colour.Red, colour.Green, colour.Blue, .. PaintFinish];
        string coloured;
        switch (target is null ? document : Resolve(id, document, target))
        {
            case IModelDoc2 whole:
                whole.MaterialPropertyValues = values;
                coloured = whole.GetTitle();
                break;
            case IComponent2 component:
                component.MaterialPropertyValues = values;
                coloured = component.Name2;
                break;
            case IFeature feature:
                feature.SetMaterialPropertyValues2(values, (int)swInConfigurationOpts_e.swThisConfiguration, null);
                coloured = feature.Name;
                break;
            case IFace2 face:
                face.MaterialPropertyValues = values;
                coloured = target!;
                break;
            default:
                throw new DatumException(ErrorCode.InvalidArgument, $"{target} can't be coloured", "Use a comp#, feat# or face# handle");
        }

        document.GraphicsRedraw2();
        return coloured;
    });

    public MaterialChange SetMaterial(string id, string? name, string? database) => Call(() =>
    {
        var document = Find(id);
        var part = document as IPartDoc ?? throw new DatumException(ErrorCode.InvalidArgument, $"{id} is not a part", "Materials belong to parts");
        var configuration = (document.GetActiveConfiguration() as IConfiguration)?.Name ?? "";
        var oldName = part.GetMaterialPropertyName2(configuration, out var oldDatabase);
        var oldMass = MassOf(document);

        var (exact, source) = name is null || database is not null ? (name, database) : FindMaterial(name);
        part.SetMaterialPropertyName2(configuration, source ?? "", exact ?? "");
        var applied = part.GetMaterialPropertyName2(configuration, out _);
        if (!string.Equals(applied, exact ?? "", StringComparison.Ordinal))
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS didn't apply material '{exact}'; the part has '{applied}'");
        }

        document.EditRebuild3();
        return new MaterialChange(oldName is { Length: > 0 } ? oldName : null, oldDatabase is { Length: > 0 } ? oldDatabase : null, exact, oldMass, MassOf(document));
    });

    public IReadOnlyList<CustomProperty> ListProperties(string id, string configuration) => Call(() =>
    {
        var manager = Find(id).Extension.get_CustomPropertyManager(configuration);
        return Strings((object?)manager.GetNames()).Select(name =>
        {
            manager.Get6(name, false, out var expression, out var value, out _, out _);
            return new CustomProperty(name, expression, value);
        }).ToList();
    });

    // The names come back as object[], not string[].
    private static string[] Strings(object? array) => (array as object[] ?? []).Select(item => item?.ToString() ?? "").ToArray();

    public PropertyChange? SetProperty(string id, string name, string? value, string configuration) => Call(() =>
    {
        var manager = Find(id).Extension.get_CustomPropertyManager(configuration);
        var found = Strings((object?)manager.GetNames()).Contains(name, StringComparer.OrdinalIgnoreCase);
        manager.Get6(name, false, out var old, out _, out _, out _);
        if (value is null)
        {
            manager.Delete2(name);
            return null;
        }

        manager.Add3(name, (int)swCustomInfoType_e.swCustomInfoText, value, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
        manager.Get6(name, false, out var written, out _, out _, out _);
        return written == value
            ? new PropertyChange(name, found ? old : null, value)
            : throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS stored '{written}' for {name}, not '{value}'");
    });

    private static double MassOf(IModelDoc2 document) => ((document.Extension.CreateMassProperty() as IMassProperty)?.Mass ?? 0) * 1000;

    private (string Name, string Database) FindMaterial(string name)
    {
        var known = (App.GetMaterialDatabases() as string[] ?? [])
            .Where(File.Exists)
            .SelectMany(database => XDocument.Load(database).Descendants("material").Select(m => (Name: (string?)m.Attribute("name") ?? "", Database: database)))
            .ToList();
        var match = known.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match.Name is { Length: > 0 })
        {
            return match;
        }

        var closest = MaterialNames.Closest(known.Select(m => m.Name), name);
        throw new DatumException(
            ErrorCode.NotFound,
            $"no material named '{name}'",
            closest.Count == 0 ? "Check the name in SOLIDWORKS's material list" : $"Closest names: {string.Join(", ", closest)}");
    }
}
