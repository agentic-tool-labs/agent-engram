using System.Globalization;
using Engram.Core;

namespace Engram.Cli;

/// <summary>
/// <c>engram maintenance claim --pid &lt;pid&gt;</c> — the session-start chain's single-flight
/// gate. Unlisted in usage: nothing but the detached chain has a reason to call it.
/// </summary>
/// <remarks>
/// The lock names the chain's shell, not this process, so it stays held for exactly as long as
/// the chain runs and frees itself however the chain ends. Nothing here opens the store.
/// </remarks>
internal static class MaintenanceCommand
{
    public const string LockName = "maintenance";

    /// <summary>Another chain holds the lock: the only answer that makes the chain skip its jobs.</summary>
    public const int Contended = 1;

    /// <summary>
    /// The claim itself failed. The chain runs its jobs anyway, because each job keeps its own gate
    /// and a broken claim must not silently disable backup, repair and drain.
    /// </summary>
    public const int Error = 2;

    public static int Run(string? homePath, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args is not ["claim", "--pid", var pidText]
            || !int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || pid <= 0)
        {
            stderr.WriteLine("usage: engram maintenance claim --pid <pid>");
            return Error;
        }

        var home = EngramHome.ResolveFromProcess(homePath);

        // The handle is deliberately not disposed: disposing deletes the lock, and the holder is
        // the chain's shell, whose exit is what releases it.
        IndexLock? claimed;
        IndexLockRecord? holder;
        try
        {
            (claimed, holder) = IndexLock.TryClaimNamed(home, LockName, DateTimeOffset.UtcNow, pid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"error: could not claim the maintenance lock: {ex.Message}");
            return Error;
        }

        if (claimed is null)
        {
            stdout.WriteLine(holder is null
                ? "another maintenance chain holds the lock"
                : $"maintenance chain pid {holder.Pid} holds the lock");
            return Contended;
        }

        return 0;
    }
}
