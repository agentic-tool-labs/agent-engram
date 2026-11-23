using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Doctor's plugin row (D108): the version the newest <c>session-start</c> recorded, against the
/// binary's. Only a recorded mismatch warns; anything unrecorded is <c>Ok</c>, never <c>Broken</c>.
/// </summary>
public class PluginDoctorTests
{
    private static Diagnosis Plugin(SandboxHome sandbox) => Assert.Single(
        Diagnostics.Run(
            sandbox.Home,
            _ => null,
            reachOut: false,
            // Never the developer's own Claude Code settings: this row is about the log alone.
            claudeSettingsPath: Path.Combine(sandbox.Home.Root, "claude-settings.json")).Checks,
        check => check.Name == "plugin");

    private static void Record(SandboxHome sandbox, string kind, string? pluginVersion) =>
        Telemetry.Append(sandbox.Home, new TelemetryRecord(
            Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: "s",
            Kind: kind,
            PluginVersion: pluginVersion));

    [Fact]
    public void AnEmptyLog_IsOk()
    {
        using var sandbox = new SandboxHome();

        var check = Plugin(sandbox);

        Assert.Equal(DiagnosisState.Ok, check.State);
        Assert.Equal("no plugin version recorded yet", check.Detail);
    }

    [Fact]
    public void ASessionStartWithoutTheField_IsOk()
    {
        using var sandbox = new SandboxHome();
        Record(sandbox, TelemetryEventKind.SessionStart, null);

        var check = Plugin(sandbox);

        Assert.Equal(DiagnosisState.Ok, check.State);
        Assert.Equal("no plugin version recorded yet", check.Detail);
    }

    // An older record's mismatch must not outlive a newer start that stated nothing: only the
    // newest session-start answers.
    [Fact]
    public void ANewestRecordWithoutTheField_AfterAnOlderMismatch_IsOk()
    {
        using var sandbox = new SandboxHome();
        Record(sandbox, TelemetryEventKind.SessionStart, "0.0.1");
        Record(sandbox, TelemetryEventKind.SessionStart, null);

        Assert.Equal(DiagnosisState.Ok, Plugin(sandbox).State);
    }

    [Fact]
    public void AMatchingVersion_IsOk()
    {
        using var sandbox = new SandboxHome();
        Record(sandbox, TelemetryEventKind.SessionStart, EngramVersion.Current);

        var check = Plugin(sandbox);

        Assert.Equal(DiagnosisState.Ok, check.State);
        Assert.Equal($"{EngramVersion.Current}, matches the binary", check.Detail);
    }

    [Fact]
    public void ADifferentVersion_Warns_AndSaysHowToFixIt()
    {
        using var sandbox = new SandboxHome();
        Record(sandbox, TelemetryEventKind.SessionStart, "0.0.1");

        var check = Plugin(sandbox);

        Assert.Equal(DiagnosisState.Warn, check.State);
        Assert.Equal($"plugin 0.0.1, binary {EngramVersion.Current}", check.Detail);
        Assert.Equal("update the plugin and the binary to the same release", check.Fix);
    }

    // The nearest record that must not answer: a later record of another kind carrying a version.
    [Fact]
    public void ALaterRecordOfAnotherKind_DoesNotAnswer()
    {
        using var sandbox = new SandboxHome();
        Record(sandbox, TelemetryEventKind.SessionStart, EngramVersion.Current);
        Record(sandbox, TelemetryEventKind.SubagentStart, "0.0.1");

        Assert.Equal(DiagnosisState.Ok, Plugin(sandbox).State);
    }
}
