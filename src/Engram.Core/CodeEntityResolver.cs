using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// Maps a path on disk to the entity path the indexer stored its facts under.
/// </summary>
/// <remarks>
/// Reads the repo registry and the enrollment decision and nothing else: no subprocess and no file
/// access beyond resolving symlinks in the path itself, because callers sit on a per-edit hot path.
/// The registry's <c>disk_path</c> is the checkout root git reported when the repo was last indexed,
/// so the path is canonicalised the same way before comparing.
///
/// <para>The domain is every repo the indexer has written, not only the enrolled ones: a manual
/// <c>engram index --apply</c> writes a repo's file entities without consulting enrollment (only the
/// <c>--auto</c> path does), so an enrollment filter here would be narrower than the data and would
/// answer "nothing known" about a file whose facts are in the store. A checkout whose registry row
/// is detached resolves to nothing.</para>
/// </remarks>
public static class CodeEntityResolver
{
    public static (string EntityPath, string RepoPath)? Resolve(SqliteConnection connection, string absolutePath)
    {
        var canonical = PathCanonicalizer.Canonical(absolutePath);

        string? bestRepo = null;
        string? bestRoot = null;

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT repo_path, disk_path FROM repo_registry "
                + "WHERE disk_path IS NOT NULL AND detached_at IS NULL;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var root = reader.GetString(1).TrimEnd(Path.DirectorySeparatorChar);
                var inside = canonical.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                if (inside && (bestRoot is null || root.Length > bestRoot.Length))
                {
                    bestRepo = reader.GetString(0);
                    bestRoot = root;
                }
            }
        }

        if (bestRepo is null || bestRoot is null)
        {
            return null;
        }

        var relative = canonical[(bestRoot.Length + 1)..];
        return (CodePaths.ForFile(bestRepo, relative), bestRepo);
    }
}
