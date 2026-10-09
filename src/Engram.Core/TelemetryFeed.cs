namespace Engram.Core;

/// <summary>One parsed telemetry record and its position in the current epoch.</summary>
public sealed record FeedEntry(long Seq, TelemetryRecord Record);

/// <summary>A record as read from the log: the verbatim line and what it parsed to.</summary>
public sealed record FeedItem(string Line, TelemetryRecord Record);

/// <summary>What a cursor read of the ring returned.</summary>
/// <param name="Epoch">The ring's current epoch.</param>
/// <param name="Head">The newest sequence number assigned in this epoch; 0 when none.</param>
/// <param name="Rows">Matching entries, newest first.</param>
/// <param name="Skipped">
/// Null when the caller held no cursor for this epoch and so was given no rows; otherwise how
/// many records after its cursor it was not given.
/// </param>
public sealed record FeedRead(string Epoch, long Head, IReadOnlyList<FeedEntry> Rows, long? Skipped);

/// <summary>
/// The one reader of <c>telemetry.jsonl</c> in a server process, and the ring of recent records it
/// feeds. The webhook and the memory tail both consume it.
/// </summary>
/// <remarks>
/// <para><b>Exactly one reader.</b> Telemetry writers open the file <c>FileShare.None</c>, so each
/// extra reader is another thing a writer can collide with and — past its retry budget — silently
/// drop a record for. A second consumer therefore reads what this one parsed rather than opening
/// the file again.</para>
///
/// <para><b>It runs only while someone wants it.</b> A webhook subscriber holds it for the life of
/// the process; a memory tail holds it for <see cref="DemandWindow"/> after each request. With
/// neither there is no reader at all, so a server nobody is watching adds no exposure to the log.
/// Each start is at the end of the file and begins a new epoch: records written while it was stopped
/// are not recoverable from here, and a consumer comparing sequence numbers across the gap would
/// read the missing stretch as continuity.</para>
///
/// <para>Whoever drives <see cref="Poll"/> owns the cadence; this type has no thread of its own.</para>
/// </remarks>
public sealed class TelemetryFeed
{
    /// <summary>Records kept; older ones are overwritten and reported as skipped.</summary>
    public const int RingCapacity = 1024;

    /// <summary>Lines read per <see cref="Poll"/>, so a burst drains over several polls.</summary>
    public const int MaxLinesPerPoll = 64;

    /// <summary>How long one tail request keeps the reader alive.</summary>
    public static readonly TimeSpan DemandWindow = TimeSpan.FromSeconds(10);

    private readonly object gate = new();
    private readonly EngramHome home;
    private readonly TimeProvider time;
    private readonly Func<string, long, Func<int, IReadOnlyList<string>>> openReader;
    private readonly LinkedList<FeedEntry> ring = new();

    private Func<int, IReadOnlyList<string>>? reader;
    private string epoch = string.Empty;
    private long head;
    private bool webhookHold;
    private DateTimeOffset demandedUntil = DateTimeOffset.MinValue;

    /// <param name="home">Where the log lives.</param>
    /// <param name="time">The clock the demand window is measured on.</param>
    /// <param name="openReader">
    /// Opens a reader at a byte offset in a file. Defaults to <see cref="TelemetryTail"/>; the seam
    /// exists so a test can observe whether a reader was created at all.
    /// </param>
    public TelemetryFeed(
        EngramHome home,
        TimeProvider? time = null,
        Func<string, long, Func<int, IReadOnlyList<string>>>? openReader = null)
    {
        this.home = home;
        this.time = time ?? TimeProvider.System;
        this.openReader = openReader ?? ((path, offset) => new TelemetryTail(path, offset).Read);
    }

    /// <summary>True while a reader exists.</summary>
    public bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return reader is not null;
            }
        }
    }

    /// <summary>The epoch of the current (or most recent) reader; empty before the first start.</summary>
    public string Epoch
    {
        get
        {
            lock (gate)
            {
                return epoch;
            }
        }
    }

    /// <summary>Keeps the reader alive for the life of the process, as a configured webhook does.</summary>
    public void HoldForWebhook()
    {
        lock (gate)
        {
            webhookHold = true;
            EnsureStarted();
        }
    }

    /// <summary>
    /// Records that a memory tail is polling. The first call after a lapse starts the reader at the
    /// end of the file.
    /// </summary>
    public void Demand()
    {
        lock (gate)
        {
            demandedUntil = time.GetUtcNow() + DemandWindow;
            EnsureStarted();
        }
    }

    /// <summary>
    /// Reads what was appended since the last call, adds it to the ring, and returns it for
    /// delivery. Empty when no reader is running.
    /// </summary>
    /// <remarks>
    /// The ring is filled before this returns, so whatever the caller does with the batch — however
    /// slowly — cannot hide a record that has already been read. It can still delay the next read:
    /// the caller that delivers the batch is the caller that polls.
    /// </remarks>
    public IReadOnlyList<FeedItem> Poll()
    {
        lock (gate)
        {
            if (reader is not null && !webhookHold && time.GetUtcNow() >= demandedUntil)
            {
                reader = null;
            }

            if (reader is null)
            {
                return [];
            }

            var items = new List<FeedItem>();
            foreach (var line in reader(MaxLinesPerPoll))
            {
                if (Telemetry.TryParse(line) is not { } record)
                {
                    continue;
                }

                head++;
                ring.AddLast(new FeedEntry(head, record));
                if (ring.Count > RingCapacity)
                {
                    ring.RemoveFirst();
                }

                items.Add(new FeedItem(line, record));
            }

            return items;
        }
    }

    /// <summary>Reads the ring from a cursor.</summary>
    /// <param name="cursorEpoch">The epoch the caller's cursor belongs to, or null.</param>
    /// <param name="after">The last sequence number the caller has; null is treated as the head.</param>
    /// <param name="limit">Most rows to return; the newest are kept.</param>
    /// <param name="sessionId">When set, only records whose <c>session_id</c> equals it.</param>
    public FeedRead Read(string? cursorEpoch, long? after, int limit, string? sessionId)
    {
        lock (gate)
        {
            if (cursorEpoch is null || cursorEpoch != epoch)
            {
                return new FeedRead(epoch, head, [], null);
            }

            var cursor = after ?? head;
            var oldest = head - ring.Count + 1;

            // Records the ring has overwritten since the cursor are lost whatever their session:
            // once evicted, their session ids are not known.
            var overflowed = Math.Max(0, oldest - 1 - cursor);

            var matching = ring
                .Where(entry => entry.Seq > cursor
                    && (sessionId is null || string.Equals(entry.Record.SessionId, sessionId, StringComparison.Ordinal)))
                .ToList();

            var rows = matching.AsEnumerable().Reverse().Take(limit).ToList();
            return new FeedRead(epoch, head, rows, overflowed + (matching.Count - rows.Count));
        }
    }

    private void EnsureStarted()
    {
        if (reader is not null)
        {
            return;
        }

        var path = Telemetry.ResolvePath(home);
        epoch = Guid.NewGuid().ToString("N")[..16];
        head = 0;
        ring.Clear();
        reader = openReader(path, TelemetryTail.EndOf(path));
    }
}
