using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Doctor's hook delivery row (D107): which channel delivered the newest <c>session-start</c>, and a
/// warning when the newest active session has none at all, which is what a skipped hook leaves.
/// </summary>
public class HookDeliveryDoctorTests
{
    private static Diagnosis HookDelivery(SandboxHome sandbox) => Assert.Single(
        Diagnostics.Run(sandbox.Home, _ => null, reachOut: false).Checks,
        check => check.Name == "hook delivery");

    private static void Record(SandboxHome sandbox, string kind, string sessionId, string? source = null) =>
        Telemetry.Append(sandbox.Home, new TelemetryRecord(
            Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: sessionId,
            Kind: kind,
            Source: source));

    [Fact]
    public void AnEmptyLog_IsOk_NeverWarn()
    {
        using var sandbox = new SandboxHome();

        Assert.Equal(DiagnosisState.Ok, HookDelivery(sandbox).State);
    }

    [Fact]
    public void ANewestClassicSessionStart_ReadsClassic()
    {
        using var sandbox = new SandboxHome();
        Record(sandbox, TelemetryEventKind.SessionStart, "a", "mod");
        Record(sandbox, TelemetryEventKind.SessionStart, "b");

        var check = HookDelivery(sandbox);

        Assert.Equal(DiagnosisState.Ok, check.State);
        Assert.Contains("classic", check.Detail);
    }

    // Falsify: hard-code "classic" in CheckHookDelivery's detail and this reddens.
    [Fact]
    public void ANewestModSessionStart_ReadsMod()
    {
        using var sandbox = new SandboxHome();
        Record(sandbox, TelemetryEventKind.SessionStart, "a");
        Record(sandbox, TelemetryEventKind.SessionStart, "b", "mod");

        var check = HookDelivery(sandbox);

        Assert.Equal(DiagnosisState.Ok, check.State);
        Assert.Contains("mod", check.Detail);
    }

    // The nearest healthy input: tool activity newer than the session's own start. A rule keyed on
    // recency alone warns here, on every healthy install.
    [Fact]
    public void ActivityAfterItsOwnSessionStart_IsOk()
    {
        using var sandbox = new SandboxHome();
        Record(sandbox, TelemetryEventKind.SessionStart, "a");
        Record(sandbox, TelemetryEventKind.ToolObserved, "a");
        Record(sandbox, TelemetryEventKind.ModCall, "a");

        Assert.Equal(DiagnosisState.Ok, HookDelivery(sandbox).State);
    }

    // Falsify: return Ok from the warn branch and both rows redden.
    [Theory]
    [InlineData(TelemetryEventKind.ModCall)]
    [InlineData(TelemetryEventKind.ToolObserved)]
    public void ActivityFromASessionThatNeverStarted_Warns_NeverBroken(string kind)
    {
        using var sandbox = new SandboxHome();
        Record(sandbox, TelemetryEventKind.SessionStart, "a");
        Record(sandbox, kind, "b");

        var check = HookDelivery(sandbox);

        Assert.Equal(DiagnosisState.Warn, check.State);
        Assert.Contains("hooks may be skipped", check.Detail);
    }

    [Fact]
    public void ReadingTheRow_ChangesNoFileInTheHome()
    {
        using var sandbox = new SandboxHome();
        Record(sandbox, TelemetryEventKind.SessionStart, "a");
        Record(sandbox, TelemetryEventKind.ModCall, "b");
        var before = HomeSnapshot.Take(sandbox.Home.Root);

        HookDelivery(sandbox);

        Assert.Equal(before, HomeSnapshot.Take(sandbox.Home.Root));
    }
}
