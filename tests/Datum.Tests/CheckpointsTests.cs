using Datum.Core;

namespace Datum.Tests;

public class CheckpointsTests
{
    [Fact]
    public void UndoesStepsNewestFirstBackToTheCheckpoint()
    {
        var checkpoints = new Checkpoints();
        checkpoints.Record("d1", "before Sketch1", new DeleteCreated(["feat#5"]));
        var start = checkpoints.Mark("d1", "start");
        var fillet = checkpoints.Record("d1", "before Fillet1", new DeleteCreated(["feat#7"]));
        checkpoints.Record("d1", "before setting D1@Boss-Extrude1 to 20", new RestoreDimension("D1@Boss-Extrude1", 10));

        Assert.Equal(["before setting D1@Boss-Extrude1 to 20", "before Fillet1"], checkpoints.StepsToUndo("d1", start).Select(s => s.Label));
        Assert.Equal(["before setting D1@Boss-Extrude1 to 20", "before Fillet1"], checkpoints.StepsToUndo("d1", fillet).Select(s => s.Label));
    }

    [Fact]
    public void ListsNewestFirst()
    {
        var checkpoints = new Checkpoints();
        checkpoints.Record("d1", "before Sketch1", new DeleteCreated(["feat#5"]));
        checkpoints.Mark("d1", "start");

        Assert.Equal(["start", "before Sketch1"], checkpoints.List("d1").Select(e => e.Label));
        Assert.Empty(checkpoints.List("d2"));
    }

    [Fact]
    public void ADeleteBlocksRestoringPastIt()
    {
        var checkpoints = new Checkpoints();
        var start = checkpoints.Mark("d1", "start");
        checkpoints.Record("d1", "deleted feat#9", new NoReversal("deleted features"));
        var after = checkpoints.Record("d1", "before Fillet1", new DeleteCreated(["feat#11"]));

        var error = Assert.Throws<DatumException>(() => checkpoints.StepsToUndo("d1", start));

        Assert.Equal(ErrorCode.CannotRestore, error.Code);
        Assert.Contains("'deleted feat#9' happened after", error.Message, StringComparison.Ordinal);
        Assert.Single(checkpoints.StepsToUndo("d1", after));
    }

    [Fact]
    public void TruncatingKeepsANamedCheckpointButDropsAnImplicitOne()
    {
        var checkpoints = new Checkpoints();
        var start = checkpoints.Mark("d1", "start");
        var fillet = checkpoints.Record("d1", "before Fillet1", new DeleteCreated(["feat#7"]));
        checkpoints.Record("d1", "before Cut-Extrude1", new DeleteCreated(["feat#8", "feat#9"]));

        checkpoints.Truncate("d1", fillet);
        Assert.Equal(["start"], checkpoints.List("d1").Select(e => e.Label));

        checkpoints.Truncate("d1", start);
        Assert.Equal(["start"], checkpoints.List("d1").Select(e => e.Label));
    }

    [Fact]
    public void UnknownCheckpointsAndForgottenDocumentsAreNotFound()
    {
        var checkpoints = new Checkpoints();
        var start = checkpoints.Mark("d1", "start");
        checkpoints.Forget("d1");

        Assert.Equal(ErrorCode.NotFound, Assert.Throws<DatumException>(() => checkpoints.StepsToUndo("d1", start)).Code);
    }
}
