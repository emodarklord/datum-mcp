using Datum.Core;

namespace Datum.Tests;

public class GatewayTests
{
    [Theory]
    [InlineData("3 mm", 0.003)]
    [InlineData("3mm", 0.003)]
    [InlineData("2.5 CM", 0.025)]
    [InlineData("-1e2 mm", -0.1)]
    [InlineData("1 in", 0.0254)]
    [InlineData("2 ft", 0.6096)]
    [InlineData("0.5 m", 0.5)]
    [InlineData("180 deg", Math.PI)]
    [InlineData("90°", Math.PI / 2)]
    [InlineData("1 rad", 1)]
    public void ConvertsUnitsToSolidworksUnits(string text, double expected)
    {
        Assert.True(Gateway.TryParseUnit(text, out var value));
        Assert.Equal(expected, value, precision: 9);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("mm")]
    [InlineData("3 parsecs")]
    [InlineData("three mm")]
    [InlineData("feat#3")]
    public void RefusesTextThatIsNotAValueWithAUnit(string text) => Assert.False(Gateway.TryParseUnit(text, out _));

    [Theory]
    [InlineData(ApiMemberKind.Property, "Extension", false, true)]
    [InlineData(ApiMemberKind.Property, "Name", true, false)]
    [InlineData(ApiMemberKind.Method, "GetTypeName2", false, true)]
    [InlineData(ApiMemberKind.Method, "IGetBodies", false, true)]
    [InlineData(ApiMemberKind.Method, "IsSuppressed2", false, true)]
    [InlineData(ApiMemberKind.Method, "HasExtrusion", false, true)]
    [InlineData(ApiMemberKind.Method, "ModifyDefinition", false, false)]
    [InlineData(ApiMemberKind.Method, "Getaway", false, false)]
    [InlineData(ApiMemberKind.Method, "AccessSelections", false, false)]
    public void SplitsReadsFromWrites(ApiMemberKind kind, string member, bool setting, bool read) =>
        Assert.Equal(read, Gateway.IsRead(kind, member, setting));

    [Theory]
    [InlineData("CloseDoc", "doc_close")]
    [InlineData("SaveAs3", "doc_save")]
    [InlineData("Save3", "doc_save")]
    [InlineData("ExportToDWG2", "doc_export")]
    [InlineData("OpenDoc6", "doc_open")]
    [InlineData("RunMacro2", "Macros")]
    [InlineData("SetUserPreferenceToggle", "journaled")]
    [InlineData("EditDelete", "feature_delete")]
    [InlineData("SendMsgToUser2", "message boxes")]
    public void RefusesSessionAndFileOperations(string member, string hint) =>
        Assert.Contains(hint, Gateway.DeniedHint(member), StringComparison.Ordinal);

    [Theory]
    [InlineData("GetTypeName2")]
    [InlineData("ModifyDefinition")]
    [InlineData("SaveBMPIcon2")]
    public void AllowsEverythingElse(string member) => Assert.Null(Gateway.DeniedHint(member));

    [Fact]
    public void DescribesMembersAsSignatures()
    {
        var method = new ApiMember("IModelDocExtension", "GetMassProperties2", ApiMemberKind.Method,
            [new("Accuracy", "int", false, false), new("Status", "int", true, true), new("UseSelected", "bool", false, false)], "object", true, false);
        var property = new ApiMember("ISimpleFilletFeatureData2", "DefaultRadius", ApiMemberKind.Property, [], "double", true, true);

        Assert.Equal("GetMassProperties2(int Accuracy, out int Status, bool UseSelected) → object", method.ToString());
        Assert.Equal("DefaultRadius { get; set; } double", property.ToString());
    }
}
