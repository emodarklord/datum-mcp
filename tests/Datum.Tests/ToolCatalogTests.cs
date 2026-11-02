using System.ComponentModel;
using Datum.Core;
using Datum.Mcp;
using ModelContextProtocol.Server;

namespace Datum.Tests;

public static class CatalogTestTools
{
    [McpServerTool(Name = "core_tool")]
    [Description("Always listed")]
    public static string CoreTool() => "core";

    [Toolset("extra")]
    [McpServerTool(Name = "extra_tool")]
    [Description("Listed once the extra toolset is enabled")]
    public static string ExtraTool() => "extra";

    [Toolset("extra")]
    [RequiresSolidWorks(2027)]
    [McpServerTool(Name = "future_tool")]
    [Description("Needs a SOLIDWORKS release that does not exist yet")]
    public static string FutureTool() => "future";

    [RequiresAddIn("219180b0-7183-4fe2-b167-4e2bfe534004", "3DEXPERIENCE Marketplace")]
    [McpServerTool(Name = "addin_tool")]
    [Description("Core tool that needs an add-in loaded")]
    public static string AddInTool() => "addin";
}

public class ToolCatalogTests
{
    private static readonly Guid Marketplace = Guid.Parse("219180b0-7183-4fe2-b167-4e2bfe534004");

    private static readonly SolidWorksCapabilities Student2026WithMarketplace = new(2026, LicenseType.Student, new HashSet<Guid> { Marketplace });

    [Fact]
    public void ListsOnlyCoreToolsWithoutRequirementsBeforeSolidWorksConnects()
    {
        var (_, listed) = Build();

        Assert.Equal(["core_tool"], Names(listed));
    }

    [Fact]
    public void ShowsToolsWhoseRequirementsTheConnectedSolidWorksMeets()
    {
        var (catalog, listed) = Build();

        catalog.Update(Student2026WithMarketplace);

        Assert.Equal(["addin_tool", "core_tool"], Names(listed));
    }

    [Fact]
    public void HidesRequirementToolsAgainWhenSolidWorksGoesAway()
    {
        var (catalog, listed) = Build();
        catalog.Update(Student2026WithMarketplace);

        catalog.Update(null);

        Assert.Equal(["core_tool"], Names(listed));
    }

    [Fact]
    public void EnablingAToolsetListsItsUsableTools()
    {
        var (catalog, listed) = Build();
        catalog.Update(Student2026WithMarketplace);

        var entries = catalog.Enable("EXTRA");

        Assert.Equal(["addin_tool", "core_tool", "extra_tool"], Names(listed));
        var future = Assert.Single(entries, e => e.Name == "future_tool");
        Assert.True(future.Unavailable);
        Assert.Equal("unavailable: needs SOLIDWORKS 2027 or later (running 2026)", future.Status);
    }

    [Fact]
    public void RejectsUnknownToolsets()
    {
        var (catalog, _) = Build();

        var error = Assert.Throws<DatumException>(() => catalog.Enable("nope"));

        Assert.Equal(ErrorCode.InvalidArgument, error.Code);
        Assert.Equal("known toolsets: extra", error.Hint);
    }

    [Fact]
    public void AllToolsModeListsEverythingAndNeverChanges()
    {
        var (catalog, listed) = Build(allTools: true);
        var changes = 0;
        listed.Changed += (_, _) => changes++;

        catalog.Update(Student2026WithMarketplace);
        catalog.Update(null);

        Assert.Equal(["addin_tool", "core_tool", "extra_tool", "future_tool"], Names(listed));
        Assert.Equal(0, changes);
    }

    [Fact]
    public void RaisesOneChangePerUpdate()
    {
        var (catalog, listed) = Build();
        var changes = 0;
        listed.Changed += (_, _) => changes++;

        catalog.Enable("extra");

        Assert.Equal(1, changes);
    }

    [Fact]
    public void DescribesEveryToolWithItsStatus()
    {
        var (catalog, _) = Build();

        var entries = catalog.Entries().ToDictionary(e => e.Name, e => e.Status);

        Assert.Equal("listed", entries["core_tool"]);
        Assert.Equal("enable toolset 'extra'", entries["extra_tool"]);
        Assert.Equal("unavailable: SOLIDWORKS isn't connected yet; call sw_status first", entries["addin_tool"]);
        Assert.Equal(["core", "extra"], catalog.Toolsets);
    }

    private static (ToolCatalog Catalog, McpServerPrimitiveCollection<McpServerTool> Listed) Build(bool allTools = false)
    {
        var listed = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var method in typeof(CatalogTestTools).GetMethods().Where(m => m.IsDefined(typeof(McpServerToolAttribute), false)))
        {
            listed.Add(McpServerTool.Create(method, (object?)null));
        }

        var catalog = new ToolCatalog(allTools);
        catalog.Attach(listed);
        return (catalog, listed);
    }

    private static string[] Names(McpServerPrimitiveCollection<McpServerTool> listed) =>
        [.. listed.Select(t => t.ProtocolTool.Name).Order(StringComparer.Ordinal)];
}
