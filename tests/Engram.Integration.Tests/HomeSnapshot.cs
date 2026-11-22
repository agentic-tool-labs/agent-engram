using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>Every file under a home, by relative path, size and mtime — enough to catch a write.</summary>
/// <remarks>
/// Pooled connections are released first, because an in-process snapshot otherwise records pool
/// state rather than writes: a pooled handle keeps <c>engram.db-wal</c> (0 B) and
/// <c>engram.db-shm</c> (32768 B) on disk, and anything that closes it between two snapshots — a
/// <c>ClearAllPools</c> in another test, the pool's own idle pruning — deletes them, so the next
/// open recreates them and the home "gains" two files nothing wrote. Releasing closes the last
/// handle, which checkpoints any frames into <c>engram.db</c> and removes both, so a real write
/// still shows as a changed <c>engram.db</c>. The end-to-end guards never see this: their process
/// has exited before they look.
/// </remarks>
internal static class HomeSnapshot
{
    public static Dictionary<string, (long Length, DateTime Written)> Take(string root)
    {
        foreach (var database in Directory.EnumerateFiles(root, "*.db", SearchOption.AllDirectories))
        {
            EngramDatabase.ReleasePooledConnections(database);
        }

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                path => (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)));
    }
}
