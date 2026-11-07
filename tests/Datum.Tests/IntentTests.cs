using System.Text.Json;
using Datum.Core;

namespace Datum.Tests;

public sealed class IntentTests
{
    private static Dictionary<string, JsonElement> Args(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public async Task AReplaySwapsOldFeatureHandlesForNewOnes()
    {
        var log = new IntentLog();
        log.Add("sketch_end", Args("{}"), "Sketch1 is feat#7");
        log.Add("feature_extrude", Args("""{"sketch":"feat#7","depth":10}"""), "Boss is feat#8");
        var calls = new List<string>();

        await IntentLog.ReplayAsync(IntentLog.Import(log.Export()), (tool, args) =>
        {
            calls.Add($"{tool} {JsonSerializer.Serialize(args)}");
            return Task.FromResult(tool == "sketch_end" ? "Sketch1 is feat#99" : "ok");
        });

        Assert.Equal("""feature_extrude {"sketch":"feat#99","depth":10}""", calls[1]);
    }

    [Fact]
    public async Task AFaceHandleStopsAReplayAndSaysWhy()
    {
        var log = new IntentLog();
        log.Add("sketch_begin", Args("""{"on":"face#12"}"""), "");

        var refused = await Assert.ThrowsAsync<DatumException>(() => IntentLog.ReplayAsync(log.Steps, (_, _) => Task.FromResult("")));

        Assert.Contains("face#12", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("feature_extrude", true)]
    [InlineData("doc_save", false)]
    [InlineData("model_info", false)]
    public void OnlyModellingStepsAreRecorded(string tool, bool recorded)
    {
        Assert.Equal(recorded, IntentLog.Records(tool));
    }

    [Fact]
    public void GatewayCallsAreTaggedAsSolidWorksOnly()
    {
        var log = new IntentLog();
        log.Add("invoke", Args("{}"), "");

        Assert.Equal("solidworks", log.Steps[0].Tag);
    }
}
