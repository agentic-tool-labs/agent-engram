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

    public static int Run(string? homePath, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args is not ["claim", "--pid", var pidText]
            || !int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || pid <= 0)
        {
            stderr.WriteLine("usage: engram maintenance claim --pid <pid>");
            return 1;
        }

        var home = EngramHome.ResolveFromProcess(homePath);

        // The handle is deliberately not disposed: disposing deletes the lock, and the holder is
        // the chain's shell, whose exit is what releases it.
        var (claimed, holder) = IndexLock.TryClaimNamed(home, LockName, DateTimeOffset.UtcNow, pid);
        if (claimed is null)
        {
            stdout.WriteLine(holder is null
                ? "another maintenance chain holds the lock"
                : $"maintenance chain pid {holder.Pid} holds the lock");
            return 1;
        }

        return 0;
    }
}
