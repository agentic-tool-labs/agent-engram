using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Scheduled snapshots that leave out what Engram rebuilds (backup-derived-state.md, Phase 1 and
/// the restore and list half of Phase 3). Every assertion that a snapshot "keeps" something is
/// made through <see cref="AuthoredDigest"/>, so it is a row-for-row comparison and not a count.
/// </summary>
[Collection(SqlitePoolCollection.Name)]
public class BackupSlimTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    // --- the fixture: one of every row kind the digest covers ---------------------------------

    internal sealed record Seeded(long Coffee, long ReferencedByRelation, long ReferencedByReview, long ReferencedBySync);

    private static long Remember(
        SqliteConnection connection,
        string path,
        string predicate,
        string body,
        DateTimeOffset at,
        bool regenerable = false,
        long? session = null) =>
        FactStore.Remember(
            connection,
            new FactWrite(
                path,
                regenerable ? "symbol" : "concept",
                predicate,
                body,
                regenerable ? "code" : "project",
                regenerable ? "observed" : "stated",
                Evidence: regenerable ? "src/a.cs:1" : null,
                Regenerable: regenerable,
                SessionId: session,
                AnalyzerTier: regenerable ? 1 : null),
            at).FactId;

    private static void Sql(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static Seeded Seed(SqliteConnection connection)
    {
        Sql(connection, "INSERT INTO session (id, external_id, host, repo_path, started_at, ended_at, digest) "
            + "VALUES (7, 'sess-7', 'cli', '/code/acme', 100, 200, 'worked on acme');");

        // Authored: a belief revised, one forgotten, with the session on the first.
        Remember(connection, "/people/jim/preferences", "prefers", "tea in the morning", T0, session: 7);
        var coffee = Remember(connection, "/people/jim/preferences", "prefers", "coffee in the morning", T0.AddHours(1));
        var forgotten = Remember(connection, "/knowledge/acme/release", "states", "ships on Fridays", T0);
        FactStore.Forget(connection, forgotten, "no longer true", T0.AddHours(2));
        var judged = Remember(connection, "/knowledge/acme/deploy", "states", "deploys go through CI", T0);

        // Regenerable: live, closed by a regenerable rewrite, and closed by an authored one.
        Remember(connection, "/code/acme/src/a.cs#Foo", "declares", "class Foo", T0, regenerable: true);
        Remember(connection, "/code/acme/src/a.cs#Bar", "declares", "class Bar v1", T0, regenerable: true);
        Remember(connection, "/code/acme/src/a.cs#Bar", "declares", "class Bar v2", T0.AddHours(1), regenerable: true);
        Remember(connection, "/code/acme/src/b.cs#Baz", "declares", "class Baz as indexed", T0, regenerable: true);
        Remember(connection, "/code/acme/src/b.cs#Baz", "declares", "class Baz as the user corrected it", T0.AddHours(1));

        // Regenerable facts that authored side tables name (compact F5).
        var byRelation = Remember(connection, "/code/acme/src/c.cs#Qux", "declares", "class Qux", T0, regenerable: true);
        var byReview = Remember(connection, "/code/acme/src/c.cs#Quux", "declares", "class Quux", T0, regenerable: true);
        var bySync = Remember(connection, "/code/acme/src/c.cs#Corge", "declares", "class Corge", T0, regenerable: true);

        Sql(connection,
            $"""
            INSERT INTO fact_relation (fact_id, related_id, relation, reason, judged_at)
                VALUES ({coffee}, {judged}, 'not_conflict', 'different topics', 300),
                       ({judged}, {byRelation}, 'scoped', 'CI builds Qux', 301);
            INSERT INTO fact_review (fact_id, review_after, set_at) VALUES ({coffee}, 900, 400), ({byReview}, 901, 401);
            INSERT INTO fact_sync_request (fact_id, requested_at) VALUES ({bySync}, 500);
            INSERT INTO repo_enrollment (identity, state, source, last_root, decided_at, last_full_scan_at)
                VALUES ('github.com/acme/acme', 'enrolled', 'user', '/src/acme', 600, 650);
            INSERT INTO repo_registry (repo_path, identity, disk_path, detached_at, created_at)
                VALUES ('/code/acme', 'github.com/acme/acme', '/src/acme', NULL, 600);
            INSERT INTO file_state (repo_path, path, blob_sha, lang, indexed_at)
                VALUES ('/code/acme', 'src/a.cs', 'abc123', 'csharp', 700);
            INSERT INTO entity_alias (entity_id, alias, kind, created_at)
                SELECT id, '/code/acme/src/old-a.cs#Foo', 'path', 800 FROM entity WHERE path = '/code/acme/src/a.cs#Foo';
            """);

        return new Seeded(coffee, byRelation, byReview, bySync);
    }

    private static BackupMarkers Markers(string path)
    {
        try
        {
            using var connection = EngramDatabase.Open(path);
            return BackupMarkers.Read(connection);
        }
        finally
        {
            EngramDatabase.ReleasePooledConnections(path);
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> DigestOf(string path, string? libDir = null)
    {
        try
        {
            using var connection = EngramDatabase.Open(path, libDir);
            return AuthoredDigest.Read(connection);
        }
        finally
        {
            EngramDatabase.ReleasePooledConnections(path);
        }
    }

    private static bool HasTable(string path, string table)
    {
        try
        {
            using var connection = EngramDatabase.Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name = $n;";
            command.Parameters.AddWithValue("$n", table);
            return (long)command.ExecuteScalar()! > 0;
        }
        finally
        {
            EngramDatabase.ReleasePooledConnections(path);
        }
    }

    private static void Embed(SqliteConnection connection, params long[] ids)
    {
        VectorIndex.EnsureCreated(connection, new EmbeddingSpace("test-embedder", 4));
        foreach (var id in ids)
        {
            VectorIndex.Write(connection, null, id, [1f, 0f, 0f, 0f]);
        }
    }

    private static (int Exit, string Stdout, string Stderr) Cli(EngramHome home, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = Engram.Cli.BackupCommand.Run(home.Root, args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    // --- I1, I3, I8: what an authored snapshot keeps ------------------------------------------

    [Fact]
    public void AuthoredTake_KeepsAuthoredTruthEveryLiveFactAndTheIndexState()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Seed(connection);
        var source = AuthoredDigest.Read(connection);
        var fingerprint = BackupFingerprint.Read(connection);

        var snapshot = BackupStore.Take(connection, sandbox.Home, T0, BackupScope.Authored);

        Assert.Null(snapshot.Warning);
        AuthoredDigest.Equal(source, DigestOf(snapshot.Path));
        Assert.NotEmpty(source["liveRegenerable"]);
        Assert.NotEmpty(source["registry"]);
        using (var copy = EngramDatabase.Open(snapshot.Path))
        {
            Assert.Equal(fingerprint, BackupFingerprint.Read(copy));
        }

        EngramDatabase.ReleasePooledConnections(snapshot.Path);
    }

    /// <summary>Negative case "store with no fact_vec": slimming succeeds and says it dropped nothing.</summary>
    [Fact]
    public void AuthoredTake_OfAStoreWithNoVectorIndex_IsMarkedAuthoredWithNothingOmitted()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Seed(connection);

        var snapshot = BackupStore.Take(connection, sandbox.Home, T0, BackupScope.Authored);

        Assert.Equal(new BackupMarkers(BackupMarkers.AuthoredScope, 0), Markers(snapshot.Path));
    }

    [Fact]
    public void FullTake_CarriesNoMarker()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Seed(connection);

        var snapshot = BackupStore.Take(connection, sandbox.Home, T0, BackupScope.Full);

        Assert.False(Markers(snapshot.Path).IsMarked);
    }

    [Fact]
    public void AuthoredTake_DropsTheVectorIndexAndItsSpace_FromTheCopyOnly()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new VectorSandbox();
        var seeded = Seed(sandbox.Connection);
        Embed(sandbox.Connection, seeded.Coffee, seeded.ReferencedByRelation);
        var source = AuthoredDigest.Read(sandbox.Connection);
        var fingerprint = BackupFingerprint.Read(sandbox.Connection);

        var slim = BackupStore.Take(sandbox.Connection, sandbox.Home, T0, BackupScope.Authored);
        var full = BackupStore.Take(sandbox.Connection, sandbox.Home, T0, BackupScope.Full);

        Assert.Null(slim.Warning);
        Assert.False(HasTable(slim.Path, VectorIndex.TableName));
        Assert.Equal(new BackupMarkers(BackupMarkers.AuthoredScope, 2), Markers(slim.Path));
        using (var copy = EngramDatabase.Open(slim.Path))
        {
            Assert.Null(EngramDatabase.ReadMeta(copy, VectorIndex.ModelKey));
            Assert.Null(EngramDatabase.ReadMeta(copy, VectorIndex.DimensionsKey));
            Assert.Equal(fingerprint, BackupFingerprint.Read(copy));
        }

        EngramDatabase.ReleasePooledConnections(slim.Path);
        AuthoredDigest.Equal(source, DigestOf(slim.Path));

        // The neighbour that must not match: a full snapshot keeps the index.
        Assert.True(HasTable(full.Path, VectorIndex.TableName));
        Assert.Equal(2, VectorIndex.Count(sandbox.Connection));
    }

    // --- I2: the live store is never mutated by a take ----------------------------------------

    [Fact]
    public void AuthoredTake_LeavesTheLiveStoreExactlyAsItWas()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new VectorSandbox();
        var seeded = Seed(sandbox.Connection);
        Embed(sandbox.Connection, seeded.Coffee);
        var digest = AuthoredDigest.Read(sandbox.Connection);
        var file = new FileInfo(sandbox.Home.DatabasePath);
        var (size, modified) = (file.Length, file.LastWriteTimeUtc);
        var model = EngramDatabase.ReadMeta(sandbox.Connection, VectorIndex.ModelKey);

        BackupStore.Take(sandbox.Connection, sandbox.Home, T0, BackupScope.Authored);

        file.Refresh();
        Assert.Equal((size, modified), (file.Length, file.LastWriteTimeUtc));
        AuthoredDigest.Equal(digest, AuthoredDigest.Read(sandbox.Connection));
        Assert.Equal(1, VectorIndex.Count(sandbox.Connection));
        Assert.Equal(model, EngramDatabase.ReadMeta(sandbox.Connection, VectorIndex.ModelKey));
        Assert.Null(EngramDatabase.ReadMeta(sandbox.Connection, BackupMarkers.ScopeKey));
    }

    [Fact]
    public void Slim_RefusesTheLiveStore_HavingDoneNothing()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Seed(connection);
        }

        Assert.Throws<InvalidOperationException>(() => BackupStore.Slim(sandbox.Home.DatabasePath, sandbox.Home));

        using var reopened = EngramDatabase.Open(sandbox.Home);
        Assert.Null(EngramDatabase.ReadMeta(reopened, BackupMarkers.ScopeKey));
    }

    // --- I4: a slimming failure keeps a full snapshot -----------------------------------------

    private const string BlockTheStamp =
        "CREATE TRIGGER planted_block BEFORE INSERT ON schema_meta WHEN new.key = 'backup_scope' "
        + "BEGIN SELECT RAISE(ABORT, 'planted'); END;";

    [Fact]
    public void SlimmingThatFails_KeepsAFullSnapshotAndSaysSo()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Seed(connection);
        Sql(connection, BlockTheStamp);

        var snapshot = BackupStore.Take(connection, sandbox.Home, T0, BackupScope.Authored);

        Assert.Contains("kept full", snapshot.Warning, StringComparison.Ordinal);
        Assert.False(Markers(snapshot.Path).IsMarked);
        Assert.Single(BackupStore.List(sandbox.Home));
        Assert.Empty(Directory.EnumerateFiles(sandbox.Home.BackupDir, "*.partial"));
    }

    /// <summary>
    /// The drop commits before the stamp fails, so renaming the copy as it stands would ship a
    /// snapshot with no vectors and no marker saying so. The vectors being there is the proof the
    /// copy was taken again.
    /// </summary>
    [Fact]
    public void SlimmingThatFailsAfterTheDrop_StillKeepsTheVectors()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new VectorSandbox();
        var seeded = Seed(sandbox.Connection);
        Embed(sandbox.Connection, seeded.Coffee);
        Sql(sandbox.Connection, BlockTheStamp);

        var snapshot = BackupStore.Take(sandbox.Connection, sandbox.Home, T0, BackupScope.Authored);

        Assert.NotNull(snapshot.Warning);
        Assert.True(HasTable(snapshot.Path, VectorIndex.TableName));
        Assert.False(Markers(snapshot.Path).IsMarked);
    }

    [Fact]
    public void AVectorIndexSqliteVecCannotLoad_IsKeptAndTheSnapshotSaysSo()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new VectorSandbox();
        var seeded = Seed(sandbox.Connection);
        Embed(sandbox.Connection, seeded.Coffee);
        File.Delete(VectorExtension.PathIn(sandbox.Home.LibDir));

        var snapshot = BackupStore.Take(sandbox.Connection, sandbox.Home, T0, BackupScope.Authored);

        Assert.Contains("sqlite-vec", snapshot.Warning, StringComparison.Ordinal);
        Assert.True(HasTable(snapshot.Path, VectorIndex.TableName));
        Assert.Equal(new BackupMarkers(BackupMarkers.AuthoredWithVectorsScope, 0), Markers(snapshot.Path));
    }

    // --- callers: which scope each one asks for ----------------------------------------------

    [Fact]
    public void BackupTake_IsAuthored_AndWithTheJournalOff_WritesNoJournal()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Seed(connection);
        }

        File.WriteAllText(sandbox.Home.ConfigPath, "[backup]\njournal = false\n");

        var (exit, _, _) = Cli(sandbox.Home, "take");

        Assert.Equal(0, exit);
        Assert.Equal(BackupMarkers.AuthoredScope, Markers(Assert.Single(BackupStore.List(sandbox.Home)).Path).Scope);
        Assert.False(File.Exists(FactJournal.PathIn(sandbox.Home)));
    }

    [Fact]
    public void PreCompactAndPreRepair_AreAuthored()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Seed(connection);

        // Read each right after its own call: the second one's prune thins the first away.
        var compact = StoreCompactor.Compact(connection, sandbox.Home, path: null, apply: true, T0);
        Assert.Equal(BackupMarkers.AuthoredScope, Markers(Path.Combine(sandbox.Home.BackupDir, compact.SnapshotName!)).Scope);

        StoreRepairer.Repair(connection, sandbox.Home, apply: true, T0.AddMinutes(1));
        var repair = Assert.Single(BackupStore.List(sandbox.Home), s => s.Name.Contains("pre-repair", StringComparison.Ordinal));
        Assert.Equal(BackupMarkers.AuthoredScope, Markers(repair.Path).Scope);
    }

    [Fact]
    public void PreMigrationSnapshot_IsFull()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using (var old = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Seed(old);
            Sql(old,
                """
                DROP TRIGGER fact_embedding_insert;
                DROP TRIGGER fact_embedding_update;
                DROP TRIGGER fact_embedding_delete;
                DELETE FROM schema_meta WHERE key = 'embedding_revision';
                UPDATE schema_meta SET value = '17' WHERE key = 'schema_version';
                """);
        }

        using var migrated = EngramDatabase.OpenInitialized(sandbox.Home);

        var snapshot = Assert.Single(BackupStore.List(sandbox.Home));
        Assert.Contains("pre-v", snapshot.Name, StringComparison.Ordinal);
        Assert.False(Markers(snapshot.Path).IsMarked);
    }

    // --- restore (Phase 3) -------------------------------------------------------------------

    [Fact]
    public void Restore_OfAnAuthoredSnapshot_SaysWhatToEmbed_ClearsTheMarker_AndPreRestoreIsFull()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        BackupSnapshot slim;
        IReadOnlyDictionary<string, IReadOnlyList<string>> source;
        int embeddable;
        using var sandbox = new SandboxHome(initialize: false);
        VectorExtensionFile.InstallInto(sandbox.Home);
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            var seeded = Seed(connection);
            Embed(connection, seeded.Coffee);
            source = AuthoredDigest.Read(connection);
            embeddable = VectorIndex.CountEmbeddable(connection);
            slim = BackupStore.Take(connection, sandbox.Home, T0, BackupScope.Authored);
        }

        SqliteConnection.ClearAllPools();

        var dry = Cli(sandbox.Home, "restore", slim.Name);
        Assert.Equal(0, dry.Exit);
        Assert.Contains("This snapshot leaves out state Engram rebuilds:", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains($"{embeddable} facts to embed", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("would be cleared", dry.Stdout, StringComparison.Ordinal);
        Assert.Single(BackupStore.List(sandbox.Home));

        var applied = Cli(sandbox.Home, "restore", slim.Name, "--apply");
        Assert.Equal(0, applied.Exit);
        Assert.Contains($"{embeddable} facts to embed", applied.Stdout, StringComparison.Ordinal);

        using (var restored = EngramDatabase.Open(sandbox.Home))
        {
            Assert.Null(EngramDatabase.ReadMeta(restored, BackupMarkers.ScopeKey));
            Assert.Null(EngramDatabase.ReadMeta(restored, BackupMarkers.OmittedKey));
            Assert.False(VectorIndex.Exists(restored));
            AuthoredDigest.Equal(source, AuthoredDigest.Read(restored));

            // Recall's lexical lane answers at once; nothing waits on the re-embed.
            Assert.NotEmpty(FactStore.SearchRanked(restored, "coffee", 10));
        }

        EngramDatabase.ReleasePooledConnections(sandbox.Home.DatabasePath);
        var preRestore = Assert.Single(BackupStore.List(sandbox.Home), s => s.Name.Contains("pre-restore", StringComparison.Ordinal));
        Assert.False(Markers(preRestore.Path).IsMarked);
        Assert.True(HasTable(preRestore.Path, VectorIndex.TableName));
    }

    [Fact]
    public void Restore_OfAFullSnapshot_PrintsNoOmissions()
    {
        using var sandbox = new SandboxHome(initialize: false);
        BackupSnapshot full;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Seed(connection);
            full = BackupStore.Take(connection, sandbox.Home, T0, BackupScope.Full);
        }

        var dry = Cli(sandbox.Home, "restore", full.Name);

        Assert.Equal(0, dry.Exit);
        Assert.DoesNotContain("leaves out", dry.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("marker", dry.Stdout, StringComparison.Ordinal);
    }

    /// <summary>Ruling 2: the vector line counts what still needs a vector, so full coverage prints none.</summary>
    [Fact]
    public void Restore_OfAnAuthoredPlusVectorsSnapshotWithFullCoverage_PrintsNoVectorLine()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new SandboxHome(initialize: false);
        VectorExtensionFile.InstallInto(sandbox.Home);
        BackupSnapshot snapshot;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Seed(connection);
            using var eligible = connection.CreateCommand();
            eligible.CommandText = "SELECT id FROM fact WHERE valid_to IS NULL AND NOT (regenerable IS 1 AND object_id IS NOT NULL);";
            var ids = new List<long>();
            using (var reader = eligible.ExecuteReader())
            {
                while (reader.Read())
                {
                    ids.Add(reader.GetInt64(0));
                }
            }

            Embed(connection, [.. ids]);
            File.Delete(VectorExtension.PathIn(sandbox.Home.LibDir));
            snapshot = BackupStore.Take(connection, sandbox.Home, T0, BackupScope.Authored);
        }

        VectorExtensionFile.InstallInto(sandbox.Home);
        SqliteConnection.ClearAllPools();

        var dry = Cli(sandbox.Home, "restore", snapshot.Name);

        Assert.Equal(BackupMarkers.AuthoredWithVectorsScope, Markers(snapshot.Path).Scope);
        Assert.DoesNotContain("to embed", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("'authored+vectors' scope marker would be cleared", dry.Stdout, StringComparison.Ordinal);
    }

    // --- list (Phase 3) ----------------------------------------------------------------------

    [Fact]
    public void List_ShowsEachSnapshotsScope()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Seed(connection);
            BackupStore.Take(connection, sandbox.Home, T0, BackupScope.Authored);
            BackupStore.Take(connection, sandbox.Home, T0.AddHours(1), BackupScope.Full);
        }

        var (exit, stdout, _) = Cli(sandbox.Home, "list");

        Assert.Equal(0, exit);
        var rows = stdout.Split('\n').Where(l => l.Contains(BackupStore.Prefix, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Contains(" full ", rows[0], StringComparison.Ordinal);
        Assert.Contains(" authored ", rows[1], StringComparison.Ordinal);
    }
}
