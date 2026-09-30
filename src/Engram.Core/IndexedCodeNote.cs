using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// What <c>engram_expand</c> says about a fact the code indexer wrote. Such a fact is a ~60-token
/// gist of one symbol, file or section, so expanding it has nothing further to reveal — the useful
/// answer is where the real thing lives and how to get its structure.
/// </summary>
public static class IndexedCodeNote
{
    /// <summary>
    /// Only an indexer-written fact is a gist. A <c>code</c>-scope fact recorded through revise is
    /// authored truth (<c>Regenerable</c> false) and must not be described as one.
    /// </summary>
    public static string? LocationOf(StoredFact fact) =>
        fact is { Scope: "code", Regenerable: true } ? CodePaths.LocationText(fact.SubjectPath) : null;

    /// <summary>Null unless <paramref name="fact"/> is an indexed code fact with a location.</summary>
    public static string? Build(SqliteConnection connection, StoredFact fact)
    {
        if (CodePaths.LocationOf(fact.SubjectPath) is not var (repo, relative) || LocationOf(fact) is null)
        {
            return null;
        }

        var note = new System.Text.StringBuilder();
        note.Append("Indexed code: ").Append(repo).Append(':').Append(relative)
            .Append(" (").Append(fact.SubjectPath).Append(")\n");

        var freshness = FileFreshness.Check(connection, fact.SubjectPath);
        if (freshness.File is not null)
        {
            note.Append("Read it at ").Append(freshness.File)
                .Append(freshness.IsWorthReporting ? $" ({freshness.Label})" : string.Empty).Append(".\n");
        }

        note.Append("Engram keeps only this indexed gist (~60 tokens), not the source — "
            + "there is nothing more to expand.");

        // The entity's own kind decides: '#' introduces symbols and markdown sections alike.
        switch (KindOf(connection, fact.SubjectId))
        {
            case "symbol":
                note.Append("\nStructure: engram_navigate \"").Append(fact.SubjectName)
                    .Append("\" defined_at | members | callers, repo \"").Append(repo).Append("\".");
                break;
            case "file":
                note.Append("\nStructure: engram_navigate \"").Append(relative)
                    .Append("\" imports, repo \"").Append(repo).Append("\".");
                break;
        }

        return note.ToString();
    }

    private static string? KindOf(SqliteConnection connection, long entityId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind FROM entity WHERE id = $id;";
        command.Parameters.AddWithValue("$id", entityId);
        return command.ExecuteScalar() as string;
    }
}
