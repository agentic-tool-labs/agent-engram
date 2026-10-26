using System.Diagnostics;
using Engram.Core;

namespace Engram.Core.Tests;

/// <summary>
/// The detached child must not hold the hook's stdout, because Claude Code reads that stdout to
/// receive the primer and a pipe reaches EOF only when its last writer closes.
/// </summary>
/// <remarks>
/// Asserted on the script text rather than by timing a spawn. The defect this guards is a
/// difference of tens to hundreds of milliseconds that depends entirely on how much housekeeping
/// happens to be due, so a timing test would be green on an idle store — which is precisely the
/// store a test starts with.
/// </remarks>
public class MaintenanceLauncherTests
{
    private const string Executable = "/opt/engram/bin/engram";
    private const string Home = "/home/someone/.engram";

    /// <summary>
    /// The redirection has to reach <c>/bin/sh</c> itself, not just the command group. Redirecting
    /// the group leaves the shell holding whatever it inherited for as long as the slowest job
    /// runs, which is the entire failure — and it looks correct, because every job's own output
    /// really is discarded.
    /// </summary>
    [Fact]
    public void TheShellsOwnDescriptorsAreReplaced_BeforeItRunsAnything()
    {
        var script = MaintenanceLauncher.BuildScript(Executable, Home, syncEnabled: true, indexRoot: null);

        Assert.StartsWith(MaintenanceLauncher.Redirect, script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf(Executable, StringComparison.Ordinal)
                > script.IndexOf("exec ", StringComparison.Ordinal),
            $"the shell must replace its own descriptors before the first job: {script}");
    }

    /// <summary>
    /// All three, and stdin among them: a child left holding the hook's stdin can wake up reading
    /// a payload meant for the parent.
    /// </summary>
    [Theory]
    [InlineData("</dev/null")]
    [InlineData(">/dev/null")]
    [InlineData("2>&1")]
    public void EveryStandardDescriptorIsRedirected(string redirection) =>
        Assert.Contains(
            redirection,
            MaintenanceLauncher.BuildScript(Executable, Home, syncEnabled: true, indexRoot: null),
            StringComparison.Ordinal);

    /// <summary>
    /// Each job carries its own already-cheap-when-idle guard, so the spawn stays unconditional
    /// and grows no policy of its own. Named here so removing one is a decision rather than a slip.
    /// </summary>
    [Theory]
    [InlineData("backup take --if-due")]
    [InlineData("queue compact --apply --if-large")]
    [InlineData("repair --apply --tokens")]
    [InlineData("sync import --if-new --apply")]
    [InlineData("sync export --if-due --apply")]
    public void EveryJobRunsWithItsOwnIdleGuard(string job) =>
        Assert.Contains(
            job,
            MaintenanceLauncher.BuildScript(Executable, Home, syncEnabled: true, indexRoot: null),
            StringComparison.Ordinal);

    /// <summary>
    /// `[sync] enabled` defaults to false and creates nothing on its own — the ambient
    /// session-start script must omit all three sync lines entirely when it is off, or the very
    /// first automatic export writes a full unfiltered copy of the store to
    /// <c>&lt;home&gt;/sync/&lt;machine-id&gt;/1.jsonl</c> for anyone who has never touched [sync].
    /// </summary>
    [Theory]
    [InlineData("sync import")]
    [InlineData("sync export")]
    [InlineData("sync compact")]
    public void SyncJobsAreOmittedEntirely_WhenSyncIsNotEnabled(string job) =>
        Assert.DoesNotContain(
            job,
            MaintenanceLauncher.BuildScript(Executable, Home, syncEnabled: false, indexRoot: null),
            StringComparison.Ordinal);

    [Fact]
    public void TheIndexDrainIsAddedOnlyWhenThereIsARootToDrainInto()
    {
        Assert.DoesNotContain(
            "index --drain",
            MaintenanceLauncher.BuildScript(Executable, Home, syncEnabled: true, indexRoot: null),
            StringComparison.Ordinal);
        Assert.Contains(
            "index --drain-all --apply --auto '/repo'",
            MaintenanceLauncher.BuildScript(Executable, Home, syncEnabled: true, "/repo"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// --skip &lt;indexRoot&gt; must follow the drain-all job, not precede or replace it: the stamp
    /// that would exclude the invoked root from --freshen's own selection only lands once the
    /// drain-all job has run and completed (spec §5.4).
    /// </summary>
    [Fact]
    public void TheFreshenSelfHealFollowsTheDrainAllJob_ForSessionStartOnly()
    {
        var script = MaintenanceLauncher.BuildScript(Executable, Home, syncEnabled: true, "/repo");

        var drainAllAt = script.IndexOf("index --drain-all --apply --auto '/repo'", StringComparison.Ordinal);
        var freshenAt = script.IndexOf("index --freshen --apply --skip '/repo'", StringComparison.Ordinal);

        Assert.True(drainAllAt >= 0, $"expected the drain-all job: {script}");
        Assert.True(freshenAt > drainAllAt, $"expected --freshen to follow --drain-all: {script}");

        Assert.DoesNotContain(
            "--freshen",
            MaintenanceLauncher.BuildScript(
                Executable, Home, syncEnabled: true, "/repo", MaintenanceLauncher.MaintenanceJobs.EnrollmentIndex),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A path with a quote in it must not end the quoting and become further shell words.
    /// </summary>
    [Fact]
    public void APathCarryingAQuoteStaysOneWord()
    {
        var script = MaintenanceLauncher.BuildScript(Executable, "/home/o'brien/.engram", syncEnabled: true, null);

        Assert.Contains(@"'/home/o'\''brien/.engram'", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// An explicit enroll indexes even with auto_index_on_session_start = false: --auto gates
    /// ambient work and may not gate commanded work (spec §6.9, guard 16).
    /// </summary>
    [Fact]
    public void EnrollmentIndex_ContainsTheIndexJob_WithNoAutoAndNoFull()
    {
        var script = MaintenanceLauncher.BuildScript(
            Executable, Home, syncEnabled: true, "/repo", MaintenanceLauncher.MaintenanceJobs.EnrollmentIndex);

        Assert.Contains("index --drain --apply '/repo'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--auto", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--full", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The enrollment job is the sole command spawned — none of the session-start housekeeping,
    /// which is ambient and gated by its own idle guards, has anything to do with an explicit
    /// enroll (spec §6.9).
    /// </summary>
    [Fact]
    public void EnrollmentIndex_RunsNoSessionStartHousekeeping()
    {
        var script = MaintenanceLauncher.BuildScript(
            Executable, Home, syncEnabled: true, "/repo", MaintenanceLauncher.MaintenanceJobs.EnrollmentIndex);

        Assert.DoesNotContain("backup take", script, StringComparison.Ordinal);
        Assert.DoesNotContain("queue compact", script, StringComparison.Ordinal);
        Assert.DoesNotContain("repair", script, StringComparison.Ordinal);
        Assert.DoesNotContain("sync", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one-descriptor-precedes-first-job property (D55/D56) must hold for both job lists —
    /// a second kind that skipped this would reopen the pipe-vs-file latency bug it fixed.
    /// </summary>
    [Theory]
    [InlineData(MaintenanceLauncher.MaintenanceJobs.SessionStart)]
    [InlineData(MaintenanceLauncher.MaintenanceJobs.EnrollmentIndex)]
    public void RedirectLeadsTheScript_ForEveryJobsValue(MaintenanceLauncher.MaintenanceJobs jobs)
    {
        var script = MaintenanceLauncher.BuildScript(Executable, Home, syncEnabled: true, "/repo", jobs);

        Assert.StartsWith(MaintenanceLauncher.Redirect, script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The priority lines come after the descriptors are replaced, so a <c>renice</c> that lingers
    /// can never hold the hook's pipe, and before the first job, so every job inherits them.
    /// </summary>
    [Theory]
    [InlineData(MaintenanceLauncher.MaintenanceJobs.SessionStart)]
    [InlineData(MaintenanceLauncher.MaintenanceJobs.EnrollmentIndex)]
    public void PriorityFollowsTheRedirect_AndPrecedesTheFirstJob(MaintenanceLauncher.MaintenanceJobs jobs)
    {
        var script = MaintenanceLauncher.BuildScript(Executable, Home, syncEnabled: true, "/repo", jobs);

        var priorityAt = script.IndexOf(MaintenanceLauncher.Priority, StringComparison.Ordinal);
        Assert.Equal(MaintenanceLauncher.Redirect.Length, priorityAt);
        Assert.True(
            script.IndexOf(Executable, StringComparison.Ordinal) > priorityAt,
            $"every job must start after the priority line: {script}");

        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(
                priorityAt + MaintenanceLauncher.Priority.Length,
                script.IndexOf(MaintenanceLauncher.MacPriority, StringComparison.Ordinal));
        }
    }

    /// <summary>Each platform gets its own background line, and never the other's.</summary>
    [Fact]
    public void TheMacScriptCarriesBackgroundQos_AndNoIonice()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("asserts the script macOS builds");
        }

        var script = MaintenanceLauncher.BuildScript(Executable, Home, syncEnabled: false, indexRoot: null);

        Assert.StartsWith(MaintenanceLauncher.Redirect + MaintenanceLauncher.Priority + MaintenanceLauncher.MacPriority, script, StringComparison.Ordinal);
        Assert.DoesNotContain("ionice", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLinuxScriptCarriesBestEffortLowestIo_AndNoTaskpolicy()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("asserts the script Linux builds");
        }

        var script = MaintenanceLauncher.BuildScript(Executable, Home, syncEnabled: false, indexRoot: null);

        Assert.StartsWith(MaintenanceLauncher.Redirect + MaintenanceLauncher.Priority + MaintenanceLauncher.LinuxPriority, script, StringComparison.Ordinal);
        Assert.Contains("ionice -c2 -n7 -p $$", script, StringComparison.Ordinal);
        Assert.DoesNotContain("taskpolicy", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The claim comes after the priority lines and before the first job, and only its contention
    /// answer (exit 1) ends the chain: any other failure falls through to the jobs.
    /// </summary>
    [Fact]
    public void TheClaimPrecedesTheJobs_AndSkipsThemOnlyOnContention()
    {
        var script = MaintenanceLauncher.BuildScript(Executable, Home, syncEnabled: false, indexRoot: null);

        var claim = $"'{Executable}' maintenance claim --pid $$ --home '{Home}'; " + MaintenanceLauncher.SkipOnContention;
        var claimAt = script.IndexOf(claim, StringComparison.Ordinal);
        Assert.True(claimAt > script.IndexOf(MaintenanceLauncher.Priority, StringComparison.Ordinal), $"claim missing or before priority: {script}");
        Assert.True(script.IndexOf(" backup take", StringComparison.Ordinal) > claimAt, $"a job runs before the claim: {script}");
        Assert.Equal("rc=$?; [ \"$rc\" -eq 1 ] && exit 0; ", MaintenanceLauncher.SkipOnContention);
        Assert.DoesNotContain("|| exit", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host with neither <c>renice</c> nor <c>taskpolicy</c> on its PATH still runs every job:
    /// background priority is a courtesy to the foreground, never a precondition for the work.
    /// </summary>
    [Fact]
    public void TheJobsStillRun_WhenThePriorityToolsAreMissing()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("the maintenance chain is a POSIX shell script");
            return;
        }

        var dir = Directory.CreateTempSubdirectory("engram-maint-");
        try
        {
            var log = Path.Combine(dir.FullName, "jobs.log");
            var fake = Path.Combine(dir.FullName, "engram");
            File.WriteAllText(fake, $"#!/bin/sh\nprintf '%s\\n' \"$*\" >> '{log}'\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(MaintenanceLauncher.BuildScript(fake, dir.FullName, syncEnabled: false, indexRoot: null));
            info.Environment["PATH"] = Path.Combine(dir.FullName, "no-such-dir");

            using var process = Process.Start(info)!;
            Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)), "the chain did not finish");

            var ran = File.ReadAllText(log);
            Assert.Contains("backup take --if-due", ran, StringComparison.Ordinal);
            Assert.Contains("repair --apply --tokens", ran, StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
