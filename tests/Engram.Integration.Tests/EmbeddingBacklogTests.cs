using Engram.Core;

namespace Engram.Integration.Tests;

public class EmbeddingBacklogTests
{
    private const int Dimensions = 4;

    private sealed class CountingEmbedder(string model = "test-embedder") : IEmbedder
    {
        public EmbeddingSpace Space { get; } = new(model, Dimensions);

        public int Texts { get; private set; }
        public bool Fail { get; set; }
        public Action? OnEmbed { get; set; }

        public Task<IReadOnlyList<float[]?>> EmbedAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            Texts += texts.Count;
            OnEmbed?.Invoke();
            var vectors = new float[]?[texts.Count];
            for (var i = 0; i < texts.Count; i++)
            {
                vectors[i] = Fail ? null : [1f, 0f, 0f, 0f];
            }

            return Task.FromResult<IReadOnlyList<float[]?>>(vectors);
        }
    }

    private static EmbeddingSettings Settings(int maxBatch = 16) =>
        EmbeddingSettings.Disabled with { MaxBatch = maxBatch };

    [Fact]
    public async Task AnUnchangedEmptyPass_DoesNotTakeTheWriterLock()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(1);
        var backlog = new EmbeddingBacklog(sandbox.Home, new CountingEmbedder(), Settings());
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);

        // Another process can hold the writer slot while an unchanged poll reads its marker.
        // The old unconditional Reconcile waits for busy_timeout and throws here.
        using var writer = EngramDatabase.BeginWrite(sandbox.Connection);
        var result = await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BackfillOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.Embedded);
        Assert.Equal(0, result.Remaining);
    }

    /// <summary>
    /// The contract `remember` makes: the fact is durable immediately, the vector arrives later.
    /// The queue remains a query over `fact`; the revision can skip only an already verified
    /// empty state, so a first pass still discovers every durable fact.
    /// </summary>
    [Fact]
    public async Task AFactWrittenWithNoEmbedderRunning_IsPickedUpByTheNextPass()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(3);

        var embedder = new CountingEmbedder();
        var backlog = new EmbeddingBacklog(sandbox.Home, embedder, Settings());

        var result = await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BackfillOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.Embedded);
        Assert.Equal(3, embedder.Texts);
        Assert.Equal(0, VectorIndex.CountPending(sandbox.Connection));
    }

    [Fact]
    public async Task FactsWrittenBetweenPasses_LandOnTheFollowingOne()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFact("first", "written before the first pass");

        var embedder = new CountingEmbedder();
        var backlog = new EmbeddingBacklog(sandbox.Home, embedder, Settings());
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);

        sandbox.AddFact("second", "written after the first pass");
        Assert.Equal(1, VectorIndex.CountPending(sandbox.Connection));

        var second = await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, second.Embedded);
        Assert.Equal(2, VectorIndex.Count(sandbox.Connection, liveOnly: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingOrDeletingAFact_InvalidatesTheEmptyPass(bool delete)
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new VectorSandbox();
        var id = sandbox.AddFact("belief", "initial belief");
        var backlog = new EmbeddingBacklog(sandbox.Home, new CountingEmbedder(), Settings());
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);

        using var change = sandbox.Connection.CreateCommand();
        change.CommandText = delete ? "DELETE FROM fact WHERE id = $id;" : "UPDATE fact SET valid_to = 2 WHERE id = $id;";
        change.Parameters.AddWithValue("$id", id);
        change.ExecuteNonQuery();
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, VectorIndex.Count(sandbox.Connection, liveOnly: true));
        Assert.Equal(delete ? 0 : 1, VectorIndex.Count(sandbox.Connection));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearingOrDroppingTheIndex_InvalidatesTheEmptyPass(bool drop)
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(1);
        var backlog = new EmbeddingBacklog(sandbox.Home, new CountingEmbedder(), Settings());
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);

        if (drop) VectorIndex.Drop(sandbox.Connection);
        else VectorIndex.Clear(sandbox.Connection);

        var result = await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, result.Embedded);
        Assert.Equal(0, VectorIndex.CountPending(sandbox.Connection));
    }

    [Theory]
    [InlineData(VectorIndex.ModelKey, "different-model")]
    [InlineData(VectorIndex.DimensionsKey, "8")]
    [InlineData(VectorIndex.InputKey, "different-input")]
    public async Task ChangedSpaceOrInput_IsNotHiddenByTheEmptyPass(string key, string value)
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new VectorSandbox();
        var backlog = new EmbeddingBacklog(sandbox.Home, new CountingEmbedder(), Settings());
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
        EngramDatabase.WriteMeta(sandbox.Connection, null, key, value);

        var result = await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(BackfillOutcome.SpaceMismatch, result.Outcome);
    }

    [Theory]
    [InlineData("DELETE FROM schema_meta WHERE key = 'embedding_revision';")]
    [InlineData("DROP TRIGGER fact_embedding_insert;")]
    public async Task MissingRevisionMachinery_FallsBackToFullPasses(string remove)
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(1);
        var backlog = new EmbeddingBacklog(sandbox.Home, new CountingEmbedder(), Settings());
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
        using var command = sandbox.Connection.CreateCommand();
        command.CommandText = remove;
        command.ExecuteNonQuery();
        for (var pass = 0; pass < 2; pass++)
        {
            // An unmarked derived-state deletion must still be found while the marker is unavailable.
            command.CommandText = "DELETE FROM fact_vec;";
            command.ExecuteNonQuery();
            var result = await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, result.Embedded);
            await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AClosureDuringEmbedding_IsReconciledByTheNextPass()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new VectorSandbox();
        var first = sandbox.AddFact("first", "first belief");
        var embedder = new CountingEmbedder();
        var backlog = new EmbeddingBacklog(sandbox.Home, embedder, Settings());
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
        sandbox.AddFact("second", "second belief");
        embedder.OnEmbed = () => sandbox.Close(first);

        var result = await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(BackfillOutcome.Completed, result.Outcome);
        Assert.Equal(2, VectorIndex.Count(sandbox.Connection, liveOnly: true));
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, VectorIndex.Count(sandbox.Connection, liveOnly: true));
    }

    [Fact]
    public async Task AFailedPass_RetriesWithoutAnotherFactWrite()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(1);
        var embedder = new CountingEmbedder { Fail = true };
        var backlog = new EmbeddingBacklog(sandbox.Home, embedder, Settings());
        var failed = await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(BackfillOutcome.StalledOnFailures, failed.Outcome);
        embedder.Fail = false;
        var retried = await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, retried.Embedded);
    }

    [Fact]
    public async Task ReplacingTheDatabase_IsNotHiddenByAPooledHandle()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new SandboxHome(initialize: false);
        VectorExtensionFile.InstallInto(sandbox.Home);
        using (var setup = EngramDatabase.Open(sandbox.Home, pooling: false))
        {
            EngramDatabase.EnsureSchema(setup);
        }
        var backlog = new EmbeddingBacklog(sandbox.Home, new CountingEmbedder(), Settings());
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);

        using var replacement = new VectorSandbox();
        replacement.AddFacts(1);
        replacement.Connection.Dispose();
        EngramDatabase.ReleasePooledConnections(replacement.Home.DatabasePath);
        File.Move(sandbox.Home.DatabasePath, sandbox.Home.DatabasePath + ".old");
        File.Copy(replacement.Home.DatabasePath, sandbox.Home.DatabasePath);

        var result = await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, result.Embedded);
    }

    [Fact]
    public async Task SkippedPass_StillPublishesProgressAndHonoursCancellation()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new VectorSandbox();
        var backlog = new EmbeddingBacklog(sandbox.Home, new CountingEmbedder(), Settings());
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);

        using var stopping = new CancellationTokenSource();
        var run = backlog.RunAsync(stopping.Token);
        var progress = EmbeddingProgress.Read(sandbox.Home);
        Assert.NotNull(progress);
        Assert.Equal("Completed", progress.Outcome);
        Assert.True(progress.LooksLive(DateTimeOffset.UtcNow));
        await stopping.CancelAsync();
        await run;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backlog.DrainOnceAsync(stopping.Token));
    }

    [Fact]
    public async Task ADrainRespectsTheConfiguredBatchSize()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(20);

        // maxBatches is 8 inside a pass, so a batch of 2 leaves work behind and the pass says so
        // rather than running to completion in one wakeup.
        var backlog = new EmbeddingBacklog(sandbox.Home, new CountingEmbedder(), Settings(maxBatch: 2));
        var result = await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BackfillOutcome.BatchLimitReached, result.Outcome);
        Assert.Equal(16, result.Embedded);
        Assert.Equal(4, result.Remaining);
    }

    [Fact]
    public async Task RunAsync_StopsWhenCancelledAndSaysSo()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(2);

        var lines = new List<string>();
        using var stopping = new CancellationTokenSource();
        var backlog = new EmbeddingBacklog(
            sandbox.Home, new CountingEmbedder(), Settings(), lines.Add);

        var run = backlog.RunAsync(stopping.Token);
        while (VectorIndex.CountPending(sandbox.Connection) > 0)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await stopping.CancelAsync();
        await run;

        Assert.Contains(lines, l => l.Contains("started", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Embedded 2", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("stopped", StringComparison.Ordinal));
    }

    /// <summary>
    /// A pass that throws must not take the server down with it — the queue is durable, so the
    /// work survives, and the loop's job is to keep running and say why once.
    /// </summary>
    [Fact]
    public async Task RunAsync_SurvivesAnEmbedderThatThrows()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(1);

        var lines = new List<string>();
        using var stopping = new CancellationTokenSource();
        var backlog = new EmbeddingBacklog(
            sandbox.Home, new ThrowingEmbedder(), Settings(), lines.Add);

        var run = backlog.RunAsync(stopping.Token);
        while (!lines.Any(l => l.Contains("failed", StringComparison.Ordinal)))
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await stopping.CancelAsync();
        await run;

        Assert.Contains(lines, l => l.Contains("Embedding pass failed", StringComparison.Ordinal));
        Assert.Equal(1, VectorIndex.CountPending(sandbox.Connection));
    }

    private sealed class ThrowingEmbedder : IEmbedder
    {
        public EmbeddingSpace Space { get; } = new("throwing", Dimensions);

        public Task<IReadOnlyList<float[]?>> EmbedAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the endpoint is on fire");
    }
}
