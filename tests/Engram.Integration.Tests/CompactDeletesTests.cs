using System.Text;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// compact-quadratic-deletes.md §4: the delete paths were rewritten for speed (F1–F3) and two
/// guards were added (F4 aliases, F5 side tables). These pin that only the guards change what
/// is removed, that authored truth is byte-identical across every apply, and that the speed
/// comes from indexes rather than from switching foreign keys off.
/// </summary>
[Collection(SqlitePoolCollection.Name)]
public class CompactDeletesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
    private const string Repo = "/projects/acme/code/api";

    private static long Code(SqliteConnection connection, string path, string body, string? objectPath = null) =>
        FactStore.Remember(
            connection,
            new FactWrite(
                path, "symbol", objectPath is null ? "declares" : "calls", body, "code", "observed",
                Regenerable: true, ObjectPath: objectPath, ObjectKind: objectPath is null ? null : "symbol"),
            T0).FactId;

    private static long Authored(SqliteConnection connection, string path, string body, string? objectPath = null) =>
        FactStore.Remember(
            connection,
            new FactWrite(
                path, "note", objectPath is null ? "states" : "uses", body, "project", "stated",
                ObjectPath: objectPath, ObjectKind: objectPath is null ? null : "symbol"),
            T0).FactId;

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static int Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (int)(long)command.ExecuteScalar()!;
    }

    private static void Move(SqliteConnection connection, string from, string to)
    {
        using var transaction = EngramDatabase.BeginWrite(connection);
        FactStore.MoveSubtree(connection, transaction, from, to, T0);
        transaction.Commit();
    }

    /// <summary>
    /// Code facts and entities of every shape the delete paths distinguish, with no alias and no
    /// side-table reference, so F4 and F5 protect nothing here.
    /// </summary>
    private static void SeedUnguarded(SqliteConnection connection)
    {
        Authored(connection, "/knowledge/testing/kestrel", "It binds all interfaces.");
        Authored(connection, "/knowledge/testing/kestrel", "It binds loopback only.");

        Code(connection, $"{Repo}/src/A.cs", "about the request pipeline");
        Code(connection, $"{Repo}/src/A.cs#Widget", "declared as internal class Widget");
        Code(connection, $"{Repo}/src/A.cs#Widget", "declared as public class Widget");
        Code(connection, $"{Repo}/src/A.cs#Gadget", "declared as class Gadget");
        Code(connection, $"{Repo}/src/B.cs#Caller", "calls Widget", objectPath: $"{Repo}/src/A.cs#Widget");
        Code(connection, $"{Repo}/src/B.cs#Lonely", "declared as class Lonely");
        Code(connection, "/projects/acme/code/api-v2/src/C.cs#Widget", "declared as class Widget");

        // An authored fact naming a code entity as its object keeps that entity.
        Authored(connection, "/knowledge/testing/importer", "The importer depends on Gadget.", objectPath: $"{Repo}/src/A.cs#Gadget");

        // A code fact revised into a belief: protected by the supersession edge.
        Code(connection, $"{Repo}/src/B.cs#Legacy", "declared as class Legacy");
        FactStore.Remember(
            connection,
            new FactWrite($"{Repo}/src/B.cs#Legacy", "symbol", "declares", "Kept for the v1 importer.", "code", "stated"),
            T0);

        Execute(connection,
            "INSERT INTO file_state (repo_path, path, blob_sha, lang, indexed_at) VALUES "
            + $"('{Repo}', 'src/A.cs', 'aaa', 'csharp', 0), ('{Repo}', 'src/B.cs', 'bbb', 'csharp', 0);");
        Execute(connection,
            "INSERT INTO repo_registry (repo_path, identity, disk_path, created_at) VALUES "
            + $"('{Repo}', 'github.com/acme/api', '/tmp/api', 0);");
    }

    /// <summary>
    /// The rows compact must never change, as text. Any table not listed here is regenerable or
    /// addressing (entity), which the per-row tests cover instead.
    /// </summary>
    private static string AuthoredDigest(SqliteConnection connection)
    {
        var sb = new StringBuilder();
        foreach (var sql in new[]
                 {
                     "SELECT * FROM fact WHERE regenerable = 0 ORDER BY id",
                     "SELECT s.* FROM supersession s WHERE EXISTS (SELECT 1 FROM fact f WHERE f.regenerable = 0 "
                         + "AND f.id IN (s.old_fact_id, s.new_fact_id)) ORDER BY s.old_fact_id",
                     "SELECT * FROM fact_relation ORDER BY id",
                     "SELECT * FROM fact_review ORDER BY fact_id",
                     "SELECT * FROM fact_sync_request ORDER BY fact_id",
                     "SELECT * FROM entity_alias ORDER BY alias, kind, entity_id",
                     "SELECT * FROM session ORDER BY id",
                 })
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            sb.Append(sql).Append('\n');
            while (reader.Read())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    sb.Append(reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).Append('|');
                }

                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// F4: a rename filed an alias against the entity, and the alias row cascades with it, so the
    /// entity stays even with no fact left. Its facts are still pruned. The unaliased entity in
    /// the same prefix is the nearest input that must still be deleted.
    /// </summary>
    [Fact]
    public void AnAliasedCodeEntity_WithNoFacts_SurvivesAPathCompact_AndKeepsItsAliases()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var renamed = Code(connection, $"{Repo}/src/Old.cs#Widget", "declared as class Widget");
        var plain = Code(connection, $"{Repo}/src/C.cs#Gadget", "declared as class Gadget");
        Move(connection, $"{Repo}/src/Old.cs", $"{Repo}/src/New.cs");

        var aliases = Scalar(connection, "SELECT count(*) FROM entity_alias;");
        var aliasedEntities = Scalar(connection, "SELECT count(DISTINCT entity_id) FROM entity_alias;");
        Assert.True(aliases > 0);

        var dryRun = StoreCompactor.Compact(connection, sandbox.Home, Repo, apply: false, T0);
        var report = StoreCompactor.Compact(connection, sandbox.Home, Repo, apply: true, T0);

        Assert.Equal(aliasedEntities, dryRun.EntitiesKeptForRenameHistory);
        Assert.Equal(aliasedEntities, report.EntitiesKeptForRenameHistory);
        Assert.Null(FactStore.ReadById(connection, renamed));
        Assert.Null(FactStore.ReadById(connection, plain));
        Assert.Equal(aliases, Scalar(connection, "SELECT count(*) FROM entity_alias;"));
        Assert.Equal(1, Scalar(connection, $"SELECT count(*) FROM entity WHERE path = '{Repo}/src/New.cs#Widget';"));
        Assert.Equal(0, Scalar(connection, $"SELECT count(*) FROM entity WHERE path = '{Repo}/src/C.cs#Gadget';"));
        Assert.Equal(0, Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public void ANonCodeEntity_UnderThePrefix_IsNeverDeleted()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Code(connection, $"{Repo}/src/A.cs#Widget", "declared as class Widget");
        Execute(connection, $"INSERT INTO entity (path, kind, name, created_at) VALUES ('{Repo}/notes', 'topic', 'notes', 0);");

        var report = StoreCompactor.Compact(connection, sandbox.Home, Repo, apply: true, T0);

        Assert.Equal(1, report.FactsPruned);
        Assert.Equal(1, Scalar(connection, $"SELECT count(*) FROM entity WHERE path = '{Repo}/notes';"));
        Assert.Equal(0, Scalar(connection, $"SELECT count(*) FROM entity WHERE path = '{Repo}/src/A.cs#Widget';"));
    }

    /// <summary>The object half of the split reference test, which F3's index serves.</summary>
    [Fact]
    public void AnAuthoredFact_WhoseObjectIsACodeEntity_KeepsTheFactAndTheEntity()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Code(connection, $"{Repo}/src/A.cs#Gadget", "declared as class Gadget");
        Code(connection, $"{Repo}/src/A.cs#Widget", "declared as class Widget");
        var belief = Authored(connection, "/knowledge/testing/importer", "The importer depends on Gadget.", objectPath: $"{Repo}/src/A.cs#Gadget");

        StoreCompactor.Compact(connection, sandbox.Home, Repo, apply: true, T0);

        Assert.NotNull(FactStore.ReadById(connection, belief));
        Assert.Equal(1, Scalar(connection, $"SELECT count(*) FROM entity WHERE path = '{Repo}/src/A.cs#Gadget';"));
        Assert.Equal(0, Scalar(connection, $"SELECT count(*) FROM entity WHERE path = '{Repo}/src/A.cs#Widget';"));
    }

    /// <summary>
    /// Without a path compact prunes closed facts and nothing else, on a store that has no
    /// vector table at all.
    /// </summary>
    [Fact]
    public void CompactWithoutAPath_DeletesNoEntityFileStateOrRegistryRow()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedUnguarded(connection);
        Assert.False(VectorIndex.Exists(connection));

        var entities = Scalar(connection, "SELECT count(*) FROM entity;");
        var report = StoreCompactor.Compact(connection, sandbox.Home, path: null, apply: true, T0);

        Assert.True(report.ClosedPruned > 0);
        Assert.Equal(0, report.EntitiesRemoved);
        Assert.Equal(0, report.EntitiesKeptForRenameHistory);
        Assert.Equal(entities, Scalar(connection, "SELECT count(*) FROM entity;"));
        Assert.Equal(2, Scalar(connection, "SELECT count(*) FROM file_state;"));
        Assert.Equal(1, Scalar(connection, "SELECT count(*) FROM repo_registry;"));
    }

    /// <summary>
    /// C1 and C3: every table authored truth lives in reads the same after compact and after a
    /// path compact, and no foreign key is left dangling.
    /// </summary>
    [Fact]
    public void AuthoredTruth_IsUnchanged_ByEveryApply_AndNoForeignKeyDangles()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedUnguarded(connection);

        var referenced = Code(connection, $"{Repo}/src/D.cs#Judged", "declared as class Judged");
        var belief = Authored(connection, "/knowledge/testing/judged", "Judged is load-bearing.");
        Execute(connection, $"INSERT INTO fact_relation (fact_id, related_id, relation, judged_at) VALUES ({belief}, {referenced}, 'scoped', 0);");
        Execute(connection, $"INSERT INTO fact_review (fact_id, review_after, set_at) VALUES ({belief}, 0, 0);");
        Execute(connection, $"INSERT INTO fact_sync_request (fact_id, requested_at) VALUES ({referenced}, 0);");
        Execute(connection, $"INSERT INTO session (external_id, host, repo_path, started_at) VALUES ('s-1', 'cli', '{Repo}', 0);");
        Code(connection, $"{Repo}/src/Old.cs#Renamed", "declared as class Renamed");
        Move(connection, $"{Repo}/src/Old.cs", $"{Repo}/src/New.cs");

        var before = AuthoredDigest(connection);
        Assert.Contains("|scoped|", before, StringComparison.Ordinal);

        StoreCompactor.Compact(connection, sandbox.Home, path: null, apply: true, T0);
        Assert.Equal(0, Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(before, AuthoredDigest(connection));

        StoreCompactor.Compact(connection, sandbox.Home, Repo, apply: true, T0);
        Assert.Equal(0, Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(before, AuthoredDigest(connection));
    }

    /// <summary>
    /// C2: on a store where F4 and F5 protect nothing, the split reference test selects exactly
    /// the entities the OR form did. The OR form below is the SQL shipped at 27dcf11, frozen.
    /// </summary>
    [Fact]
    public void TheSplitReferenceTest_SelectsTheSameEntitiesAsTheOrForm()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedUnguarded(connection);

        var report = StoreCompactor.Compact(connection, sandbox.Home, Repo, apply: false, T0);

        Execute(connection,
            ("CREATE TEMP TABLE compact_target AS SELECT id FROM fact WHERE regenerable = 1 "
             + "AND (fact.path = $p OR (substr(fact.path, 1, length($p)) = $p AND substr(fact.path, length($p) + 1, 1) IN ('/', '#'))) "
             + "AND NOT EXISTS (SELECT 1 FROM supersession s JOIN fact other ON other.id = "
             + "CASE WHEN s.old_fact_id = fact.id THEN s.new_fact_id ELSE s.old_fact_id END "
             + "WHERE (s.old_fact_id = fact.id OR s.new_fact_id = fact.id) AND other.regenerable = 0);")
            .Replace("$p", $"'{Repo}'", StringComparison.Ordinal));
        try
        {
            var orForm = Scalar(connection,
                ("SELECT count(*) FROM entity WHERE kind IN ('repo', 'module', 'file', 'symbol', 'section') "
                 + "AND (entity.path = $p OR (substr(entity.path, 1, length($p)) = $p AND substr(entity.path, length($p) + 1, 1) IN ('/', '#'))) "
                 + "AND NOT EXISTS (SELECT 1 FROM fact WHERE (fact.subject_id = entity.id OR fact.object_id = entity.id) "
                 + "AND fact.id NOT IN (SELECT id FROM temp.compact_target));")
                .Replace("$p", $"'{Repo}'", StringComparison.Ordinal));
            var targets = Scalar(connection, "SELECT count(*) FROM temp.compact_target;");

            Assert.True(orForm > 0);
            Assert.Equal(orForm, report.EntitiesRemoved);
            Assert.Equal(targets, report.FactsPruned);
            Assert.Equal(0, report.EntitiesKeptForRenameHistory);
        }
        finally
        {
            Execute(connection, "DROP TABLE temp.compact_target;");
        }
    }

    /// <summary>
    /// C5: the speed must come from indexes. A temp trigger reads the pragma on the compactor's
    /// own connection at the moment each fact row is deleted.
    /// </summary>
    [Fact]
    public void TheApply_DeletesWithForeignKeysOn()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedUnguarded(connection);
        Execute(connection,
            "CREATE TEMP TABLE fk_seen (on_ INTEGER); "
            + "CREATE TEMP TRIGGER fk_probe AFTER DELETE ON main.fact BEGIN "
            + "INSERT INTO fk_seen SELECT foreign_keys FROM pragma_foreign_keys; END;");

        var report = StoreCompactor.Compact(connection, sandbox.Home, Repo, apply: true, T0);

        Assert.Equal(report.FactsPruned, Scalar(connection, "SELECT count(*) FROM temp.fk_seen;"));
        Assert.Equal(0, Scalar(connection, "SELECT count(*) FROM temp.fk_seen WHERE on_ IS NOT 1;"));
    }

    private static List<string> Plan(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        command.Parameters.AddWithValue("$p", Repo);
        command.Parameters.AddWithValue("$id", 1L);
        using var reader = command.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
        {
            lines.Add(reader.GetString(3));
        }

        return lines;
    }

    /// <summary>
    /// F1–F3 at the plan level. A foreign-key child check does not appear in a plan, so each is
    /// shown through the statement SQLite runs for it, <c>col = ?</c> on the child column; the
    /// entity reference test is the compactor's own SQL.
    /// </summary>
    [Fact]
    public void TheDeletePaths_SeekTheirChildColumnsByIndex()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedUnguarded(connection);

        Assert.Contains(Plan(connection, "SELECT 1 FROM fact WHERE superseded_by = $id;"),
            line => line.Contains("ix_fact_superseded_by", StringComparison.Ordinal));
        Assert.Contains(Plan(connection, "SELECT 1 FROM fact WHERE object_id = $id;"),
            line => line.Contains("ix_fact_object_ref", StringComparison.Ordinal));

        Execute(connection, "CREATE TEMP TABLE compact_target (id INTEGER, was_live INTEGER);");
        try
        {
            var entityPlan = Plan(connection, StoreCompactor.EntitySql("SELECT count(*) FROM entity"));
            Assert.Contains(entityPlan, line => line.Contains("ix_fact_thread (subject_id=?)", StringComparison.Ordinal));
            Assert.Contains(entityPlan, line => line.Contains("ix_fact_object_ref (object_id=?)", StringComparison.Ordinal));
            Assert.DoesNotContain(entityPlan, line => line.StartsWith("SCAN fact", StringComparison.Ordinal));

            // With ix_fact_object_ref present the OR form also seeks, as a MULTI-INDEX OR, so the
            // seeks above cannot tell it from the split; this line is what pins F2's two probes.
            Assert.DoesNotContain(entityPlan, line => line.Contains("MULTI-INDEX OR", StringComparison.Ordinal));
        }
        finally
        {
            Execute(connection, "DROP TABLE temp.compact_target;");
        }
    }
}
