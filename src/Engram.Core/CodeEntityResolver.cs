using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// Maps a path on disk to the entity path the indexer stored its facts under.
/// </summary>
/// <remarks>
/// Reads the repo registry and the enrollment decision and nothing else: no subprocess and no file
/// access beyond resolving symlinks in the path itself, because callers sit on a per-edit hot path.
/// The registry's <c>disk_path</c> is the checkout root git reported when the repo was last indexed,
/// so the path is canonicalised the same way before comparing. A repo that was never enrolled, or
/// whose checkout was detached, resolves to nothing — its facts, if any, are not served.
/// </remarks>
public static class CodeEntityResolver
{
    public static (string EntityPath, string RepoPath)? Resolve(SqliteConnection connection, string absolutePath)
    {
        var canonical = PathCanonicalizer.Canonical(absolutePath);

        string? bestRepo = null;
        string? bestIdentity = null;
        string? bestRoot = null;

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT repo_path, identity, disk_path FROM repo_registry "
                + "WHERE disk_path IS NOT NULL AND detached_at IS NULL;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var root = reader.GetString(2).TrimEnd(Path.DirectorySeparatorChar);
                var inside = canonical.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                if (inside && (bestRoot is null || root.Length > bestRoot.Length))
                {
                    bestRepo = reader.GetString(0);
                    bestIdentity = reader.GetString(1);
                    bestRoot = root;
                }
            }
        }

        if (bestRepo is null || bestIdentity is null || bestRoot is null
            || RepoEnrollment.Get(connection, bestIdentity)?.State != RepoEnrollmentState.Enrolled)
        {
            return null;
        }

        var relative = canonical[(bestRoot.Length + 1)..];
        return (CodePaths.ForFile(bestRepo, relative), bestRepo);
    }
}
