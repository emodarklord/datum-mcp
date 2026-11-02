using Datum.Core;
using ModelContextProtocol.Protocol;

namespace Datum.Mcp;

internal static class ToolErrors
{
    // Returned rather than thrown: the SDK prefixes thrown errors with its own text,
    // which would push the error code out of the first word the model reads.
    public static async Task<CallToolResult> Run(Func<Task<string>> body)
    {
        try
        {
            return Text(await body().ConfigureAwait(false), isError: false);
        }
        catch (DatumException e)
        {
            return Text(e.ToString(), isError: true);
        }
    }

    public static async Task<CallToolResult> Run(Func<Task<IList<ContentBlock>>> body)
    {
        try
        {
            return new() { Content = await body().ConfigureAwait(false), IsError = false };
        }
        catch (DatumException e)
        {
            return Text(e.ToString(), isError: true);
        }
    }

    private static CallToolResult Text(string text, bool isError) =>
        new() { Content = [new TextContentBlock { Text = text }], IsError = isError };
}
