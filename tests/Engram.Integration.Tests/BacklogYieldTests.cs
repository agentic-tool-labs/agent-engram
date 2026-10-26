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

    /// <summary>
    /// A request left open past the cap delays the batch by the cap and then lets it through, so a
    /// session abandoned mid-call slows vector production instead of stopping it.
    /// </summary>
    [Fact]
    public async Task AForegroundRequestHeldPastTheCap_DelaysTheBatchByTheCap_ThenItProceeds()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(2);

        var foreground = new ForegroundWork();
        var release = new TaskCompletionSource<int>();
        var held = foreground.RunAsync(() => release.Task);

        var clock = Stopwatch.StartNew();
        TimeSpan? embeddedAt = null;
        var activeWhenEmbedded = -1;
        var embedder = new ObservingEmbedder(() =>
        {
            embeddedAt ??= clock.Elapsed;
            activeWhenEmbedded = foreground.Active;
        });
        var backlog = new EmbeddingBacklog(
            sandbox.Home, embedder, EmbeddingSettings.Disabled with { MaxBatch = 16 }, log: null, foreground);

        var result = await backlog.DrainOnceAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Embedded);
        Assert.Equal(1, activeWhenEmbedded);
        Assert.True(embeddedAt >= EmbeddingBacklog.ForegroundYieldCap, $"the batch went ahead after {embeddedAt}");

        var note = EmbeddingProgress.Read(sandbox.Home);
        Assert.NotNull(note);
        Assert.True(note.LooksLive(DateTimeOffset.UtcNow), "the note read stalled straight after a yielded batch");

        release.SetResult(0);
        await held;
    }

    /// <summary>
    /// A loop that yields a full cap, then waits its busy interval, has still reported in recently
    /// enough to be believed.
    /// </summary>
    [Fact]
    public void AYieldOfTheWholeCap_StaysInsideTheStalenessWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var lastReport = now - EmbeddingBacklog.ForegroundYieldCap - EmbeddingBacklog.BusyInterval - TimeSpan.FromSeconds(5);
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
