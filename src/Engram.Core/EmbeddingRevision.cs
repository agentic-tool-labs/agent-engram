using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>A single-statement snapshot of the state an empty embedding pass verified.</summary>
internal sealed record EmbeddingRevision(
    string Revision,
    string Model,
    string Dimensions,
    string Input,
    long SchemaVersion)
{
    // Random revisions, rather than counters, cannot repeat after restoring a backup and
    // writing a different history with the same number of changes.
    internal static void Invalidate(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE schema_meta SET value = lower(hex(randomblob(16))) WHERE key = 'embedding_revision';";
        command.ExecuteNonQuery();
    }

    internal static EmbeddingRevision? Read(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.value, m.value, d.value, i.value, s.schema_version
            FROM schema_meta r
            JOIN schema_meta m ON m.key = 'embedding_model'
            JOIN schema_meta d ON d.key = 'embedding_dimensions'
            JOIN schema_meta i ON i.key = 'embedding_input'
            CROSS JOIN pragma_schema_version s
            WHERE r.key = 'embedding_revision' AND length(r.value) = 32
              AND m.value IS NOT NULL AND d.value IS NOT NULL AND i.value IS NOT NULL
              AND EXISTS (SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'fact_vec')
              AND (SELECT COUNT(*) FROM sqlite_schema WHERE type = 'trigger'
                   AND name IN ('fact_embedding_insert', 'fact_embedding_update', 'fact_embedding_delete')) = 3;
            """;

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new EmbeddingRevision(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4))
            : null;
    }
}
