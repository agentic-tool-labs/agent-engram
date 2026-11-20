using System.Text.Json;

namespace Engram.EndToEnd.Tests;

[Collection("Hook execution")]
public class TelemetryConcurrencyTests
{
    [Fact(Timeout = 300_000)]
    public async Task SessionStart_ThirtyConcurrentProcesses_WriteNoTornRecords()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        for (var round = 0; round < 5; round++)
        {
            using var home = new TestHome();

            var runs = Enumerable.Range(0, 30)
                .Select(_ => Task.Run(() => EngramProcess.Run(home.Root, "hook", "session-start")));
            var results = await Task.WhenAll(runs);

            Assert.All(results, r => Assert.Equal(0, r.ExitCode));

            var telemetryPath = Path.Combine(home.Root, "telemetry.jsonl");
            var lines = File.ReadAllLines(telemetryPath);

            // Every line must parse, including the maintenance child's — that half is about
            // DurableAppend not interleaving two writers into one corrupt record, and more
            // concurrent writers makes it a stronger check, not a weaker one.
            //
            // No count of records is asserted (D56, D101). A record that waits out its 500 ms
            // retry budget is dropped by design, and how often that happens depends on load: 0
            // lost of 600 idle and under 18× `yes`, yet 28 of 30 once inside an integration QA
            // run. The count was also a weak guard for the budget it stood in for: with the
            // budget planted at zero it lost 9 and 11 of 600, so `lost ≤ 2` per run would have
            // caught that plant in 3 of 8 runs. What the budget buys is guarded deterministically
            // by SessionStart_WaitsOutABriefCollision_RatherThanDroppingItsRecord below.
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
                document.RootElement.GetProperty("kind");
            }
        }
    }

    /// <summary>
    /// Session start's telemetry append waits out a collision shorter than its retry budget.
    /// </summary>
    /// <remarks>
    /// The collision is arranged rather than hoped for: this test holds <c>telemetry.jsonl</c>
    /// exclusively, the way a concurrent appender's <c>FileShare.None</c> does, for about 200 ms
    /// while the hook runs, then releases it. Inside the 500 ms budget the hook's retry gets the
    /// file and its record lands; with the budget at <see cref="TimeSpan.Zero"/> — the value
    /// <c>file-touched</c> passes on purpose — the first attempt collides and the record is
    /// dropped every time (D101). The hook has to reach its append before the release for this to
    /// prove anything, which is why the hold is far longer than a hook takes to start.
    /// </remarks>
    [Fact]
    public async Task SessionStart_WaitsOutABriefCollision_RatherThanDroppingItsRecord()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var telemetryPath = Path.Combine(home.Root, "telemetry.jsonl");

        Task<(int ExitCode, string Stdout, string Stderr)> hook;
        using (new FileStream(telemetryPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            hook = Task.Run(() => EngramProcess.Run(home.Root, "hook", "session-start"));
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        var (exitCode, _, _) = await hook;
        Assert.Equal(0, exitCode);

        var kinds = File.ReadAllLines(telemetryPath)
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.GetProperty("kind").GetString();
            });
        Assert.Contains("session-start", kinds);
    }
}
