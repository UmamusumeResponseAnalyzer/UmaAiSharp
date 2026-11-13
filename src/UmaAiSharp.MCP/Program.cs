using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using UmaAiSharp.MCP;

var host = Host.CreateApplicationBuilder();
host.Logging.ClearProviders();
host.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

using var toolSlot = new SemaphoreSlim(1);
host.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, token) =>
    {
        await toolSlot.WaitAsync(token);
        try
        {
            if (context.MatchedPrimitive is McpServerTool tool && context.Params?.Arguments is { } arguments
                && tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties))
            {
                foreach (var name in arguments.Keys)
                    if (!properties.TryGetProperty(name, out _))
                        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = $"未知工具参数 {name}。" }] };
            }
            return await next(context, token);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException
            or JsonException or SqliteException or PlatformNotSupportedException)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = ToolDiagnostics.Describe(error) }] };
        }
        finally
        {
            toolSlot.Release();
        }
    }));

await host.Build().RunAsync();
