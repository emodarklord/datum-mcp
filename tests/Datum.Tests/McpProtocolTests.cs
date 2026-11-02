using System.IO.Pipelines;
using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Datum.Tests;

public sealed class McpProtocolTests : IAsyncLifetime, IDisposable
{
    private readonly Pipe _toServer = new();
    private readonly Pipe _toClient = new();
    private readonly FakeBackend _backend = new();
    private IHost? _host;
    private McpClient? _client;

    public async Task InitializeAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddDatum(() => _backend).WithStreamServerTransport(_toServer.Reader.AsStream(), _toClient.Writer.AsStream());
        _host = builder.Build();
        await _host.StartAsync();

        _client = await McpClient.CreateAsync(new StreamClientTransport(_toServer.Writer.AsStream(), _toClient.Reader.AsStream()));
    }

    public async Task DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    public void Dispose() => _backend.Dispose();

    [Fact]
    public async Task ListsSwStatusAsReadOnly()
    {
        var tools = await _client!.ListToolsAsync();

        var status = Assert.Single(tools, t => t.Name == "sw_status");
        Assert.True(status.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.Equal("datum", _client!.ServerInfo?.Name);
    }

    [Fact]
    public async Task CallsSwStatus()
    {
        var result = await _client!.CallToolAsync("sw_status");

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(DiagnoseReport.Format(_backend.Info), Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task ReturnsErrorsWithTheirCode()
    {
        _backend.ConnectFailure = ErrorCode.NotRunning;

        var result = await _client!.CallToolAsync("sw_status");

        Assert.True(result.IsError);
        Assert.StartsWith("NOT_RUNNING: ", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
    }
}
