using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// A 500-record revising import through the published binary: one process, one transaction. The
/// budget is wall time including process start, so a per-record transaction or a scan that grows
/// with the batch shows up as a failure rather than as a slower background job.
/// </summary>
public class ImportReviseTimingTests
{
    private const int Records = 500;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(1.5);

    [Fact]
    public void ImportRevise_FiveHundredRecords_UnderOnePointFiveSeconds()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var directory = Path.Combine(Path.GetTempPath(), "engram-e2e-revise-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            // The first third keeps its body, the second third changes, the last third is new.
            var third = Records / 3;
            var seed = Path.Combine(directory, "seed.jsonl");
            File.WriteAllLines(seed, Enumerable.Range(0, 2 * third).Select(i => Line(i, "before", 1_800_000_000)));
            var (seedCode, _, seedErr) = EngramProcess.Run(home.Root, "import", seed, "--apply");
            Assert.True(seedCode == 0, seedErr);

            var batch = Path.Combine(directory, "batch.jsonl");
            File.WriteAllLines(
                batch,
                Enumerable.Range(0, Records).Select(i => Line(i, i >= third && i < 2 * third ? "after" : "before", 1_800_000_100)));

            var timer = Stopwatch.StartNew();
            var (code, stdout, stderr) = EngramProcess.Run(home.Root, "import", batch, "--apply", "--revise");
            timer.Stop();

            Assert.True(code == 0, stderr);
            TestContext.Current.SendDiagnosticMessage($"import --apply --revise of {Records} records: {timer.Elapsed.TotalMilliseconds:F0} ms");
            Assert.Contains($"new={Records - 2 * third} revised={third} unchanged={third}", stdout, StringComparison.Ordinal);
            Assert.True(timer.Elapsed <= Budget, $"took {timer.Elapsed.TotalMilliseconds:F0} ms against {Budget.TotalMilliseconds:F0} ms");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Line(int index, string body, long validFrom) =>
        new JsonObject
        {
            ["subject"] = $"/ledger/key-{index}",
            ["predicate"] = "states",
            ["body"] = body,
            ["valid_from"] = validFrom,
            ["reason"] = "ledger verdict",
        }.ToJsonString();
}
