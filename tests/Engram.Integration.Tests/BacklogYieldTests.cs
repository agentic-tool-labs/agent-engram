using System.Diagnostics;
using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>The embedding backlog steps aside for foreground requests, and never for longer than its cap.</summary>
public class BacklogYieldTests
{
    private sealed class ObservingEmbedder(Action onEmbed) : IEmbedder
    {
        public EmbeddingSpace Space { get; } = new("test-embedder", 4);

        public Task<IReadOnlyList<float[]?>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            onEmbed();
            return Task.FromResult<IReadOnlyList<float[]?>>([.. texts.Select(_ => (float[]?)[1f, 0f, 0f, 0f])]);
        }
    }

    /// <summary>The slowest single batch measured on a real backfill (CLAUDE.md, D54).</summary>
    private static readonly TimeSpan SlowestMeasuredBatch = TimeSpan.FromSeconds(28);

    /// <summary>
    /// A request left open past the cap delays the batch by the cap and then lets it through, so a
    /// session abandoned mid-call slows vector production instead of stopping it — and for the
    /// whole of the wait the loop's note still reads as alive.
    /// </summary>
    [Fact]
    public async Task AForegroundRequestHeldPastTheCap_DelaysTheBatchByTheCap_ThenItProceeds_NeverReadingStalled()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(2);

        var foreground = new ForegroundWork();
        var release = new TaskCompletionSource<int>();
        var held = foreground.RunAsync(() => release.Task);

        var clock = Stopwatch.StartNew();
        var embedded = new TaskCompletionSource<(TimeSpan At, int Active)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var embedder = new ObservingEmbedder(() => embedded.TrySetResult((clock.Elapsed, foreground.Active)));
        var backlog = new EmbeddingBacklog(
            sandbox.Home, embedder, EmbeddingSettings.Disabled with { MaxBatch = 16 }, log: null, foreground);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var loop = backlog.RunAsync(stop.Token);

        // Sampled while the batch is yielding, which is the only time the loop publishes nothing.
        var samples = 0;
        while (!embedded.Task.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(90))
        {
            if (EmbeddingProgress.Read(sandbox.Home) is { } note)
            {
                Assert.True(note.LooksLive(DateTimeOffset.UtcNow), $"the note read stalled {clock.Elapsed} into the yield");
                samples++;
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        Assert.True(embedded.Task.IsCompleted, "the batch never went ahead");
        var (at, active) = await embedded.Task;
        Assert.Equal(1, active);
        Assert.True(at >= EmbeddingBacklog.ForegroundYieldCap, $"the batch went ahead after {at}");
        Assert.True(samples > 0, "the note was never sampled during the yield");

        await stop.CancelAsync();
        await loop;
        release.SetResult(0);
        await held;
    }

    /// <summary>
    /// The longest a working loop goes without publishing is the busy interval between passes, a
    /// full yield, and then one batch embed before its commit publishes. That must still read as
    /// alive, or a backlog stepping aside would be reported as stalled.
    /// </summary>
    [Fact]
    public void TheLongestSilence_AYieldPlusTheSlowestBatch_StaysInsideTheStalenessWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var lastReport = now - EmbeddingBacklog.BusyInterval - EmbeddingBacklog.ForegroundYieldCap - SlowestMeasuredBatch;
        var note = new EmbeddingProgress(lastReport, lastReport, 1, "test-embedder", 0, 0, "running", null, []);

        Assert.True(note.LooksLive(now));
    }

    /// <summary>A tail or a band polling every two seconds must never hold the backlog paused.</summary>
    [Theory]
    [InlineData("tail")]
    [InlineData("embed-status")]
    [InlineData("fact")]
    [InlineData("history")]
    [InlineData("captures")]
    [InlineData("path-facts")]
    public async Task ALookup_IsNotForegroundWork(string op)
    {
        var foreground = new ForegroundWork();
        var activeDuring = -1;

        await ServeCommand.RunModOp(foreground, op, () =>
        {
            activeDuring = foreground.Active;
            return new ModApiResult(200, "{}");
        });

        Assert.Equal(0, activeDuring);
    }

    [Theory]
    [InlineData("recall")]
    [InlineData("remember")]
    [InlineData("forget")]
    public async Task AnOpThatActsForTheModel_IsForegroundWork_UntilItReturns(string op)
    {
        var foreground = new ForegroundWork();
        var activeDuring = -1;

        await ServeCommand.RunModOp(foreground, op, () =>
        {
            activeDuring = foreground.Active;
            return new ModApiResult(200, "{}");
        });

        Assert.Equal(1, activeDuring);
        Assert.Equal(0, foreground.Active);
    }
}
