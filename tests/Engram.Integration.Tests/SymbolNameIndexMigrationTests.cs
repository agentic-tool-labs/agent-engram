using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

public class SymbolNameIndexMigrationTests
{
    [Fact]
    public void AVersion18Store_GainsBothPartialNameIndexesAndAPreMigrationSnapshot()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using (var old = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            using var command = old.CreateCommand();
            command.CommandText =
                """
                INSERT INTO entity(path,kind,name,created_at) VALUES('/projects/p/code/r/a.cs#Widget','symbol','Widget',0);
                DROP INDEX ix_symbol_name;
                DROP INDEX ix_symbol_name_nocase;
                UPDATE schema_meta SET value = '18' WHERE key = 'schema_version';
                """;
            command.ExecuteNonQuery();
        }

        using var migrated = EngramDatabase.OpenInitialized(sandbox.Home);
        using var freshHome = new SandboxHome(initialize: false);
        using var fresh = EngramDatabase.OpenInitialized(freshHome.Home);
        Assert.Equal(19, EngramDatabase.ReadSchemaVersion(migrated));
        foreach (var index in new[] { "ix_symbol_name", "ix_symbol_name_nocase" })
        {
            Assert.Equal(IndexShape(fresh, index), IndexShape(migrated, index));
            Assert.Contains(index == "ix_symbol_name" ? "name:BINARY" : "name:NOCASE", IndexShape(migrated, index), StringComparison.Ordinal);
        }
        Assert.Equal(SymbolMatchTier.Exact, Assert.Single(SymbolResolver.Resolve(migrated, "Widget", 20)).Tier);
        Assert.Equal(SymbolMatchTier.CaseInsensitive, Assert.Single(SymbolResolver.Resolve(migrated, "WIDGET", 20)).Tier);
        Assert.Equal(18, Assert.Single(BackupStore.List(sandbox.Home)).SchemaVersion);
    }

    private static string IndexShape(SqliteConnection connection, string index)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name = $name;";
        command.Parameters.AddWithValue("$name", index);
        var sql = Assert.IsType<string>(command.ExecuteScalar());
        Assert.EndsWith("WHERE kind = 'symbol'", sql, StringComparison.Ordinal);
        command.CommandText = "SELECT name, coll FROM pragma_index_xinfo($name) WHERE key = 1 ORDER BY seqno;";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(0) + ":" + reader.GetString(1));
        return string.Join(',', columns);
    }
}
