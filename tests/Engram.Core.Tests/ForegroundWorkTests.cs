using Engram.Core;

namespace Engram.Core.Tests;

public class ForegroundWorkTests
{
    /// <summary>A request that throws still leaves the count, or the backlog would yield forever.</summary>
    [Fact]
    public async Task AScopeThatThrows_ReturnsTheCountToZero()
    {
        var foreground = new ForegroundWork();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            foreground.RunAsync<int>(() => throw new InvalidOperationException("tool failed")));

        Assert.Equal(0, foreground.Active);
    }

    [Fact]
    public async Task AScopeThatFaultsAsynchronously_ReturnsTheCountToZero()
    {
        var foreground = new ForegroundWork();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            foreground.RunAsync<int>(async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("tool failed");
            }));

        Assert.Equal(0, foreground.Active);
    }

    [Fact]
    public async Task AWaitWithNothingRunning_ReturnsAtOnce()
    {
        var foreground = new ForegroundWork();

        Assert.True(await foreground.WaitForIdleAsync(TimeSpan.FromHours(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AWaitBehindARequestThatNeverEnds_GivesUpAtTheCap()
    {
        var foreground = new ForegroundWork();
        var never = new TaskCompletionSource<int>();
        var held = foreground.RunAsync(() => never.Task);

        Assert.False(await foreground.WaitForIdleAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));
        Assert.Equal(1, foreground.Active);

        never.SetResult(0);
        await held;
        Assert.Equal(0, foreground.Active);
    }
}
