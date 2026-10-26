using Engram.Core;

namespace Engram.Core.Tests;

/// <summary>
/// The backlog and recall share one local embedder, so a query embed arriving mid-batch must wait
/// for the text in flight and no longer. Ordering is observed through the inference seam; nothing
/// here depends on how long anything takes.
/// </summary>
public class LLamaSharpEmbedderGateTests
{
    private const int Dimensions = 2;

    private readonly List<string> started = [];
    private readonly Dictionary<string, TaskCompletionSource<IReadOnlyList<float[]>>> inFlight = [];
    private readonly SemaphoreSlim began = new(0);

    private Task<IReadOnlyList<float[]>> EmbedOne(string text, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<float[]>>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (started)
        {
            started.Add(text);
            inFlight[text] = completion;
        }

        began.Release();
        return completion.Task;
    }

    private void Finish(string text)
    {
        lock (started)
        {
            inFlight[text].SetResult([new float[Dimensions]]);
        }
    }

    private async Task NextStarts()
    {
        Assert.True(await began.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), "no inference started");
    }

    [Fact]
    public async Task AQueryIssuedMidBatch_RunsAfterTheTextInFlight_AndCompletesBeforeTheBatch()
    {
        var embedder = new LLamaSharpEmbedder(new EmbeddingSpace("test", Dimensions), EmbedOne);

        var batch = embedder.EmbedAsync(["b1", "b2", "b3"], TestContext.Current.CancellationToken);
        await NextStarts();

        var query = embedder.EmbedAsync(["q"], TestContext.Current.CancellationToken);
        Finish("b1");
        await NextStarts();

        lock (started)
        {
            Assert.Equal(["b1", "q"], started);
        }

        Finish("q");
        await query;
        Assert.False(batch.IsCompleted, "the batch finished before the query it was meant to let through");

        await NextStarts();
        Finish("b2");
        await NextStarts();
        Finish("b3");

        Assert.Equal(3, (await batch).Count);
        lock (started)
        {
            Assert.Equal(["b1", "q", "b2", "b3"], started);
        }
    }
}
