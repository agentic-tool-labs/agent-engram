using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Engram.Cli;
using Engram.Core;
using Engram.EndToEnd.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Engram.Integration.Tests;

/// <summary>
/// An MCP tool call is foreground work for exactly as long as it runs. Driven through the real
/// server in this process, so the registration that enters the count is what is under test, not a
/// copy of it.
/// </summary>
public class McpForegroundTests
{
    [Fact]
    public async Task AToolCall_CountsAsForeground_WhileItRuns_AndNotAfter()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sandbox = new SandboxHome();
        var port = FreePort();

        var ready = new TaskCompletionSource<IServiceProvider>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Factory.StartNew(
            () => ServeCommand.Run(
                sandbox.Home.Root,
                ["--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                TextWriter.Null,
                TextWriter.Null,
                services => ready.TrySetResult(services)),
            ct,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        var services = await ready.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
        var foreground = services.GetRequiredService<ForegroundWork>();
        try
        {
            using var client = new HttpMcpClient(port);
            await client.InitializeAsync(ct);
            Assert.Equal(0, foreground.Active);

            // Holding the writer slot makes engram_remember block inside the tool, on its write,
            // which is where the count has to be held. busy_timeout is 5 s, so the slot is let go
            // as soon as the count is seen.
            Task<string> call;
            using (var connection = EngramDatabase.Open(sandbox.Home))
            using (EngramDatabase.BeginWrite(connection))
            {
                call = client.CallToolTextAsync(
                    "engram_remember", new JsonObject { ["statement"] = "the embedding backlog yields to tool calls" }, ct);

                Assert.True(
                    await Eventually(() => foreground.Active == 1, TimeSpan.FromSeconds(4), ct),
                    "a tool call blocked inside the tool was never counted as foreground");
            }

            await call.WaitAsync(TimeSpan.FromSeconds(30), ct);
            Assert.Equal(0, foreground.Active);
        }
        finally
        {
            services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            await server.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
    }

    private static async Task<bool> Eventually(Func<bool> condition, TimeSpan within, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > within)
            {
                return false;
            }

            await Task.Delay(10, ct);
        }

        return true;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
