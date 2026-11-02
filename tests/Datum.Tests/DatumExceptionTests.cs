using Datum.Core;

namespace Datum.Tests;

public class DatumExceptionTests
{
    [Fact]
    public void FormatsCodeMessageAndHint()
    {
        var error = new DatumException(ErrorCode.DialogBlocking, "SOLIDWORKS is showing \"Save As\"", "Close the dialog in SOLIDWORKS, then retry");

        Assert.Equal("DIALOG_BLOCKING: SOLIDWORKS is showing \"Save As\". Hint: Close the dialog in SOLIDWORKS, then retry", error.ToString());
    }

    [Fact]
    public void OmitsMissingHint()
    {
        Assert.Equal("NOT_RUNNING: SOLIDWORKS is not running", new DatumException(ErrorCode.NotRunning, "SOLIDWORKS is not running").ToString());
    }

    [Theory]
    [InlineData(ErrorCode.NotRunning, "NOT_RUNNING")]
    [InlineData(ErrorCode.Unreachable, "UNREACHABLE")]
    [InlineData(ErrorCode.Timeout, "TIMEOUT")]
    [InlineData(ErrorCode.DialogBlocking, "DIALOG_BLOCKING")]
    [InlineData(ErrorCode.Busy, "BUSY")]
    [InlineData(ErrorCode.ComError, "COM_ERROR")]
    public void NamesCodesInScreamingSnakeCase(ErrorCode code, string name)
    {
        Assert.Equal(name, code.Name());
    }
}
