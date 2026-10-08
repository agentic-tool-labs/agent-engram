using System.Text.RegularExpressions;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// <c>engram embed --status</c> through the published binary.
/// </summary>
/// <remarks>
/// Tier 3 because the thing being checked spans two processes: the server decides whether to run an
/// embedding loop, and a separate invocation of the CLI has to be able to say what it decided. A
/// test with both halves in one process would assert on a note it wrote itself.
/// </remarks>
public class EmbedStatusCommandTests
{
    /// <summary>
    /// The defect this was written for. A server was up, the backlog had declined to start because
    /// the model was not downloaded, and status answered "not running — start the server with
    /// `engram start`": advice to do the thing that had already been done, while the only process
    /// that knew the real reason had written it to a log nobody asking this question opens.
    /// </summary>
    [Fact]
    public void Status_ServerRunningButTheModelIsMissing_NamesTheReason()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        UseUndownloadedLocalModel(home.Root);

        var port = FreeTcpPort.Next();
        var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"start failed: {startErr}");

        try
        {
            var note = Path.Combine(home.Root, "embedding.json");
            for (var attempt = 0; attempt < 50 && !File.Exists(note); attempt++)
            {
                Thread.Sleep(100);
            }

            var (exit, output, _) = EngramProcess.Run(home.Root, "embed", "--status");

            Assert.Equal(0, exit);
            Assert.Contains("all-minilm-l6-v2", output, StringComparison.Ordinal);
            Assert.Contains("not downloaded", output, StringComparison.Ordinal);
            Assert.DoesNotContain("start the server", output, StringComparison.Ordinal);
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }
    }

    /// <summary>
    /// The note states what a particular server decided, so it may not outlive that server. A
    /// backlog that declined to start never reaches its own cleanup — the loop was never entered —
    /// so the shutdown hook has to do it, and this is the case that proves the hook is reached.
    /// </summary>
    [Fact]
    public void Status_AfterTheServerStops_HasNoNoteLeftBehind()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        UseUndownloadedLocalModel(home.Root);

        var port = FreeTcpPort.Next();
        var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"start failed: {startErr}");

        var note = Path.Combine(home.Root, "embedding.json");
        for (var attempt = 0; attempt < 50 && !File.Exists(note); attempt++)
        {
            Thread.Sleep(100);
        }

        Assert.True(File.Exists(note), "the server never recorded why the backlog did not start");

        var (stopExit, _, stopErr) = EngramProcess.Run(home.Root, "stop");
        Assert.True(stopExit == 0, $"stop failed: {stopErr}");

        for (var attempt = 0; attempt < 50 && File.Exists(note); attempt++)
        {
            Thread.Sleep(100);
        }

        Assert.False(File.Exists(note), "a stopped server left a note claiming to describe a backlog");

        var (_, output, _) = EngramProcess.Run(home.Root, "embed", "--status");
        Assert.DoesNotContain("not downloaded", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The AOT proof for the source-generated context: the published binary must print an object a
    /// parser accepts, and the text form must not have changed shape.
    /// </summary>
    [Fact]
    public void StatusJson_PublishedBinary_PrintsOneParseableObject()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exit, output, _) = EngramProcess.Run(home.Root, "embed", "--status", "--json");

        Assert.Equal(0, exit);
        using var doc = System.Text.Json.JsonDocument.Parse(output);
        var json = doc.RootElement;
        Assert.Equal("not-running", json.GetProperty("backlog").GetProperty("state").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.GetProperty("provider").ValueKind);
        Assert.Equal(0, json.GetProperty("total").GetInt32());

        var (_, text, _) = EngramProcess.Run(home.Root, "embed", "--status");
        Assert.False(text.TrimStart().StartsWith('{'), "the text form must stay text");
    }

    [Fact]
    public void StatusJson_ServerDeclinedToStart_ReportsUnavailableWithTheReason()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        UseUndownloadedLocalModel(home.Root);

        var port = FreeTcpPort.Next();
        var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"start failed: {startErr}");

        try
        {
            var note = Path.Combine(home.Root, "embedding.json");
            for (var attempt = 0; attempt < 50 && !File.Exists(note); attempt++)
            {
                Thread.Sleep(100);
            }

            var (exit, output, _) = EngramProcess.Run(home.Root, "embed", "--status", "--json");

            Assert.Equal(0, exit);
            using var doc = System.Text.Json.JsonDocument.Parse(output);
            var backlog = doc.RootElement.GetProperty("backlog");
            Assert.Equal("unavailable", backlog.GetProperty("state").GetString());
            Assert.Contains("not downloaded", backlog.GetProperty("reason").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }
    }

    [Fact]
    public void StatusJson_WithWatch_IsAUsageError()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exit, output, error) = EngramProcess.Run(home.Root, "embed", "--status", "--json", "--watch");

        Assert.Equal(2, exit);
        Assert.Equal(string.Empty, output.Trim());
        Assert.Contains("--watch", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_WithoutAMode_IsTheSameUsageErrorAsNoFlags()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exit, _, error) = EngramProcess.Run(home.Root, "embed", "--json");
        var (bareExit, _, bareError) = EngramProcess.Run(home.Root, "embed");

        Assert.Equal(2, exit);
        Assert.Equal(bareExit, exit);
        Assert.Equal(bareError, error);
    }

    /// <summary>
    /// Points the home at a real model that is definitely not on disk in a fresh test home, so the
    /// backlog declines for a stated reason rather than loading several hundred megabytes.
    /// </summary>
    private static void UseUndownloadedLocalModel(string root)
    {
        var path = Path.Combine(root, "config.toml");
        var config = File.ReadAllText(path);

        config = Regex.Replace(config, @"(?m)^provider\s*=.*$", "provider = \"local\"");
        config = Regex.Replace(config, @"(?m)^model_path\s*=.*$", "model_path = \"\"");
        config = Regex.Replace(config, @"(?m)^model\s*=.*$", "model = \"all-minilm-l6-v2\"");
        config = Regex.Replace(config, @"(?m)^dim\s*=.*$", "dim = 384");

        File.WriteAllText(path, config);
    }
}
