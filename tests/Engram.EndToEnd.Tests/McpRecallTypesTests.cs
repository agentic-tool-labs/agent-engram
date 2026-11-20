using System.Text.Json.Nodes;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// Tier 3 (D9): <c>engram_recall</c>'s <c>types</c> array (D85) binds through the published binary's
/// MCP marshaller. The JIT tool tests call <c>EngramMcpTools.Recall</c> directly and so cannot see
/// an array argument the AOT server fails to deserialize.
/// </summary>
public class McpRecallTypesTests
{
    [Fact]
    public async Task TypesArray_BindsAndFilters_AndAnUnknownTypeIsRefusedByName()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var port = FreeTcpPort.Next();
        var cancellationToken = TestContext.Current.CancellationToken;

        var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"engram start failed: {startErr}");

        try
        {
            using var client = new HttpMcpClient(port);
            await client.InitializeAsync(cancellationToken);

            var filtered = await client.CallToolTextAsync(
                "engram_recall",
                new JsonObject { ["query"] = "anything", ["types"] = new JsonArray("code", "user") },
                cancellationToken);
            Assert.Contains(" · types: user,code", filtered, StringComparison.Ordinal);

            var refused = await client.CallToolTextAsync(
                "engram_recall",
                new JsonObject { ["query"] = "anything", ["types"] = new JsonArray("bogus") },
                cancellationToken);
            Assert.StartsWith("unknown types: bogus; valid types are", refused, StringComparison.Ordinal);
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }
    }
}
