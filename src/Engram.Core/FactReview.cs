using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>One <c>fact_review</c> row.</summary>
public sealed record ReviewEntry(long FactId, long ReviewAfter, long SetAt);

/// <summary>
/// The review-due marker (docs/memory-expansion/04-lifecycle-spec.md): an explicit,
/// caller-supplied reminder date on a fact. Side-table, mirroring
/// <see cref="FactSyncRequests"/>'s pattern — never a column on <c>fact</c> (D8). Unlike
/// <c>fact_sync_request</c>, a fact may only carry one review date at a time, so setting a new
/// one replaces the row rather than accumulating.
/// </summary>
public static class FactReview
{
    /// <summary>Sets or replaces the review date for <paramref name="factId"/>.</summary>
    /// <param name="keepEarlier">
    /// Leave an existing date alone when it is already at or before <paramref name="reviewAfter"/>,
    /// decided in the upsert itself rather than by a read first (D106).
    /// </param>
    public static void Set(
        SqliteConnection connection, SqliteTransaction? transaction, long factId, long reviewAfter, long setAt,
        bool keepEarlier = false)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO fact_review (fact_id, review_after, set_at) VALUES ($factId, $reviewAfter, $setAt)
              ON CONFLICT(fact_id) DO UPDATE SET review_after = excluded.review_after, set_at = excluded.set_at
              WHERE $keepEarlier = 0 OR fact_review.review_after > excluded.review_after;
            """;
        command.Parameters.AddWithValue("$factId", factId);
        command.Parameters.AddWithValue("$reviewAfter", reviewAfter);
        command.Parameters.AddWithValue("$setAt", setAt);
        command.Parameters.AddWithValue("$keepEarlier", keepEarlier ? 1 : 0);
        command.ExecuteNonQuery();
    }

    /// <summary>Clears the review marker for <paramref name="factId"/>. Returns whether a row was removed.</summary>
    public static bool Clear(SqliteConnection connection, SqliteTransaction? transaction, long factId)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM fact_review WHERE fact_id = $factId;";
        command.Parameters.AddWithValue("$factId", factId);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Every review marker, due soonest first, joined against its fact's live body.</summary>
    public static IReadOnlyList<(ReviewEntry Entry, string Body)> ListLive(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.fact_id, r.review_after, r.set_at, f.body
              FROM fact_review r
              JOIN fact f ON f.id = r.fact_id
             WHERE f.valid_to IS NULL
             ORDER BY r.review_after;
            """;

        var rows = new List<(ReviewEntry, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((
                new ReviewEntry(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)),
                reader.GetString(3)));
        }

        return rows;
    }

    /// <summary>
    /// Live facts whose review date has passed. Shares the "join against live fact" shape with
    /// <see cref="ListLive"/> rather than a second query, since the only difference is the
    /// <c>review_after &lt;= $now</c> filter and the count. <c>CROSS JOIN</c> pins the loop
    /// order: driven from the small <c>fact_review</c> table with a <c>fact</c> primary-key probe
    /// per row, instead of the planner's choice of scanning every live fact through
    /// <c>ix_fact_scope</c> — 16 ms against 0.01 ms on a 621,904-live-fact snapshot, same count.
    /// </summary>
    public static int CountDue(SqliteConnection connection, long nowUnixSeconds)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
              FROM fact_review r
              CROSS JOIN fact f ON f.id = r.fact_id
             WHERE f.valid_to IS NULL AND r.review_after <= $now;
            """;
        command.Parameters.AddWithValue("$now", nowUnixSeconds);
        return (int)(long)command.ExecuteScalar()!;
    }
}
