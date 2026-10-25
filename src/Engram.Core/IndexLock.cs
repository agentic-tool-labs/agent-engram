using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Engram.Core;

/// <param name="Pid">
/// The claiming process's id, stored separately from <paramref name="StartToken"/> because the
/// token is opaque (<see cref="ProcessStartToken"/>) and cannot be reverse-mapped to a pid for
/// <see cref="ProcessStartToken.ForPid"/> — reaping needs the pid to re-derive the live token to
/// compare against.
/// </param>
/// <param name="StartToken">
/// <see cref="ProcessStartToken.ForSelf"/> at claim time — pid plus the kernel's start token for
/// that pid, the one thing a recycled pid cannot forge (D42). Null on a platform where the token
/// could not be read; such a claim can never be confirmed live and is always eligible for reaping.
/// </param>
public sealed record IndexLockRecord(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("identity")] string Identity,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("start_token")] string? StartToken);

[JsonSerializable(typeof(IndexLockRecord))]
internal sealed partial class IndexLockJsonContext : JsonSerializerContext;

/// <summary>
/// A per-identity, cross-process mutual-exclusion lock over one repo's index run (spec §6.4).
/// </summary>
/// <remarks>
/// <para>Not a schema change — one file per identity under <see cref="EngramHome.IndexLockDir"/>,
/// claimed with <see cref="FileMode.CreateNew"/> (<c>O_EXCL</c>), which is atomic across processes
/// without a transaction. There is no timeout-based reaping and no tolerance window: a lock naming
/// a still-live process is never stolen from, matching D42's rule that a process-identity
/// comparison may not be softened with a window. A lock naming a dead one self-releases the moment
/// another claimant looks at it — nobody sweeps for it in the background.</para>
///
/// <para>Contention never waits. <see cref="TryClaim"/> returns immediately either way: the held
/// lock, or <see langword="null"/> plus whoever currently holds it, for the caller to report on its
/// own terms (commanded vs. ambient, §6.4's table).</para>
/// </remarks>
public sealed class IndexLock : IDisposable
{
    private readonly string _path;
    private bool _released;

    private IndexLock(string path)
    {
        _path = path;
    }

    /// <summary>
    /// Where the lock for <paramref name="identity"/> lives. The identity is hashed rather than
    /// used verbatim because it is an absolute path (<see cref="CodeIndexer.ResolveIdentity"/>) and
    /// may contain characters a filename cannot.
    /// </summary>
    public static string PathFor(EngramHome home, string identity) =>
        Path.Combine(
            home.IndexLockDir,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".lock");

    /// <summary>
    /// Claims the lock for <paramref name="identity"/>. On contention, reads the lock file: an
    /// unparseable one, or one whose <see cref="ProcessStartToken"/> no longer matches the recorded
    /// pid, is reaped and the claim is retried exactly once — never more, and never against a
    /// holder confirmed live.
    /// </summary>
    /// <returns>
    /// The held lock, or <see langword="null"/> plus the record of whoever holds it — best-effort;
    /// <c>BlockedBy</c> may itself be <see langword="null"/> if the file could not be read at the
    /// moment of the second look.
    /// </returns>
    public static (IndexLock? Lock, IndexLockRecord? BlockedBy) TryClaim(
        EngramHome home, string identity, DateTimeOffset now) =>
        TryClaimAt(home, PathFor(home, identity), identity, now, Environment.ProcessId);

    /// <summary>Where the home-wide lock called <paramref name="name"/> lives.</summary>
    /// <remarks>
    /// Kept verbatim rather than hashed: a name is a short word chosen in code, and a repo identity
    /// is an absolute path, so the two can never spell the same file.
    /// </remarks>
    public static string PathForName(EngramHome home, string name) =>
        Path.Combine(home.IndexLockDir, name + ".lock");

    /// <summary>
    /// Claims the home-wide lock <paramref name="name"/> on behalf of <paramref name="holderPid"/>,
    /// which need not be this process. The record names the holder, so the lock is released by that
    /// process exiting and needs no explicit release — the caller may drop the returned handle.
    /// </summary>
    public static (IndexLock? Lock, IndexLockRecord? BlockedBy) TryClaimNamed(
        EngramHome home, string name, DateTimeOffset now, int holderPid) =>
        TryClaimAt(home, PathForName(home, name), name, now, holderPid);

    /// <remarks>
    /// The read, reap and create run under a guard file held exclusively for the length of the
    /// decision and released by the claimant's exit however it ends. Without it, claimants arriving
    /// in the same instant both won: one failed to read the other's half-written lock and reaped it
    /// as unreadable, or both read one stale record and each reaped the other's fresh claim.
    /// Measured: two chains forked in the same millisecond both ran their jobs in 3 runs of 4.
    /// A claimant that finds the guard taken reports contention; whoever holds it is deciding.
    /// </remarks>
    private static (IndexLock? Lock, IndexLockRecord? BlockedBy) TryClaimAt(
        EngramHome home, string path, string identity, DateTimeOffset now, int holderPid)
    {
        Directory.CreateDirectory(home.IndexLockDir);

        FileStream guard;
        try
        {
            guard = new FileStream(path + ".guard", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return (null, ReadRecord(path));
        }

        using (guard)
        {
            return TryClaimGuarded(path, identity, now, holderPid);
        }
    }

    private static (IndexLock? Lock, IndexLockRecord? BlockedBy) TryClaimGuarded(
        string path, string identity, DateTimeOffset now, int holderPid)
    {
        if (TryCreateAndWrite(path, identity, now, holderPid))
        {
            return (new IndexLock(path), null);
        }

        var holder = ReadRecord(path);
        if (IsLive(holder))
        {
            return (null, holder);
        }

        if (TryDelete(path) && TryCreateAndWrite(path, identity, now, holderPid))
        {
            return (new IndexLock(path), null);
        }

        return (null, ReadRecord(path));
    }

    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        TryDelete(_path);
    }

    private static bool IsLive(IndexLockRecord? holder)
    {
        if (holder is null)
        {
            return false;
        }

        var liveToken = ProcessStartToken.ForPid(holder.Pid);
        return liveToken is not null && string.Equals(liveToken, holder.StartToken, StringComparison.Ordinal);
    }

    private static bool TryCreateAndWrite(string path, string identity, DateTimeOffset now, int holderPid)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException)
        {
            return false;
        }

        using (stream)
        {
            var record = new IndexLockRecord(holderPid, identity, now, ProcessStartToken.ForPid(holderPid));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, IndexLockJsonContext.Default.IndexLockRecord);
            stream.Write(bytes);
        }

        return true;
    }

    private static IndexLockRecord? ReadRecord(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, IndexLockJsonContext.Default.IndexLockRecord);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
