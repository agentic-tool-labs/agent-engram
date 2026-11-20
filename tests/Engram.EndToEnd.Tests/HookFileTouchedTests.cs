using System.Diagnostics;
using System.Globalization;

namespace Engram.EndToEnd.Tests;

[CollectionDefinition("Hook execution")]
public class HookExecutionCollection
{
}

[Collection("Hook execution")]
public class HookFileTouchedTests
{
    [Fact]
    public void FileTouched_ExitsZero_FiveRunsYieldFiveSpoolFiles()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        for (var i = 0; i < 5; i++)
        {
            var (exitCode, _, stderr) = EngramProcess.Run(home.Root, "hook", "file-touched");
            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr);
        }

        var queueDir = Path.Combine(home.Root, "queue");
        var spoolFiles = Directory.GetFiles(queueDir);
        Assert.Equal(5, spoolFiles.Length);
    }

    [Fact(Timeout = 300_000)]
    public async Task FileTouched_FiftyConcurrentProcesses_ProduceFiftyDistinctSpoolFiles_EachWithAParseableTimestamp()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var runs = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => EngramProcess.Run(home.Root, "hook", "file-touched")));
        var results = await Task.WhenAll(runs);

        Assert.All(results, r => Assert.Equal(0, r.ExitCode));

        var queueDir = Path.Combine(home.Root, "queue");
        var spoolFiles = Directory.GetFiles(queueDir);
        Assert.Equal(50, spoolFiles.Length);

        foreach (var file in spoolFiles)
        {
            // First line, not the whole file: an entry carries an optional path on the second.
            var content = File.ReadLines(file).FirstOrDefault()?.Trim() ?? string.Empty;
            Assert.True(
                DateTime.TryParse(content, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
                $"spool file '{file}' did not contain a parseable ISO-8601 timestamp: '{content}'");
        }
    }

    /// <summary>
    /// The hook reads its payload for the one field a drain cannot do without.
    /// </summary>
    /// <remarks>
    /// Tested through the published binary because the payload arrives on stdin, and whether a
    /// process reads stdin is not a property any in-process call can exercise honestly —
    /// <c>Console.IsInputRedirected</c> is false in a test host and the hook would take its
    /// no-payload branch every time, passing while recording nothing.
    /// </remarks>
    [Fact]
    public void FileTouched_RecordsTheEditedPathFromTheHookPayload()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        const string edited = "/Users/someone/project/src/Widget.cs";
        var payload =
            """{"session_id":"s1","tool_name":"Edit","tool_input":{"file_path":"""
            + "\"" + edited + "\"}}";

        var (exitCode, _, stderr) = EngramProcess.RunWithStdin(
            home.Root, payload, "hook", "file-touched");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        var spool = Assert.Single(Directory.GetFiles(Path.Combine(home.Root, "queue")));
        var lines = File.ReadAllLines(spool);

        Assert.True(
            DateTime.TryParse(lines[0].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            $"first line was not a timestamp: '{lines[0]}'");
        Assert.Equal(edited, lines[1].Trim());
    }

    /// <summary>
    /// A payload with no <c>file_path</c> still spools, and still exits zero.
    /// </summary>
    /// <remarks>
    /// The hook must never fail on a payload shape it did not expect. Its budget is protected by
    /// swallowing errors, so a throw here would not surface as a broken hook — it would surface
    /// as an edit that silently never got queued.
    /// </remarks>
    [Fact]
    public void FileTouched_WithAPayloadCarryingNoPath_StillRecordsTheEdit()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, _, _) = EngramProcess.RunWithStdin(
            home.Root, """{"session_id":"s1","tool_name":"Bash"}""", "hook", "file-touched");

        Assert.Equal(0, exitCode);

        var spool = Assert.Single(Directory.GetFiles(Path.Combine(home.Root, "queue")));
        var lines = File.ReadAllLines(spool);

        Assert.Single(lines);
        Assert.True(
            DateTime.TryParse(lines[0].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            $"first line was not a timestamp: '{lines[0]}'");
    }

    [Fact]
    public void FileTouched_AddsUnder50MillisecondsOverANoOp()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        const int totalRuns = 21;
        var hookMs = new List<double>(totalRuns);
        var noOpMs = new List<double>(totalRuns);

        // Relative to starting the binary to do nothing, never absolute (D4, D101). The absolute
        // form failed in QA with medians of 110–300 ms while its own fastest run was 36–117 ms —
        // four to twelve times the ~10 ms idle floor, so what it measured was the machine. Each
        // pair alternates which arm goes first, because running the same arm first every time
        // charges it whatever the first of a pair costs (D56).
        for (var i = 0; i < totalRuns; i++)
        {
            if (i % 2 == 0)
            {
                hookMs.Add(TimeHook(home.Root));
                noOpMs.Add(Time(home.Root, "home"));
            }
            else
            {
                noOpMs.Add(Time(home.Root, "home"));
                hookMs.Add(TimeHook(home.Root));
            }
        }

        var queueDir = Path.Combine(home.Root, "queue");
        var spoolFiles = Directory.GetFiles(queueDir);
        Assert.Equal(totalRuns, spoolFiles.Length);

        var hookMedian = Median(hookMs.Skip(1));
        var noOpMedian = Median(noOpMs.Skip(1));
        var margin = hookMedian - noOpMedian;

        Assert.True(margin < 50,
            $"file-touched median {hookMedian:0.0} ms against a no-op median of {noOpMedian:0.0} ms "
                + $"across {totalRuns - 1} interleaved pairs: {margin:0.0} ms over starting the binary, expected < 50 ms");
    }

    private static double TimeHook(string home)
    {
        var stopwatch = Stopwatch.StartNew();
        var (exitCode, _, _) = EngramProcess.Run(home, "hook", "file-touched");
        stopwatch.Stop();

        Assert.Equal(0, exitCode);
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static double Time(string home, params string[] args)
    {
        var stopwatch = Stopwatch.StartNew();
        EngramProcess.Run(home, args);
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static double Median(IEnumerable<double> samples)
    {
        var sorted = samples.Order().ToList();
        return sorted[sorted.Count / 2];
    }
}
