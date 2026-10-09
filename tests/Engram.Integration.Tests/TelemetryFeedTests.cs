using System.Net;
using System.Net.Sockets;
using Engram.Cli;
using Engram.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2. The feed is the one reader of the telemetry log, so what these hold is when that reader
/// exists, that it is shared, and that the ring is filled ahead of delivery.
/// </summary>
public class TelemetryFeedTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    /// <summary>Counts readers created and reads made, around the real tail.</summary>
    private sealed class Probe
    {
        public int Created;

        public int Reads;

        public Func<string, long, Func<int, IReadOnlyList<string>>> Factory => (path, offset) =>
        {
            Interlocked.Increment(ref Created);
            var tail = new TelemetryTail(path, offset);
            return max =>
            {
                Interlocked.Increment(ref Reads);
                return tail.Read(max);
            };
        };
    }

    private static void Record(SandboxHome sandbox, string kind, string session = "s", string? query = null) =>
        Telemetry.Append(sandbox.Home, new TelemetryRecord(DateTimeOffset.UtcNow.ToString("O"), session, kind, query));

    private static async Task<bool> Settles(Func<bool> condition, TimeSpan? patience = null)
    {
        var deadline = DateTime.UtcNow + (patience ?? Patience);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        return condition();
    }

    /// <summary>A subscriber that accepts the request and never answers it.</summary>
    private sealed class HangingSink : IDisposable
    {
        private readonly HttpListener listener = new();

        public int Accepted;

        public string Url { get; }

        public HangingSink()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Url = $"http://127.0.0.1:{port}/engram/";
            listener.Prefixes.Add(Url);
            listener.Start();
            _ = Task.Run(async () =>
            {
                var held = new List<HttpListenerContext>();
                while (listener.IsListening)
                {
                    try
                    {
                        held.Add(await listener.GetContextAsync().ConfigureAwait(false));
                        Interlocked.Increment(ref Accepted);
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            });
        }

        public void Dispose() => listener.Close();
    }

    [Fact]
    public async Task WithNoWebhookAndNoDemand_NoReaderIsEverCreated()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var probe = new Probe();
        var feed = new TelemetryFeed(sandbox.Home, openReader: probe.Factory);
        var service = new WebhookService(sandbox.Home, NullLogger<WebhookService>.Instance, feed);

        await service.StartAsync(CancellationToken.None);
        Record(sandbox, TelemetryEventKind.Remember, query: "unread");
        await Task.Delay(WebhookService.PollInterval * 4, TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);
        service.Dispose();

        Assert.Equal(0, probe.Created);
        Assert.Equal(0, probe.Reads);
        Assert.False(feed.IsRunning);
    }

    [Fact]
    public void AWebhookStartsTheReaderOnce_AndNothingElseDoes()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var probe = new Probe();
        var feed = new TelemetryFeed(sandbox.Home, openReader: probe.Factory);

        feed.HoldForWebhook();
        feed.HoldForWebhook();

        Assert.Equal(1, probe.Created);
        Assert.True(feed.IsRunning);
    }

    [Fact]
    public void DemandStartsTheReaderAtTheEndOfTheFile_SoEarlierRecordsAreNotReplayed()
    {
        using var sandbox = new SandboxHome(initialize: false);
        Record(sandbox, TelemetryEventKind.Recall, query: "before");
        var feed = new TelemetryFeed(sandbox.Home, new ManualTime());

        feed.Demand();
        Record(sandbox, TelemetryEventKind.Remember, query: "after");
        var batch = feed.Poll();

        var only = Assert.Single(batch);
        Assert.Equal("after", only.Record.Query);
        var read = feed.Read(feed.Epoch, after: 0, limit: 10, sessionId: null);
        Assert.Equal(1, Assert.Single(read.Rows).Seq);
        Assert.Equal(1, read.Head);
    }

    [Fact]
    public void WhenDemandLapses_TheReaderStops_AndTheNextDemandStartsANewEpoch()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var time = new ManualTime();
        var feed = new TelemetryFeed(sandbox.Home, time);

        feed.Demand();
        var first = feed.Epoch;
        time.Advance(TelemetryFeed.DemandWindow - TimeSpan.FromSeconds(1));
        feed.Poll();
        Assert.True(feed.IsRunning);

        time.Advance(TimeSpan.FromSeconds(2));
        feed.Poll();
        Assert.False(feed.IsRunning);

        Record(sandbox, TelemetryEventKind.Remember, query: "while stopped");
        feed.Demand();

        Assert.True(feed.IsRunning);
        Assert.NotEqual(first, feed.Epoch);
        Assert.Empty(feed.Poll());
    }

    [Fact]
    public void DemandWithinTheWindow_KeepsTheSameEpoch()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var time = new ManualTime();
        var feed = new TelemetryFeed(sandbox.Home, time);

        feed.Demand();
        var epoch = feed.Epoch;
        time.Advance(TimeSpan.FromSeconds(9));
        feed.Demand();
        time.Advance(TimeSpan.FromSeconds(9));
        feed.Poll();

        Assert.True(feed.IsRunning);
        Assert.Equal(epoch, feed.Epoch);
    }

    [Fact]
    public void AWebhookHold_OutlivesTheDemandWindow()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var time = new ManualTime();
        var feed = new TelemetryFeed(sandbox.Home, time);

        feed.HoldForWebhook();
        feed.Demand();
        time.Advance(TelemetryFeed.DemandWindow * 6);
        feed.Poll();

        Assert.True(feed.IsRunning);
    }

    [Fact]
    public void AWebhookAndATailShareOneReader_AndOneReadPerPoll()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var probe = new Probe();
        var feed = new TelemetryFeed(sandbox.Home, new ManualTime(), probe.Factory);

        feed.HoldForWebhook();
        feed.Demand();
        feed.Demand();
        Record(sandbox, TelemetryEventKind.Remember, query: "shared");
        var readsBefore = probe.Reads;
        var batch = feed.Poll();

        Assert.Equal(1, probe.Created);
        Assert.Equal(readsBefore + 1, probe.Reads);
        Assert.Single(batch);
        Assert.Single(feed.Read(feed.Epoch, 0, 10, null).Rows);
    }

    [Fact]
    public async Task AWebhookAndATailOnTheRunningService_DeliverAndFillTheRingFromOneReader()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var sink = new HangingSink();
        File.WriteAllText(sandbox.Home.ConfigPath, $"[webhook]\nurl = \"{sink.Url}\"\ntimeout_ms = 30000\n");
        var probe = new Probe();
        var feed = new TelemetryFeed(sandbox.Home, openReader: probe.Factory);
        var service = new WebhookService(sandbox.Home, NullLogger<WebhookService>.Instance, feed);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Settles(() => feed.IsRunning), "the webhook never started the reader");
            feed.Demand();
            Record(sandbox, TelemetryEventKind.Remember, query: "both");

            Assert.True(await Settles(() => Volatile.Read(ref sink.Accepted) == 1), "the subscriber got no request");
            Assert.Single(feed.Read(feed.Epoch, 0, 10, null).Rows);
            Assert.Equal(1, probe.Created);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    /// <summary>
    /// The 30 s timeout is the point: a subscriber that never answers holds the delivery for all of
    /// it, and the batch that provoked the hang must already be in the ring. Later batches wait
    /// for the delivery to give up, as they always have; this holds the ordering within one.
    /// </summary>
    [Fact]
    public async Task AHangingSubscriber_DoesNotDelayTheRing()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var sink = new HangingSink();
        File.WriteAllText(sandbox.Home.ConfigPath, $"[webhook]\nurl = \"{sink.Url}\"\ntimeout_ms = 30000\n");
        var feed = new TelemetryFeed(sandbox.Home);
        var service = new WebhookService(sandbox.Home, NullLogger<WebhookService>.Instance, feed);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await Settles(() => feed.IsRunning), "the webhook never started the reader");
            Record(sandbox, TelemetryEventKind.Remember, query: "first");

            Assert.True(await Settles(() => Volatile.Read(ref sink.Accepted) == 1), "the subscriber got no request");
            Assert.Single(feed.Read(feed.Epoch, 0, 10, null).Rows);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }
}
