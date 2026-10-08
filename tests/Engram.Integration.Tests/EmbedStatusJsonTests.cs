using System.Text.Json;
using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2. <c>embed --status --json</c> renders the same view the text does, so every state is
/// asserted from the view plus a planted <c>embedding.json</c>, and against the text where the two
/// must agree.
/// </summary>
public class EmbedStatusJsonTests
{
    private static JsonElement Json(EmbedStatusView view, DateTimeOffset now) =>
        JsonDocument.Parse(EmbedStatus.ToJson(view, now)).RootElement;

    private static EmbedStatusView Configured(SandboxHome sandbox, DateTimeOffset now, int embedded, int pending) =>
        EmbedStatus.Read(sandbox.Home, now) with { Provider = "local", Note = null, Embedded = embedded, Pending = pending };

    private static EmbeddingProgress Note(DateTimeOffset updated, string? outcome = "running", string? error = null) =>
        new(updated, updated.AddSeconds(-100), 4242, "x/4", 500, 0, outcome, error, []);

    [Fact]
    public void Running_ReportsPidAgeRateAndEta()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;
        EmbeddingProgress.Write(sandbox.Home, Note(now.AddSeconds(-3)));

        var view = Configured(sandbox, now, 500, 500);
        var json = Json(view, now);

        Assert.Equal("running", json.GetProperty("backlog").GetProperty("state").GetString());
        Assert.Equal(4242, json.GetProperty("backlog").GetProperty("pid").GetInt32());
        Assert.Equal(3, json.GetProperty("backlog").GetProperty("last_update_seconds").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("backlog").GetProperty("reason").ValueKind);
        Assert.StartsWith("5.0/s mean since", json.GetProperty("rate").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("~", json.GetProperty("eta").GetString(), StringComparison.Ordinal);
        Assert.Equal(500, json.GetProperty("embedded").GetInt32());
        Assert.Equal(1000, json.GetProperty("total").GetInt32());
        Assert.Equal(500, json.GetProperty("remaining").GetInt32());
        Assert.Contains("running, pid 4242, last update 3s ago", string.Join('\n', EmbedStatus.Lines(view, now, false)), StringComparison.Ordinal);
    }

    [Fact]
    public void NoNote_WithWorkOutstanding_IsNotRunning_WithTheTextsReason()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;

        var view = Configured(sandbox, now, 0, 5);
        var backlog = Json(view, now).GetProperty("backlog");

        Assert.Equal("not-running", backlog.GetProperty("state").GetString());
        Assert.Equal("start the server with `engram start`", backlog.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, backlog.GetProperty("pid").ValueKind);
        Assert.Equal(JsonValueKind.Null, backlog.GetProperty("last_update_seconds").ValueKind);
        Assert.Contains("not running — start the server with `engram start`", string.Join('\n', EmbedStatus.Lines(view, now, false)), StringComparison.Ordinal);
    }

    [Fact]
    public void NoNote_WithNothingOutstanding_IsNotRunning_WithNoReason()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;

        var backlog = Json(Configured(sandbox, now, 5, 0), now).GetProperty("backlog");

        Assert.Equal("not-running", backlog.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, backlog.GetProperty("reason").ValueKind);
    }

    [Fact]
    public void Declined_IsUnavailable_WithTheReasonVerbatim_NotStalled()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;
        EmbeddingProgress.WriteUnavailable(sandbox.Home, "qwen3-embedding-0.6b is not downloaded yet.");

        var json = Json(Configured(sandbox, now, 0, 873), now);

        Assert.Equal("unavailable", json.GetProperty("backlog").GetProperty("state").GetString());
        Assert.Equal("qwen3-embedding-0.6b is not downloaded yet.", json.GetProperty("backlog").GetProperty("reason").GetString());
        Assert.Equal("qwen3-embedding-0.6b is not downloaded yet.", json.GetProperty("last_error").GetString());
    }

    /// <summary>The standing statement does not age into <c>stalled</c> (the clock is not consulted).</summary>
    [Fact]
    public void Declined_TenMinutesOld_IsStillUnavailable()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;
        EmbeddingProgress.Write(sandbox.Home, Note(now.AddMinutes(-10), EmbeddingProgress.Unavailable, "no model"));

        var state = Json(Configured(sandbox, now, 0, 9), now).GetProperty("backlog").GetProperty("state").GetString();

        Assert.Equal("unavailable", state);
    }

    [Fact]
    public void StaleNote_IsStalled_AndTheReasonIsTheTextsWording()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;
        EmbeddingProgress.Write(sandbox.Home, Note(now.AddSeconds(-60)));

        var view = Configured(sandbox, now, 10, 90);
        var backlog = Json(view, now).GetProperty("backlog");

        Assert.Equal("stalled", backlog.GetProperty("state").GetString());
        Assert.Equal("pid 4242 last reported 1m 0s ago", backlog.GetProperty("reason").GetString());
        Assert.Equal(60, backlog.GetProperty("last_update_seconds").GetInt32());
        Assert.Contains("stalled or stopped — pid 4242 last reported 1m 0s ago", string.Join('\n', EmbedStatus.Lines(view, now, false)), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, Json(view, now).GetProperty("rate").ValueKind);
        Assert.Equal(JsonValueKind.Null, Json(view, now).GetProperty("eta").ValueKind);
    }

    /// <summary>Nearest non-matching input to the stalled rule: a note inside the window stays <c>running</c>.</summary>
    [Fact]
    public void FreshNote_IsNotStalled()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;
        EmbeddingProgress.Write(sandbox.Home, Note(now.AddSeconds(-20)));

        var state = Json(Configured(sandbox, now, 10, 90), now).GetProperty("backlog").GetProperty("state").GetString();

        Assert.Equal("running", state);
    }

    [Fact]
    public void EmbeddingsOff_HasNullSpaceAndProvider_ZeroCounts_AndTheNoteAsReason()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;
        var view = EmbedStatus.Read(sandbox.Home, now) with { Embedded = 0, Pending = 5 };
        Assert.Equal("none", view.Provider);

        var json = Json(view, now);

        Assert.Equal(JsonValueKind.Null, json.GetProperty("space").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("provider").ValueKind);
        Assert.Equal(0, json.GetProperty("embedded").GetInt32());
        Assert.Equal(0, json.GetProperty("total").GetInt32());
        Assert.Equal(0, json.GetProperty("remaining").GetInt32());
        Assert.Equal("not-running", json.GetProperty("backlog").GetProperty("state").GetString());
        Assert.Equal("embeddings are off — engram init --with-embeddings", json.GetProperty("backlog").GetProperty("reason").GetString());
        Assert.Equal("embeddings are off — engram init --with-embeddings", json.GetProperty("note").GetString());
    }

    /// <summary>Nearest non-matching input to the off rule: a configured provider keeps its name.</summary>
    [Fact]
    public void ProviderConfigured_KeepsItsNameAndCounts()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;

        var json = Json(Configured(sandbox, now, 3, 4), now);

        Assert.Equal("local", json.GetProperty("provider").GetString());
        Assert.Equal(7, json.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("note").ValueKind);
    }
}
