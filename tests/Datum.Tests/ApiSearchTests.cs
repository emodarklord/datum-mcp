using Datum.Core;

namespace Datum.Tests;

public sealed class ApiSearchTests
{
    private static readonly ApiEntry[] Entries =
    [
        new("IFeatureManager", "FeatureBossThicken", "Feature FeatureBossThicken(...)"),
        new("IFeatureManager", "FeatureBossThicken2", "Feature FeatureBossThicken2(...)"),
        new("IFeatureManager", "IFeatureBossThicken", "Feature IFeatureBossThicken(...)"),
        new("IFeatureManager", "InsertFeatureChamfer", "Feature InsertFeatureChamfer(...)"),
        new("ISurface", "Reverse", "void Reverse()"),
        new("IModelDoc2", "GetTitle", "String GetTitle()"),
    ];

    [Fact]
    public void AVerbInPlainEnglishFindsItsMember()
    {
        Assert.Equal("FeatureBossThicken2", ApiSearch.Rank("thicken surface", Entries, 3)[0].Member);
    }

    [Fact]
    public void OnlyTheNewestVersionIsShown()
    {
        Assert.Single(ApiSearch.Rank("thicken", Entries, 10));
    }

    [Fact]
    public void AnEverydayWordMapsToTheApiWord()
    {
        Assert.Equal("InsertFeatureChamfer", ApiSearch.Rank("bevel an edge", Entries, 3)[0].Member);
    }

    [Fact]
    public void NoMatchGivesNothing()
    {
        Assert.Empty(ApiSearch.Rank("xyzzy", Entries, 3));
    }

    [Fact]
    public void NamesSplitIntoWords()
    {
        Assert.Equal(["feature", "boss", "thicken"], ApiSearch.Words("FeatureBossThicken"));
    }
}
