using System.Diagnostics;

namespace Engram.Core;

/// <summary>
/// Counts the requests the model is waiting on, so background work can stay out of their way.
/// </summary>
/// <remarks>
/// <para>Entered once per surface — every MCP tool call, every mod op that is not a read-only
/// lookup — and never per tool, so a new tool cannot forget it. Lookups stay out because a band or
/// a tail polls them every few seconds from every session, and counting them would hold the
/// backlog paused for as long as anyone has a pane open.</para>
///
/// <para>Background work asks <see cref="WaitForIdleAsync"/> before each unit and always gets a
/// cap: a request left open mid-call must slow vector production, never stop it.</para>
/// </remarks>
public sealed class ForegroundWork
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private int active;

    public int Active => Volatile.Read(ref active);

    /// <summary>Runs <paramref name="work"/> as foreground, counted until it ends however it ends.</summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        Interlocked.Increment(ref active);
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref active);
        }
    }

    /// <summary>
    /// Waits while any foreground request is running, for at most <paramref name="cap"/>.
    /// </summary>
    /// <returns>True when the foreground went idle, false when the cap ran out first.</returns>
    public async Task<bool> WaitForIdleAsync(TimeSpan cap, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (Active > 0)
        {
            var left = cap - Stopwatch.GetElapsedTime(started);
            if (left <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.Delay(left < PollInterval ? left : PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}
