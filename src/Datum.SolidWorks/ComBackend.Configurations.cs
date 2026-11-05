using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    private const int ExplodeComponentMark = 1;
    private const int NewConfigurationOptions = 1;

    public IReadOnlyList<ConfigurationInfo> ListConfigurations(string id) => Call(() =>
    {
        var document = Find(id);
        var active = ActiveConfigurationName(document);
        return ConfigurationNames(document).Select(name => new ConfigurationInfo(name, name == active)).ToList();
    });

    public string AddConfiguration(string id, string name) => Call(() =>
    {
        var document = Find(id);
        if (ConfigurationNames(document).Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"a configuration named {name} already exists", "Use config_activate");
        }

        document.AddConfiguration3(name, "", "", NewConfigurationOptions);
        return RequireActive(document, name);
    });

    public string ActivateConfiguration(string id, string name) => Call(() =>
    {
        var document = Find(id);
        var exact = ConfigurationNames(document).FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new DatumException(ErrorCode.NotFound, $"no configuration named {name}", "Call config_list");

        // ShowConfiguration2 can return false when that configuration is already active, so read the result instead.
        document.ShowConfiguration2(exact);
        document.EditRebuild3();
        return RequireActive(document, exact);
    });

    public ExplodeResult CreateExplode(string id, IReadOnlyList<ExplodeStepSpec> steps) => Call(() =>
    {
        var document = Find(id);
        var assembly = AssemblyOf(document, id);
        var configurationName = ActiveConfigurationName(document);
        if (Strings(assembly.GetExplodedViewNames2(configurationName) as object).Length > 0)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"configuration {configurationName} already has an exploded view", "Call config_add to make another configuration for it");
        }

        var targets = steps.Select(step => (Step: step, Component: ComponentOf(id, document, step.Component))).ToList();
        document.ClearSelection2(true);
        if (!assembly.CreateExplodedView())
        {
            throw new DatumException(ErrorCode.SolidworksError, "SOLIDWORKS couldn't create an exploded view");
        }

        var configuration = (IConfiguration)document.GetActiveConfiguration();

        // CreateExplodedView adds a step holding every component, so controlled steps start from none.
        for (var i = configuration.GetNumberOfExplodeSteps() - 1; i >= 0; i--)
        {
            configuration.DeleteExplodeStep(((IExplodeStep)configuration.GetExplodeStep(i)).Name);
        }

        var made = new List<ExplodeStepInfo>();
        foreach (var (step, component) in targets)
        {
            document.ClearSelection2(true);
            SelectWithMark(document, component, ExplodeComponentMark, append: false);
            var added = configuration.AddExplodeStep2(
                Math.Abs(step.Distance) / Mm, AxisIndex(step.Axis), step.Distance < 0, 0, (int)swRotationAxisIndex_e.swRotationAxisIndex_Unknown, false, false, false, out var error) as IExplodeStep;
            if (added is null || error != (int)swCreateExplodeStepError_e.swCreateExplodeStepError_Successful)
            {
                throw new DatumException(
                    ErrorCode.SolidworksError,
                    $"SOLIDWORKS couldn't explode {step.Component}: {((swCreateExplodeStepError_e)error).ToString().Replace("swCreateExplodeStepError_", "", StringComparison.Ordinal)}");
            }

            made.Add(new ExplodeStepInfo(added.Name, component.Name2, added.ExplodeDistance * Mm));
        }

        document.ClearSelection2(true);
        var names = Strings(assembly.GetExplodedViewNames2(configurationName) as object);
        if (names.Length == 0)
        {
            throw new DatumException(ErrorCode.SolidworksError, $"configuration {configurationName} reports no exploded view after creating one");
        }

        return new ExplodeResult(names[0], configurationName, made);
    });

    public bool ShowExplode(string id, bool show) => Call(() =>
    {
        var document = Find(id);
        var assembly = AssemblyOf(document, id);
        var configurationName = ActiveConfigurationName(document);
        var names = Strings(assembly.GetExplodedViewNames2(configurationName) as object);
        if (names.Length == 0)
        {
            throw new DatumException(ErrorCode.NotFound, $"configuration {configurationName} has no exploded view", "Call explode_create first");
        }

        // ShowExploded2 fails with an empty name, so the real one comes from the configuration.
        assembly.ShowExploded2(show, names[0]);
        var exploded = document.Extension.IsExploded(out _);
        return exploded == show
            ? exploded
            : throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS didn't {(show ? "explode" : "collapse")} {names[0]}");
    });

    private static string ActiveConfigurationName(IModelDoc2 document) => (document.GetActiveConfiguration() as IConfiguration)?.Name ?? "";

    private static string[] ConfigurationNames(IModelDoc2 document) => Strings(document.GetConfigurationNames() as object);

    private static string RequireActive(IModelDoc2 document, string name)
    {
        var active = ActiveConfigurationName(document);
        return string.Equals(active, name, StringComparison.OrdinalIgnoreCase)
            ? active
            : throw new DatumException(ErrorCode.SolidworksError, $"configuration {active} is active, not {name}");
    }

    private static int AxisIndex(string axis) => axis.Trim().ToUpperInvariant() switch
    {
        "X" => (int)swExplodeDirectionIndex_e.swExplodeDirectionIndex_XAxis,
        "Y" => (int)swExplodeDirectionIndex_e.swExplodeDirectionIndex_YAxis,
        _ => (int)swExplodeDirectionIndex_e.swExplodeDirectionIndex_ZAxis,
    };
}
