using System.Text.Json.Nodes;
using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// <c>engram hook &lt;verb&gt; --source mod</c> (D107): the bridge mod's way in on a managed install.
/// A session whose classic <c>SessionStart</c> ran stands every mod-sourced call down, so the bridge
/// never doubles a classic hook; otherwise the call is the classic one plus <c>source: "mod"</c>.
/// </summary>
[Collection(ConsoleStdinCollection.Name)]
public class HookSourceModTests
{
    private const string Session = "session-under-test";
    private const string Remember = "mcp__plugin_engram_engram__engram_remember";

    public static TheoryData<string> Verbs => new()
    {
        "session-start", "subagent-start", "pre-compact", "post-compact", "user-prompt",
        "file-touched", "memory-guard", "lookup-nudge", "tool-observed",
    };

    /// <summary>The payload under which each verb does its full work on the classic path.</summary>
    private static string Payload(SandboxHome sandbox, string verb, string sessionId = Session)
    {
        var root = new JsonObject { ["session_id"] = sessionId, ["cwd"] = sandbox.Home.Root };
        switch (verb)
        {
            case "subagent-start":
                root["agent_type"] = "wrangl:implementor";
                break;
            case "post-compact":
                root["compact_summary"] = "<engram-digest v=\"1\">\n- The user prefers short commit subjects.\n</engram-digest>";
                break;
            case "file-touched":
                root["tool_name"] = "Edit";
                root["tool_input"] = new JsonObject { ["file_path"] = Path.Combine(sandbox.Home.Root, "touched.cs") };
                break;
            case "memory-guard":
                root["tool_name"] = "Write";
                root["tool_input"] = new JsonObject
                {
                    ["file_path"] = Path.Combine(sandbox.Home.ClaudeProjectsDir, "some-project", "memory", "note.md"),
                };
                break;
            case "lookup-nudge":
                // An enrolled, indexed checkout, the state every lookup-nudge deny needs.
                var checkout = Path.Combine(sandbox.Home.Root, "checkout");
                Directory.CreateDirectory(Path.Combine(checkout, ".git"));
                var now = DateTimeOffset.UtcNow;
                RepoIndexStamp.Append(sandbox.Home.RepoIndexStampPath, now, checkout, "stamped-repo-identity", "enroll");
                RepoIndexStamp.Append(sandbox.Home.RepoIndexStampPath, now, checkout, "stamped-repo-identity", RepoIndexStamp.Indexed);
                root["cwd"] = checkout;
                root["tool_name"] = "Grep";
                root["tool_input"] = new JsonObject { ["pattern"] = "ProcessFile" };
                break;
            case "tool-observed":
                root["tool_name"] = Remember;
                root["tool_input"] = new JsonObject { ["statement"] = "x" };
                break;
        }

        return root.ToJsonString();
    }

    // Console.SetIn is process-global, not per-test — see ConsoleStdinCollection.
    private static (int ExitCode, string Stdout, string Stderr) Run(SandboxHome sandbox, string payload, params string[] hookArgs)
    {
        Console.SetIn(new StringReader(payload));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", .. hookArgs], stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static void MarkClassic(SandboxHome sandbox, string sessionId = Session) =>
        Assert.True(SessionNudgeState.TryAppend(sandbox.Home.ClassicHooksStatePath, sessionId));

    private static IReadOnlyList<TelemetryRecord> Records(SandboxHome sandbox, string kind)
    {
        var path = Telemetry.ResolvePath(sandbox.Home);
        return !File.Exists(path)
            ? []
            : File.ReadAllLines(path)
                .Select(Telemetry.TryParse)
                .Where(record => record is not null && record.Kind == kind)
                .Select(record => record!)
                .ToList();
    }

    // Falsify: delete the SessionNudgeState.Contains stand-down in HookCommand.Run and every row
    // reddens except user-prompt, which is a no-op either way.
    [Theory]
    [MemberData(nameof(Verbs))]
    public void ModCall_ForAClassicSession_StandsDown_WithNoOutputAndNoFileChange(string verb)
    {
        using var sandbox = new SandboxHome();
        MarkClassic(sandbox);
        var payload = Payload(sandbox, verb);
        var before = HomeSnapshot.Take(sandbox.Home.Root);

        var result = Run(sandbox, payload, verb, "--source", "mod");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
        Assert.Equal(before, HomeSnapshot.Take(sandbox.Home.Root));
    }

    [Fact]
    public void TheMarkIsPerSession_AnotherSessionIsNotStoodDown()
    {
        using var sandbox = new SandboxHome();
        MarkClassic(sandbox, "some-other-session");

        var result = Run(sandbox, Payload(sandbox, "tool-observed"), "tool-observed", "--source", "mod");

        Assert.Equal(0, result.ExitCode);
        var record = Assert.Single(Records(sandbox, TelemetryEventKind.ToolObserved));
        Assert.Equal(Session, record.SessionId);
        Assert.Equal("mod", record.Source);
    }

    public static TheoryData<string, string> RecordingVerbs => new()
    {
        { "session-start", TelemetryEventKind.SessionStart },
        { "subagent-start", TelemetryEventKind.SubagentStart },
        { "pre-compact", TelemetryEventKind.PreCompact },
        { "post-compact", TelemetryEventKind.PostCompact },
        { "file-touched", TelemetryEventKind.FileTouched },
        { "memory-guard", TelemetryEventKind.MemoryGuard },
        { "lookup-nudge", TelemetryEventKind.LookupNudge },
        { "tool-observed", TelemetryEventKind.ToolObserved },
    };

    // Falsify: make Sourced return the record unchanged and the mod arm's Source assertion
    // reddens for every row.
    [Theory]
    [MemberData(nameof(RecordingVerbs))]
    public void ModCall_WithoutAMark_IsTheClassicCall_PlusSource(string verb, string kind)
    {
        using var classic = new SandboxHome();
        using var mod = new SandboxHome();

        var classicResult = Run(classic, Payload(classic, verb), verb);
        var modResult = Run(mod, Payload(mod, verb), verb, "--source", "mod");

        Assert.Equal(classicResult.ExitCode, modResult.ExitCode);
        Assert.Equal(
            classicResult.Stdout.Replace(classic.Home.Root, "<home>"),
            modResult.Stdout.Replace(mod.Home.Root, "<home>"));
        var classicRecord = Assert.Single(Records(classic, kind));
        var modRecord = Assert.Single(Records(mod, kind));
        Assert.Null(classicRecord.Source);
        Assert.Equal("mod", modRecord.Source);
        Assert.Equal(classicRecord.SessionId, modRecord.SessionId);
        Assert.Equal(classicRecord.Tool, modRecord.Tool);
    }

    [Fact]
    public void AClassicRecord_HasNoSourceField()
    {
        using var sandbox = new SandboxHome();

        Run(sandbox, Payload(sandbox, "tool-observed"), "tool-observed");

        var line = Assert.Single(File.ReadAllLines(Telemetry.ResolvePath(sandbox.Home)), l => l.Contains("tool-observed"));
        Assert.DoesNotContain("\"source\"", line);
    }

    // Falsify: delete the TryAppend in RunSessionStart and this reddens.
    [Fact]
    public void ClassicSessionStart_LeavesTheMark()
    {
        using var sandbox = new SandboxHome();

        Run(sandbox, Payload(sandbox, "session-start"), "session-start");

        Assert.True(SessionNudgeState.Contains(sandbox.Home.ClassicHooksStatePath, Session));
    }

    // Falsify: drop the `source is null` from the mark's guard in RunSessionStart and this reddens.
    [Fact]
    public void ModSessionStart_LeavesNoMark_SoTheBridgesLaterCallsStillRun()
    {
        using var sandbox = new SandboxHome();

        Run(sandbox, Payload(sandbox, "session-start"), "session-start", "--source", "mod");
        Run(sandbox, Payload(sandbox, "tool-observed"), "tool-observed", "--source", "mod");

        Assert.False(SessionNudgeState.Contains(sandbox.Home.ClassicHooksStatePath, Session));
        Assert.Single(Records(sandbox, TelemetryEventKind.ToolObserved));
    }

    // The nearest input that must not count as mod: an unknown value is treated as absent.
    [Fact]
    public void AnUnknownSourceValue_IsTreatedAsAbsent_AndIsNotStoodDown()
    {
        using var sandbox = new SandboxHome();
        MarkClassic(sandbox);

        var result = Run(sandbox, Payload(sandbox, "tool-observed"), "tool-observed", "--source", "bridge");

        Assert.Equal(0, result.ExitCode);
        var record = Assert.Single(Records(sandbox, TelemetryEventKind.ToolObserved));
        Assert.Null(record.Source);
    }

    [Theory]
    [InlineData("--source")]
    [InlineData("--sauce", "mod")]
    [InlineData("--source", "mod", "extra")]
    public void AMalformedFlag_PrintsUsage_AndExitsOne(params string[] flag)
    {
        using var sandbox = new SandboxHome();

        var result = Run(sandbox, Payload(sandbox, "tool-observed"), ["tool-observed", .. flag]);

        Assert.Equal(1, result.ExitCode);
        Assert.NotEqual(string.Empty, result.Stderr);
        Assert.Empty(Records(sandbox, TelemetryEventKind.ToolObserved));
    }

    // file-touched --source mod must still never open the database: with no store on disk, an
    // open would create engram.db.
    [Fact]
    public void ModFileTouched_NeverOpensTheDatabase()
    {
        using var sandbox = new SandboxHome();
        File.Delete(sandbox.Home.DatabasePath);

        var result = Run(sandbox, Payload(sandbox, "file-touched"), "file-touched", "--source", "mod");

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(sandbox.Home.DatabasePath));
        Assert.Single(Directory.EnumerateFiles(sandbox.Home.QueueDir, "*.spool"));
        Assert.Equal("mod", Assert.Single(Records(sandbox, TelemetryEventKind.FileTouched)).Source);
    }
}
