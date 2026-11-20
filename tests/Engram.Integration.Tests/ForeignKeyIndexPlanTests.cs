using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// C4 (compact-quadratic-deletes.md §4): the v20 indexes exist for compact's foreign-key checks
/// and must not move any other query onto them. Each statement below is copied from the line the
/// spec names and planned with and without the two indexes; the plans must match. If one
/// differs, the planner switched to a new index, which the spec says to report, not fix.
/// </summary>
public class ForeignKeyIndexPlanTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, string, bool> Statements => new()
    {
        {
            "RecallRanker.cs prior-session edges",
            """
            SELECT f.session_id
            FROM entity e CROSS JOIN fact f INDEXED BY ux_fact_edge_live ON f.subject_id = e.id
            WHERE f.valid_to IS NULL AND f.object_id IS NOT NULL
              AND e.path >= '/sessions/' AND e.path < '/sessions0'
              AND f.session_id IS NOT $s
            """,
            false
        },
        {
            "CodeCallGraph.cs callers",
            "SELECT f.path, o.name, f.analyzer_tier FROM fact f JOIN entity o ON o.id = f.object_id "
                + "WHERE f.predicate = 'calls' AND f.valid_to IS NULL AND f.object_id IS NOT NULL "
                + "AND o.path IN ($o0) AND f.path LIKE '%' || $repo || '%';",
            false
        },
        {
            "FactJournal.cs live objectless",
            """
            SELECT 1 FROM fact f
            JOIN entity e ON e.id = f.subject_id
            WHERE e.path = $path AND f.predicate = $predicate AND f.object_id IS NULL
              AND f.valid_to IS NULL
            LIMIT 1;
            """,
            false
        },
        {
            "FactJournal.cs live edge",
            """
            SELECT 1 FROM fact f
            JOIN entity e ON e.id = f.subject_id
            JOIN entity o ON o.id = f.object_id
            WHERE e.path = $path AND f.predicate = $predicate AND o.path = $object
              AND f.valid_to IS NULL
            LIMIT 1;
            """,
            false
        },
        {
            "FactJournal.cs existing objectless",
            """
            SELECT f.id, f.valid_to IS NULL AS is_live FROM fact f
            JOIN entity e ON e.id = f.subject_id
            WHERE e.path = $path AND f.predicate = $predicate AND f.body = $body
              AND f.valid_from = $validFrom AND f.object_id IS NULL
            ORDER BY is_live DESC, f.id ASC
            LIMIT 2;
            """,
            false
        },
        {
            "FactJournal.cs existing edge",
            """
            SELECT f.id, f.valid_to IS NULL AS is_live FROM fact f
            JOIN entity e ON e.id = f.subject_id
            JOIN entity o ON o.id = f.object_id
            WHERE e.path = $path AND f.predicate = $predicate AND f.body = $body
              AND f.valid_from = $validFrom AND o.path = $object
            ORDER BY is_live DESC, f.id ASC
            LIMIT 2;
            """,
            false
        },
        {
            "VectorIndex.cs edge counts",
            """
            SELECT f.predicate, COUNT(*)
            FROM fact_vec v
            JOIN fact f ON f.id = v.fact_id
            WHERE f.regenerable IS 1 AND f.object_id IS NOT NULL
            GROUP BY f.predicate
            ORDER BY f.predicate;
            """,
            true
        },
        { "VectorIndex.cs backfill batch", VectorIndex.BackfillBatchSql, true },
        { "VectorIndex.cs pending count", VectorIndex.PendingCountSql, true },
        {
            "VectorIndex.cs CountEmbeddable",
            "SELECT COUNT(*) FROM fact WHERE valid_to IS NULL AND NOT (regenerable IS 1 AND object_id IS NOT NULL);",
            false
        },
        {
            "FactTokenIndex.cs missing candidates",
            "SELECT id FROM fact WHERE valid_to IS NULL AND NOT (regenerable IS 1 AND object_id IS NOT NULL) "
                + "AND id NOT IN (SELECT DISTINCT fact_id FROM fact_token);",
            false
        },
        {
            "FactStore.cs ReadLive excludeEdges",
            "SELECT f.id FROM fact f JOIN entity e ON e.id = f.subject_id WHERE f.valid_to IS NULL "
                + "AND NOT (f.regenerable IS 1 AND f.object_id IS NOT NULL) ORDER BY f.id;",
            false
        },
        {
            "FactTokenIndex.cs live facts",
            """
            SELECT f.id, f.path, e.name, f.body
              FROM fact f
              JOIN entity e ON e.id = f.subject_id
             WHERE f.valid_to IS NULL AND NOT (f.regenerable IS 1 AND f.object_id IS NOT NULL);
            """,
            false
        },
        {
            "FactStore.cs version counts",
            """
            SELECT e.path, f.predicate, COUNT(*)
              FROM fact f
              JOIN entity e ON e.id = f.subject_id
             WHERE NOT (f.regenerable IS 1 AND f.object_id IS NOT NULL)
             GROUP BY e.path, f.predicate
            HAVING COUNT(*) > 1;
            """,
            false
        },
    };

    [Theory]
    [MemberData(nameof(Statements))]
    public void AStatementOutsideCompact_PlansTheSameWithAndWithoutTheV20Indexes(string site, string sql, bool needsVectors)
    {
        Assert.SkipUnless(!needsVectors || VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new SandboxHome(initialize: false);
        if (needsVectors) VectorExtensionFile.InstallInto(sandbox.Home);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        if (needsVectors) VectorIndex.EnsureCreated(connection, new EmbeddingSpace("test", 4));
        Seed(connection);

        var with = Plan(connection, sql);
        Execute(connection, "DROP INDEX ix_fact_superseded_by; DROP INDEX ix_fact_object_ref;");
        var without = Plan(connection, sql);

        Assert.True(
            with.SequenceEqual(without),
            $"{site}: plan changed with the v20 indexes\nwith:\n{string.Join("\n", with)}\nwithout:\n{string.Join("\n", without)}");
    }

    /// <summary>
    /// Ruling 2 on C4 (D92): without its unary plus <c>PruneIneligible</c> would seek
    /// ix_fact_object_ref over most of the table, measured 2.8x slower than the scan it had at
    /// v19. Planned from the production text, so removing the plus reddens this.
    /// </summary>
    [Fact]
    public void PruneIneligible_StillScansFactUnderV20()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new SandboxHome(initialize: false);
        VectorExtensionFile.InstallInto(sandbox.Home);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        VectorIndex.EnsureCreated(connection, new EmbeddingSpace("test", 4));
        Seed(connection);

        var with = Plan(connection, VectorIndex.PruneIneligibleSql);
        Execute(connection, "DROP INDEX ix_fact_superseded_by; DROP INDEX ix_fact_object_ref;");
        var without = Plan(connection, VectorIndex.PruneIneligibleSql);

        Assert.Contains("SCAN fact", with);
        Assert.Equal(without, with);
    }

    /// <summary>
    /// The unary plus changes the plan and nothing else: exactly the vectors of regenerable edge
    /// facts go, live or closed, and an authored edge or a regenerable non-edge keeps its vector.
    /// </summary>
    [Fact]
    public void PruneIneligible_DeletesExactlyTheRegenerableEdgeVectors()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new SandboxHome(initialize: false);
        VectorExtensionFile.InstallInto(sandbox.Home);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        VectorIndex.EnsureCreated(connection, new EmbeddingSpace("test", 4));
        Seed(connection);
        FactStore.Remember(
            connection,
            new FactWrite(
                "/knowledge/testing/importer", "note", "uses", "The importer uses W0.", "project", "stated",
                ObjectPath: "/projects/acme/code/api/src/A.cs#W0", ObjectKind: "symbol"),
            T0);
        Execute(connection,
            "INSERT INTO fact_vec (fact_id, is_live, embedding) "
            + "SELECT id, valid_to IS NULL, '[0.1, 0.2, 0.3, 0.4]' FROM fact;");
        var expected = Ids(connection, "SELECT id FROM fact WHERE NOT (regenerable = 1 AND object_id IS NOT NULL) ORDER BY id;");
        var edges = Ids(connection, "SELECT id FROM fact WHERE regenerable = 1 AND object_id IS NOT NULL;");
        Assert.NotEmpty(edges);
        Assert.NotEmpty(Ids(connection, "SELECT id FROM fact WHERE regenerable = 1 AND valid_to IS NOT NULL AND object_id IS NOT NULL;"));
        Assert.All(Ids(connection, "SELECT id FROM fact WHERE regenerable = 0 AND object_id IS NOT NULL;"), id => Assert.Contains(id, expected));
        Assert.All(Ids(connection, "SELECT id FROM fact WHERE regenerable = 1 AND valid_to IS NOT NULL AND object_id IS NULL;"), id => Assert.Contains(id, expected));

        var deleted = VectorIndex.PruneIneligible(connection);

        Assert.Equal(edges.Count, deleted);
        Assert.Equal(expected, Ids(connection, "SELECT fact_id FROM fact_vec ORDER BY fact_id;"));
    }

    private static List<long> Ids(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static void Seed(SqliteConnection connection)
    {
        for (var i = 0; i < 4; i++)
        {
            var path = $"/projects/acme/code/api/src/A.cs#W{i}";
            FactStore.Remember(connection, new FactWrite(path, "symbol", "declares", $"declared as class W{i}", "code", "observed", Regenerable: true), T0);
            FactStore.Remember(connection, new FactWrite(path, "symbol", "declares", $"declared as sealed class W{i}", "code", "observed", Regenerable: true), T0);
            FactStore.Remember(
                connection,
                new FactWrite(
                    $"/projects/acme/code/api/src/B.cs#C{i}", "symbol", "calls", $"calls W{i}", "code", "observed",
                    Regenerable: true, ObjectPath: path, ObjectKind: "symbol"),
                T0);
            FactStore.Remember(connection, new FactWrite($"/knowledge/testing/n{i}", "note", "states", $"Note {i}.", "project", "stated"), T0);
        }

        // A closed regenerable edge: the same caller, predicate and object, restated.
        FactStore.Remember(
            connection,
            new FactWrite(
                "/projects/acme/code/api/src/B.cs#C0", "symbol", "calls", "calls W0 twice", "code", "observed",
                Regenerable: true, ObjectPath: "/projects/acme/code/api/src/A.cs#W0", ObjectKind: "symbol"),
            T0);
    }

    private static List<string> Plan(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var name in new[] { "$s", "$o0", "$repo", "$path", "$predicate", "$object", "$body", "$validFrom", "$limit" })
        {
            if (sql.Contains(name, StringComparison.Ordinal))
            {
                command.Parameters.AddWithValue(name, "x");
            }
        }

        using var reader = command.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
        {
            lines.Add(reader.GetString(3));
        }

        return lines;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
