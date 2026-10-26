using System.Diagnostics;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// The session-start chain runs its jobs at most once at a time per home. Driven through the real
/// script and the real claim verb; only the jobs are faked, by a wrapper that hands
/// <c>maintenance</c> to the built CLI and logs everything else.
/// </summary>
public sealed class MaintenanceChainTests : IDisposable
{
    private readonly SandboxHome sandbox = new();
    private readonly string log;
    private readonly string wrapper;

    public MaintenanceChainTests()
    {
        log = Path.Combine(sandbox.Home.Root, "jobs.log");
        wrapper = Path.Combine(sandbox.Home.Root, "engram-wrapper");
        var real = Path.Combine(AppContext.BaseDirectory, "engram");

        File.WriteAllText(
            wrapper,
            "#!/bin/sh\n"
            + $"if [ \"$1\" = maintenance ]; then exec '{real}' \"$@\"; fi\n"
            + $"printf '%s\\n' \"$*\" >> '{log}'\n"
            + "sleep 1\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose() => sandbox.Dispose();

    [Fact]
    public void TwoChainsStartedBackToBack_RunTheJobsOnce_AndBothExitZero()
    {
        SkipOnWindows();

        using var first = StartChain();
        using var second = StartChain();

        Assert.True(first.WaitForExit(TimeSpan.FromSeconds(60)), "the first chain did not finish");
        Assert.True(second.WaitForExit(TimeSpan.FromSeconds(60)), "the second chain did not finish");

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Single(JobLines(), line => line.StartsWith("backup take", StringComparison.Ordinal));
    }

    [Fact]
    public void AChainWhoseLockHolderIsDead_IsClaimedByTheNextOne()
    {
        SkipOnWindows();

        using (var earlier = StartChain())
        {
            Assert.True(earlier.WaitForExit(TimeSpan.FromSeconds(60)), "the earlier chain did not finish");
        }

        Assert.True(
            File.Exists(IndexLock.PathForName(sandbox.Home, "maintenance")),
            "the lock is released by its holder exiting, so the file itself is left behind");

        using var next = StartChain();
        Assert.True(next.WaitForExit(TimeSpan.FromSeconds(60)), "the next chain did not finish");

        Assert.Equal(0, next.ExitCode);
        Assert.Equal(2, JobLines().Count(line => line.StartsWith("backup take", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A claim that cannot be made at all — here an unwritable lock directory — is not contention,
    /// so the chain still runs its jobs rather than silently giving up backup, repair and drain.
    /// </summary>
    [Fact]
    public void AClaimThatErrors_StillRunsTheJobs()
    {
        SkipOnWindows();
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(sandbox.Home.IndexLockDir);
        File.SetUnixFileMode(sandbox.Home.IndexLockDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var chain = StartChain();
            Assert.True(chain.WaitForExit(TimeSpan.FromSeconds(60)), "the chain did not finish");

            Assert.Equal(0, chain.ExitCode);
            Assert.Single(JobLines(), line => line.StartsWith("backup take", StringComparison.Ordinal));
        }
        finally
        {
            File.SetUnixFileMode(sandbox.Home.IndexLockDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private Process StartChain()
    {
        var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(MaintenanceLauncher.BuildScript(wrapper, sandbox.Home.Root, syncEnabled: false, indexRoot: null));
        return Process.Start(info)!;
    }

    private string[] JobLines() => File.Exists(log) ? File.ReadAllLines(log) : [];

    private static void SkipOnWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("the maintenance chain is a POSIX shell script");
        }
    }
}
