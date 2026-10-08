using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>One live invariant, with the file it is about.</summary>
public sealed record Invariant(long Id, string SubjectPath, string FilePath, string Body, long ValidFrom);

/// <summary>
/// A rule about one file that a person marked load-bearing, stored as an ordinary fact so that
/// the existing live-under-file read (<c>path = file OR path LIKE file#…</c>) finds it with no new
/// query. Authored only by <c>engram invariant</c>; the indexer never writes or closes one.
/// </summary>
/// <remarks>
/// Not regenerable, which is what keeps every indexer pass away from it: file deletion and a full
/// reindex close only regenerable facts, so a deleted file's invariants stay live and simply never
/// match a path again. Each statement is its own <c>#invariant-&lt;fingerprint&gt;</c> subject
/// because <c>ux_fact_live</c> allows one live fact per subject and predicate, and a file can carry
/// several rules.
/// </remarks>
public static class InvariantFacts
{
    public const string Predicate = "invariant";
    public const string Kind = "convention";
    public const string Scope = "project";
    public const string LearnedVia = "stated";
    public const string Evidence = "stated by the user via engram invariant";

    /// <summary>Between the file's entity path and the statement fingerprint.</summary>
    public const string Marker = "#invariant-";

    /// <summary>Same bound as a directive, and for the same reason: it is what is reasonable to show per edit.</summary>
    public const int MaxStatementTokens = DirectiveFacts.MaxDirectiveTokens;

    public static string PathFor(string filePath, string statement) =>
        filePath + Marker + FactStore.Fingerprint(statement)[..8];

    /// <summary>
    /// Records <paramref name="statement"/> against the file, or returns null when that exact
    /// statement is already live on it. Not left to <see cref="FactStore.Remember"/>, which would
    /// close the incumbent and insert a duplicate.
    /// </summary>
    public static long? Add(SqliteConnection connection, string filePath, string statement, DateTimeOffset now)
    {
        var path = PathFor(filePath, statement);

        using var transaction = EngramDatabase.BeginWrite(connection);

        if (FactStore.FindLiveFactId(connection, transaction, path, Predicate) is not null)
        {
            transaction.Rollback();
            return null;
        }

        var result = FactStore.Remember(
            connection,
            transaction,
            new FactWrite(
                SubjectPath: path,
                SubjectKind: Kind,
                Predicate: Predicate,
                Body: statement,
                Scope: Scope,
                LearnedVia: LearnedVia,
                Evidence: Evidence,
                Regenerable: false),
            now);

        transaction.Commit();
        return result.FactId;
    }

    /// <summary>Live invariants on every file, or only on <paramref name="filePath"/> when given.</summary>
    public static IReadOnlyList<Invariant> ReadLive(SqliteConnection connection, string? filePath = null)
    {
        using var command = connection.CreateCommand();

        // A range over the denormalized path rather than LIKE: '-' is followed by '.' in
        // ordinal order, so [prefix, prefix with its last char bumped) is exactly the subtree.
        if (filePath is null)
        {
            command.CommandText =
                """
                SELECT id, path, body, valid_from FROM fact
                 WHERE predicate = $predicate AND valid_to IS NULL
                 ORDER BY path, valid_from;
                """;
        }
        else
        {
            command.CommandText =
                """
                SELECT id, path, body, valid_from FROM fact
                 WHERE predicate = $predicate AND valid_to IS NULL
                   AND path >= $low AND path < $high
                 ORDER BY path, valid_from;
                """;
            var prefix = filePath + Marker;
            command.Parameters.AddWithValue("$low", prefix);
            command.Parameters.AddWithValue("$high", prefix[..^1] + (char)(prefix[^1] + 1));
        }

        command.Parameters.AddWithValue("$predicate", Predicate);

        var invariants = new List<Invariant>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(1);
            var marker = path.IndexOf(Marker, StringComparison.Ordinal);
            if (marker < 0)
            {
                continue;
            }

            invariants.Add(new Invariant(reader.GetInt64(0), path, path[..marker], reader.GetString(2), reader.GetInt64(3)));
        }

        return invariants;
    }

    /// <summary>
    /// A fact id is a live invariant only when it carries the predicate and the marker, so a verb
    /// that takes an id can never close an arbitrary fact someone typed.
    /// </summary>
    public static bool TryReadLive(SqliteConnection connection, long factId, out StoredFact invariant)
    {
        var fact = FactStore.ReadById(connection, factId);

        if (fact is not null
            && fact.ValidTo is null
            && fact.Predicate == Predicate
            && fact.SubjectPath.Contains(Marker, StringComparison.Ordinal))
        {
            invariant = fact;
            return true;
        }

        invariant = null!;
        return false;
    }
}
