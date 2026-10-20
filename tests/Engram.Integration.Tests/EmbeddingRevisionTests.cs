using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

public class EmbeddingRevisionTests
{
    private static string? Revision(SqliteConnection connection) =>
        EngramDatabase.ReadMeta(connection, "embedding_revision");

    [Theory]
    [InlineData("UPDATE fact SET valid_to = 2 WHERE id = $id;", true)]
    [InlineData("DELETE FROM fact WHERE id = $id;", true)]
    [InlineData("UPDATE fact SET details = 'details only' WHERE id = $id;", false)]
    [InlineData("UPDATE fact SET valid_to = valid_to WHERE id = $id;", false)]
    public void FactChanges_AdvanceTheRevisionWithoutLoadingVec0(string sql, bool changesRevision)
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var empty = Revision(connection);
        Assert.NotNull(empty);
        var id = FactStore.Remember(connection,
            new FactWrite("/test/belief", "concept", "states", "a belief", "project", "stated"),
            DateTimeOffset.UnixEpoch.AddSeconds(1)).FactId;
        var inserted = Revision(connection);
        Assert.NotEqual(empty, inserted);

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        Assert.Equal(changesRevision, inserted != Revision(connection));
    }

    [Fact]
    public void RolledBackFactChanges_DoNotAdvanceTheRevision()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var id = FactStore.Remember(connection,
            new FactWrite("/test/belief", "concept", "states", "a belief", "project", "stated"),
            DateTimeOffset.UnixEpoch.AddSeconds(1)).FactId;
        var before = Revision(connection);
        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE fact SET valid_to = 2 WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
            Assert.NotEqual(before, Revision(connection));
        }
        Assert.Equal(before, Revision(connection));
    }

    [Fact]
    public void FactWrites_WithAnExistingVectorTable_DoNotRequireVec0OnTheWriterConnection()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, new EmbeddingSpace("test-embedder", 4));
        using var writer = EngramDatabase.Open(sandbox.Home.DatabasePath, pooling: false);
        using var unavailable = writer.CreateCommand();
        unavailable.CommandText = "SELECT vec_version();";
        Assert.Throws<SqliteException>(() => unavailable.ExecuteScalar());
        var before = Revision(writer);

        FactStore.Remember(writer,
            new FactWrite("/test/belief", "concept", "states", "a belief", "project", "stated"),
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.NotEqual(before, Revision(writer));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("retire")]
    [InlineData("clear")]
    [InlineData("drop")]
    [InlineData("prune")]
    [InlineData("reconcile")]
    public void VectorMutations_AdvanceTheRevision(string operation)
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, new EmbeddingSpace("test-embedder", 4));
        var id = operation == "prune"
            ? sandbox.AddEdgeFact("caller", "calls", "callee")
            : sandbox.AddFact("belief", "a belief");
        VectorIndex.Write(sandbox.Connection, null, id, [1f, 0f, 0f, 0f]);
        if (operation == "reconcile") sandbox.Close(id);
        var before = Revision(sandbox.Connection);
        switch (operation)
        {
            case "write": VectorIndex.Write(sandbox.Connection, null, id, [0f, 1f, 0f, 0f]); break;
            case "retire": VectorIndex.Retire(sandbox.Connection, null, id); break;
            case "clear": VectorIndex.Clear(sandbox.Connection); break;
            case "drop": VectorIndex.Drop(sandbox.Connection); break;
            case "prune": VectorIndex.PruneIneligible(sandbox.Connection); break;
            case "reconcile": VectorIndex.Reconcile(sandbox.Connection); break;
        }
        Assert.NotEqual(before, Revision(sandbox.Connection));
    }

    [Fact]
    public void RolledBackVectorChanges_DoNotAdvanceTheRevision()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, new EmbeddingSpace("test-embedder", 4));
        var id = sandbox.AddFact("belief", "a belief");
        VectorIndex.Write(sandbox.Connection, null, id, [1f, 0f, 0f, 0f]);
        var before = Revision(sandbox.Connection);
        using (var transaction = EngramDatabase.BeginWrite(sandbox.Connection))
        {
            VectorIndex.Clear(sandbox.Connection, transaction);
            Assert.NotEqual(before, Revision(sandbox.Connection));
        }
        Assert.Equal(before, Revision(sandbox.Connection));
        Assert.Equal(1, VectorIndex.Count(sandbox.Connection));
        VectorIndex.Reconcile(sandbox.Connection);
        Assert.Equal(before, Revision(sandbox.Connection));
    }

    [Fact]
    public void AVersion17Store_GainsTheMarkerAndTriggersWithAPreMigrationSnapshot()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var freshHome = new SandboxHome(initialize: false);
        using (var old = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            using var downgrade = old.CreateCommand();
            downgrade.CommandText =
                """
                DROP TRIGGER fact_embedding_insert;
                DROP TRIGGER fact_embedding_update;
                DROP TRIGGER fact_embedding_delete;
                DELETE FROM schema_meta WHERE key = 'embedding_revision';
                UPDATE schema_meta SET value = '17' WHERE key = 'schema_version';
                """;
            downgrade.ExecuteNonQuery();
        }
        using var migrated = EngramDatabase.OpenInitialized(sandbox.Home);
        using var fresh = EngramDatabase.OpenInitialized(freshHome.Home);
        Assert.Equal(EngramDatabase.SchemaVersion, EngramDatabase.ReadSchemaVersion(migrated));
        Assert.Equal(32, Revision(migrated)?.Length);
        Assert.Equal(TriggerSql(fresh), TriggerSql(migrated));
        var snapshot = Assert.Single(BackupStore.List(sandbox.Home));
        Assert.Equal(17, snapshot.SchemaVersion);
    }

    private static string TriggerSql(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name LIKE 'fact_embedding_%' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var sql = new List<string>();
        while (reader.Read())
        {
            sql.Add(string.Join(' ', reader.GetString(0).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        }
        Assert.Equal(3, sql.Count);
        return string.Join('\n', sql);
    }
}
