using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Schema v20 (compact-quadratic-deletes.md §3). The fixture drops both indexes before rolling
/// the version back, because <c>CREATE INDEX IF NOT EXISTS</c> on a current-schema store would
/// no-op and pass with the migration step deleted (D60).
/// </summary>
public class ForeignKeyIndexMigrationTests
{
    private static readonly string[] Indexes = ["ix_fact_superseded_by", "ix_fact_object_ref"];

    [Fact]
    public void AVersion19Store_GainsBothForeignKeyIndexesAfterAPreMigrationSnapshot()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using (var old = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            using var command = old.CreateCommand();
            command.CommandText =
                """
                DROP INDEX ix_fact_superseded_by;
                DROP INDEX ix_fact_object_ref;
                UPDATE schema_meta SET value = '19' WHERE key = 'schema_version';
                """;
            command.ExecuteNonQuery();
            foreach (var index in Indexes)
            {
                Assert.Null(IndexSql(old, index));
            }
        }

        using var migrated = EngramDatabase.OpenInitialized(sandbox.Home);
        using var freshHome = new SandboxHome(initialize: false);
        using var fresh = EngramDatabase.OpenInitialized(freshHome.Home);

        Assert.Equal(20, EngramDatabase.ReadSchemaVersion(migrated));
        foreach (var index in Indexes)
        {
            Assert.NotNull(IndexSql(fresh, index));
            Assert.True(IndexSql(migrated, index) is not null, $"{index} missing after the v20 migration");
            Assert.Equal(Normalize(IndexSql(fresh, index)!), Normalize(IndexSql(migrated, index)!));
        }

        Assert.Equal(19, Assert.Single(BackupStore.List(sandbox.Home)).SchemaVersion);

        // Recall's indexes are left exactly as they were (F3).
        Assert.Equal(
            "CREATE INDEX ix_fact_object ON fact(object_id, predicate) WHERE valid_to IS NULL AND object_id IS NOT NULL",
            Normalize(IndexSql(migrated, "ix_fact_object")!));
        Assert.Equal("CREATE INDEX ix_fact_thread ON fact(subject_id, predicate)", Normalize(IndexSql(migrated, "ix_fact_thread")!));
    }

    private static string? IndexSql(SqliteConnection connection, string index)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type = 'index' AND name = $name;";
        command.Parameters.AddWithValue("$name", index);
        return command.ExecuteScalar() as string;
    }

    private static string Normalize(string sql) =>
        string.Join(' ', sql.Replace("IF NOT EXISTS ", string.Empty, StringComparison.Ordinal)
            .Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));
}
