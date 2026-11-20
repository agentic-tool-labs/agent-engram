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

    private static long IdOf(SqliteConnection connection, string body)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM fact WHERE body = $b;";
        command.Parameters.AddWithValue("$b", body);
        return (long)command.ExecuteScalar()!;
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

        Assert.Equal(new BackupMarkers(BackupMarkers.AuthoredScope, 0, FactsClosed: 1), Markers(snapshot.Path));
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

        // Bar v1 is closed code history 2b prunes; its vector still counts as omitted (D96, Q1).
        Embed(sandbox.Connection, seeded.Coffee, seeded.ReferencedByRelation, IdOf(sandbox.Connection, "class Bar v1"));
        var source = AuthoredDigest.Read(sandbox.Connection);
        var fingerprint = BackupFingerprint.Read(sandbox.Connection);

        var slim = BackupStore.Take(sandbox.Connection, sandbox.Home, T0, BackupScope.Authored);
        var full = BackupStore.Take(sandbox.Connection, sandbox.Home, T0, BackupScope.Full);

        Assert.Null(slim.Warning);
        Assert.False(HasTable(slim.Path, VectorIndex.TableName));
        Assert.Equal(new BackupMarkers(BackupMarkers.AuthoredScope, 3, FactsClosed: 1), Markers(slim.Path));
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
        Assert.Equal(3, VectorIndex.Count(sandbox.Connection));
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

        Assert.Throws<InvalidOperationException>(() => BackupStore.Slim(sandbox.Home.DatabasePath, sandbox.Home, T0));

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

    /// <summary>
    /// Review round 1, n3: ANY slimming failure keeps a full snapshot, not only the exception types
    /// SQLite and the file system happen to throw.
    /// </summary>
    [Fact]
    public void SlimmingThatThrowsAnyException_StillLeavesAFullRestorableSnapshot()
    {
        using var sandbox = new SandboxHome(initialize: false);
        IReadOnlyDictionary<string, IReadOnlyList<string>> source;
        BackupSnapshot snapshot;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Seed(connection);
            source = AuthoredDigest.Read(connection);
            BackupStore.SlimFault.Value = () => throw new UnauthorizedAccessException("planted");
            try
            {
                snapshot = BackupStore.Take(connection, sandbox.Home, T0, BackupScope.Authored);
            }
            finally
            {
                BackupStore.SlimFault.Value = null;
            }
        }

        Assert.Contains("planted", snapshot.Warning, StringComparison.Ordinal);
        Assert.False(Markers(snapshot.Path).IsMarked);

        SqliteConnection.ClearAllPools();
        Assert.Equal(0, Cli(sandbox.Home, "restore", snapshot.Name, "--apply").Exit);
        AuthoredDigest.Equal(source, DigestOf(sandbox.Home.DatabasePath));
    }

    /// <summary>Review round 1, n4: a full snapshot of a store carrying stale markers lists and restores as full.</summary>
    [Fact]
    public void FullTake_OfAStoreCarryingStaleMarkers_IsLabelledFull()
    {
        using var sandbox = new SandboxHome(initialize: false);
        BackupSnapshot full;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Seed(connection);
            using (var transaction = EngramDatabase.BeginWrite(connection))
            {
                EngramDatabase.WriteMeta(connection, transaction, BackupMarkers.ScopeKey, BackupMarkers.AuthoredScope);
                EngramDatabase.WriteMeta(connection, transaction, BackupMarkers.OmittedKey, "{\"vectors\":5}");
                transaction.Commit();
            }

            full = BackupStore.Take(connection, sandbox.Home, T0, BackupScope.Full);

            // The live store's own stale rows are not this take's to touch.
            Assert.Equal(BackupMarkers.AuthoredScope, EngramDatabase.ReadMeta(connection, BackupMarkers.ScopeKey));
        }

        Assert.False(Markers(full.Path).IsMarked);
        using (var copy = EngramDatabase.Open(full.Path))
        {
            Assert.Null(EngramDatabase.ReadMeta(copy, BackupMarkers.OmittedKey));
        }

        EngramDatabase.ReleasePooledConnections(full.Path);
        Assert.Contains(" full ", Cli(sandbox.Home, "list").Stdout, StringComparison.Ordinal);
        var dry = Cli(sandbox.Home, "restore", full.Name);
        Assert.DoesNotContain("leaves out", dry.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("marker", dry.Stdout, StringComparison.Ordinal);
    }

    /// <summary>Ruling after round 2: a failed marker strip keeps the full copy and warns; the take does not fail.</summary>
    [Fact]
    public void FullTake_WhoseMarkerStripFails_KeepsARestorableSnapshotAndWarns()
    {
        using var sandbox = new SandboxHome(initialize: false);
        IReadOnlyDictionary<string, IReadOnlyList<string>> source;
        BackupSnapshot full;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Seed(connection);
            source = AuthoredDigest.Read(connection);
            using (var transaction = EngramDatabase.BeginWrite(connection))
            {
                EngramDatabase.WriteMeta(connection, transaction, BackupMarkers.ScopeKey, BackupMarkers.AuthoredScope);
                transaction.Commit();
            }

            BackupStore.StripFault.Value = () => throw new UnauthorizedAccessException("planted");
            try
            {
                full = BackupStore.Take(connection, sandbox.Home, T0, BackupScope.Full);
            }
            finally
            {
                BackupStore.StripFault.Value = null;
            }
        }

        Assert.Contains("may list as authored", full.Warning, StringComparison.Ordinal);
        Assert.Contains("planted", full.Warning, StringComparison.Ordinal);
        Assert.True(File.Exists(full.Path));

        SqliteConnection.ClearAllPools();
        Assert.Equal(0, Cli(sandbox.Home, "restore", full.Name, "--apply").Exit);
        AuthoredDigest.Equal(source, DigestOf(sandbox.Home.DatabasePath));
    }

    /// <summary>
    /// §9 "provider = off with a stale fact_vec" and review round 1, n5: the vectors are dropped,
    /// restore promises no server-side embedding, and doctor reads embedding as Off afterwards.
    /// </summary>
    [Fact]
    public void ProviderOffWithAStaleVectorIndex_IsDropped_AndRestoreSaysEmbeddingIsOff()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new SandboxHome(initialize: false);
        VectorExtensionFile.InstallInto(sandbox.Home);
        File.WriteAllText(sandbox.Home.ConfigPath, "[embedding]\nprovider = \"none\"\n");
        BackupSnapshot slim;
        int embeddable;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            var seeded = Seed(connection);
            Embed(connection, seeded.Coffee);
            embeddable = VectorIndex.CountEmbeddable(connection);
            slim = BackupStore.Take(connection, sandbox.Home, T0, BackupScope.Authored);
        }

        SqliteConnection.ClearAllPools();
        Assert.False(HasTable(slim.Path, VectorIndex.TableName));

        var restore = Cli(sandbox.Home, "restore", slim.Name, "--apply");

        Assert.Equal(0, restore.Exit);
        Assert.Contains(
            $"{embeddable} facts have no vector; embedding is off in this home, so recall uses the lexical and overlap lanes only",
            restore.Stdout,
            StringComparison.Ordinal);
        Assert.DoesNotContain("the server embeds them", restore.Stdout, StringComparison.Ordinal);

        var report = Diagnostics.Run(
            sandbox.Home,
            _ => null,
            lifecycle: new ServerLifecycle(new FakeProcessInspector(), new FakeServerHealthChecker(), new FakeServerLauncher()),
            reachOut: false,
            executablePath: "/nonexistent/engram",
            claudeSettingsPath: Path.Combine(sandbox.Home.Root, "claude-settings.json"));
        Assert.Equal(DiagnosisState.Off, Assert.Single(report.Checks, c => c.Name == "embedding").State);
        EngramDatabase.ReleasePooledConnections(sandbox.Home.DatabasePath);
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
        Assert.Equal(new BackupMarkers(BackupMarkers.AuthoredWithVectorsScope, 0, FactsClosed: 1), Markers(snapshot.Path));
    }

    // --- Phase 2b: closed code history out of the snapshot -----------------------------------

    private static List<string> ClosedRegenerable(string path)
    {
        try
        {
            using var connection = EngramDatabase.Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT body FROM fact WHERE regenerable = 1 AND valid_to IS NOT NULL ORDER BY body;";
            using var reader = command.ExecuteReader();
            var bodies = new List<string>();
            while (reader.Read())
            {
                bodies.Add(reader.GetString(0));
            }

            return bodies;
        }
        finally
        {
            EngramDatabase.ReleasePooledConnections(path);
        }
    }

    /// <summary>
    /// Beyond <see cref="Seed"/>: a closed code fact a review names (F5-protected), and a code entity
    /// whose only fact is closed history and which carries an alias.
    /// </summary>
    private static void SeedClosedHistory(SqliteConnection connection)
    {
        var reviewed = Remember(connection, "/code/acme/src/e.cs#Grault", "declares", "class Grault v1", T0, regenerable: true);
        Remember(connection, "/code/acme/src/e.cs#Grault", "declares", "class Grault v2", T0.AddHours(1), regenerable: true);
        var gone = Remember(connection, "/code/acme/src/d.cs#Gone", "declares", "class Gone", T0, regenerable: true);
        Sql(connection,
            $"""
            UPDATE fact SET valid_to = {T0.AddHours(1).ToUnixTimeSeconds()} WHERE id = {gone};
            INSERT INTO fact_review (fact_id, review_after, set_at) VALUES ({reviewed}, 902, 402);
            INSERT INTO entity_alias (entity_id, alias, kind, created_at)
                SELECT id, '/code/acme/src/old-d.cs#Gone', 'path', 801 FROM entity WHERE path = '/code/acme/src/d.cs#Gone';
            """);
    }

    /// <summary>
    /// §4.2 and §9 I1, I3, I8 with the 2b negative cases: unprotected closed code history goes; a
    /// closed fact with an authored edge or a review stays; no entity or alias goes; every live
    /// fact, the registry and file state survive a restore; restore names what is not recoverable.
    /// </summary>
    [Fact]
    public void AuthoredTake_LeavesOutUnprotectedClosedCodeHistory_AndRestoreSaysSo()
    {
        using var sandbox = new SandboxHome(initialize: false);
        IReadOnlyDictionary<string, IReadOnlyList<string>> source;
        BackupSnapshot slim;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Seed(connection);
            SeedClosedHistory(connection);
            source = AuthoredDigest.Read(connection);
            var fingerprint = BackupFingerprint.Read(connection);

            slim = BackupStore.Take(connection, sandbox.Home, T0, BackupScope.Authored);

            Assert.Null(slim.Warning);
            using (var copy = EngramDatabase.Open(slim.Path))
            {
                Assert.Equal(fingerprint, BackupFingerprint.Read(copy));
            }

            EngramDatabase.ReleasePooledConnections(slim.Path);

            // The live store keeps its history: only the copy was compacted.
            Assert.Equal(4, ClosedRegenerable(sandbox.Home.DatabasePath).Count);
        }

        Assert.Equal(["class Baz as indexed", "class Grault v1"], ClosedRegenerable(slim.Path));
        Assert.Equal(new BackupMarkers(BackupMarkers.AuthoredScope, 0, FactsClosed: 2), Markers(slim.Path));
        Assert.Contains(source["aliases"], a => a.Contains("old-d.cs#Gone", StringComparison.Ordinal));
        AuthoredDigest.Equal(source, DigestOf(slim.Path));

        SqliteConnection.ClearAllPools();
        var restore = Cli(sandbox.Home, "restore", slim.Name, "--apply");

        Assert.Equal(0, restore.Exit);
        Assert.Contains(
            "  not recoverable: 2 closed code facts (superseded index history; 'compact' discards the same)",
            restore.Stdout,
            StringComparison.Ordinal);
        AuthoredDigest.Equal(source, DigestOf(sandbox.Home.DatabasePath));
    }

    /// <summary>§9 I2 for the compactor's snapshot-copy option: pointed at the live store it refuses, having done nothing.</summary>
    [Fact]
    public void CompactOnASnapshotCopy_RefusesTheLiveStore_HavingDoneNothing()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Seed(connection);

        Assert.Throws<InvalidOperationException>(
            () => StoreCompactor.Compact(connection, sandbox.Home, path: null, apply: true, T0, onSnapshotCopy: true));

        Assert.Equal(["class Bar v1", "class Baz as indexed"], ClosedRegenerable(sandbox.Home.DatabasePath));
        Assert.Empty(BackupStore.List(sandbox.Home));
    }

    /// <summary>D96 Q1 negative: a compact of the live store still sweeps the vectors of what it prunes.</summary>
    [Fact]
    public void LiveCompact_StillSweepsTheVectorsOfWhatItPrunes()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new VectorSandbox();
        var seeded = Seed(sandbox.Connection);
        Embed(sandbox.Connection, seeded.Coffee, IdOf(sandbox.Connection, "class Bar v1"));

        var report = StoreCompactor.Compact(sandbox.Connection, sandbox.Home, path: null, apply: true, T0);

        Assert.Equal(1, report.VectorsRemoved);
        Assert.Equal(1, VectorIndex.Count(sandbox.Connection));
    }

    /// <summary>D96 Q1: on a snapshot copy compact leaves vector rows alone — the take has already dropped the table.</summary>
    [Fact]
    public void CompactOnASnapshotCopy_DoesNotSweepVectors()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new VectorSandbox();
        Seed(sandbox.Connection);
        Embed(sandbox.Connection, IdOf(sandbox.Connection, "class Bar v1"));
        var copyPath = Path.Combine(sandbox.Home.Root, "copy.db");
        Sql(sandbox.Connection, $"VACUUM INTO '{copyPath}';");

        try
        {
            using var copy = EngramDatabase.Open(copyPath, sandbox.Home.LibDir);
            var report = StoreCompactor.Compact(copy, sandbox.Home, path: null, apply: true, T0, onSnapshotCopy: true);

            Assert.Equal(1, report.ClosedPruned);
            Assert.Equal(0, report.VectorsRemoved);
            Assert.Equal(1, VectorIndex.Count(copy));
        }
        finally
        {
            EngramDatabase.ReleasePooledConnections(copyPath);
        }
    }

    /// <summary>D96 Q2: a pre-compact snapshot keeps the closed code fact a scheduled snapshot omits.</summary>
    [Fact]
    public void PreCompactSnapshot_KeepsClosedCodeHistory_AScheduledSnapshotOmits()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Seed(connection);

        var scheduled = BackupStore.Take(connection, sandbox.Home, T0, BackupScope.Authored);
        Assert.DoesNotContain("class Bar v1", ClosedRegenerable(scheduled.Path));

        var compact = StoreCompactor.Compact(connection, sandbox.Home, path: null, apply: true, T0.AddMinutes(1));
        var preCompact = Path.Combine(sandbox.Home.BackupDir, compact.SnapshotName!);

        Assert.Equal(1, compact.ClosedPruned);
        Assert.Contains("class Bar v1", ClosedRegenerable(preCompact));
        Assert.Equal(new BackupMarkers(BackupMarkers.AuthoredScope, 0), Markers(preCompact));
    }

    /// <summary>§9 negative case: a store still at v19 skips 2b with a note; Phase 1 still applies.</summary>
    [Fact]
    public void AuthoredTake_OfAVersion19Store_KeepsClosedHistory_AndSaysWhy()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Seed(connection);
            Sql(connection,
                """
                DROP INDEX ix_fact_superseded_by;
                DROP INDEX ix_fact_object_ref;
                UPDATE schema_meta SET value = '19' WHERE key = 'schema_version';
                """);
        }

        BackupSnapshot slim;
        using (var old = EngramDatabase.Open(sandbox.Home))
        {
            slim = BackupStore.Take(old, sandbox.Home, T0, BackupScope.Authored);
        }

        Assert.Contains("closed code history was kept", slim.Warning, StringComparison.Ordinal);
        Assert.Equal(["class Bar v1", "class Baz as indexed"], ClosedRegenerable(slim.Path));
        Assert.Equal(new BackupMarkers(BackupMarkers.AuthoredScope, 0), Markers(slim.Path));
        using (var copy = EngramDatabase.Open(slim.Path))
        {
            Assert.DoesNotContain("facts_closed", EngramDatabase.ReadMeta(copy, BackupMarkers.OmittedKey), StringComparison.Ordinal);
        }

        EngramDatabase.ReleasePooledConnections(slim.Path);
        Assert.DoesNotContain("not recoverable", Cli(sandbox.Home, "restore", slim.Name).Stdout, StringComparison.Ordinal);
    }

    /// <summary>§9 I4 for 2b: a compaction that fails on the copy keeps a full snapshot, history included.</summary>
    [Fact]
    public void ACompactionThatFailsOnTheCopy_KeepsAFullSnapshot()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Seed(connection);
        Sql(connection, "CREATE TRIGGER planted_block BEFORE DELETE ON fact BEGIN SELECT RAISE(ABORT, 'planted'); END;");

        var snapshot = BackupStore.Take(connection, sandbox.Home, T0, BackupScope.Authored);

        Assert.Contains("kept full", snapshot.Warning, StringComparison.Ordinal);
        Assert.False(Markers(snapshot.Path).IsMarked);
        Assert.Equal(["class Bar v1", "class Baz as indexed"], ClosedRegenerable(snapshot.Path));
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
        File.WriteAllText(sandbox.Home.ConfigPath, "[embedding]\nprovider = \"local\"\n");

        var dry = Cli(sandbox.Home, "restore", slim.Name);
        Assert.Equal(0, dry.Exit);
        Assert.Contains("This snapshot leaves out state Engram rebuilds:", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains($"{embeddable} facts to embed", dry.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("embedding is off", dry.Stdout, StringComparison.Ordinal);
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

    // --- 2a: the authored journal ------------------------------------------------------------

    private static List<System.Text.Json.Nodes.JsonObject> Lines(string path) =>
        File.ReadLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(l)!)
            .ToList();

    [Fact]
    public void AuthoredJournal_KeepsAuthoredFactsAndOnlyTheRegenerableOnesAuthoredTruthNeeds()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Seed(connection);
        }

        Assert.Equal(0, Cli(sandbox.Home, "take").Exit);

        var lines = Lines(FactJournal.PathIn(sandbox.Home));
        var regenerable = lines.Skip(1)
            .Where(l => l["regenerable"]!.GetValue<bool>())
            .Select(l => l["body"]!.GetValue<string>())
            .Order(StringComparer.Ordinal)
            .ToList();

        // Kept: a supersession edge to an authored fact, and one named by each side table.
        // Left out: a live code fact and a regenerable-only rewrite chain — the snapshot has them.
        Assert.Equal(["class Baz as indexed", "class Corge", "class Quux", "class Qux"], regenerable);
        Assert.Contains(lines.Skip(1), l => l["body"]!.GetValue<string>() == "tea in the morning");

        foreach (var file in new[] { FactJournal.FileName, RelationJournal.FileName, SyncRequestJournal.FileName, ReviewJournal.FileName })
        {
            Assert.Equal("authored", Lines(Path.Combine(sandbox.Home.BackupDir, file))[0]["scope"]!.GetValue<string>());
        }
    }

    /// <summary>
    /// I1 and I9: the journal replayed into a fresh initialised home reproduces authored truth, and
    /// every relation, review and sync line finds its fact in the same journal.
    /// </summary>
    [Fact]
    public void AuthoredJournal_ReplaysIntoAFreshHome_WithEveryReferenceResolved()
    {
        using var source = new SandboxHome(initialize: false);
        IReadOnlyDictionary<string, IReadOnlyList<string>> expected;
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Seed(connection);
            expected = AuthoredDigest.Read(connection, journalSubset: true);
        }

        Assert.Equal(0, Cli(source.Home, "take").Exit);

        using var target = new SandboxHome();

        // The target's own seed, read before the replay: a second freshly initialized home would
        // carry the same seed stamped with its own clock, and differ whenever the two land in
        // different seconds.
        IReadOnlyDictionary<string, IReadOnlyList<string>> seeded;
        using (var fresh = EngramDatabase.OpenInitialized(target.Home))
        {
            seeded = AuthoredDigest.Read(fresh, journalSubset: true);
        }

        var replay = Cli(target.Home, "replay", FactJournal.PathIn(source.Home), "--apply");

        Assert.Equal(0, replay.Exit);
        Assert.DoesNotContain("could not", replay.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("pointed outside", replay.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("outside this journal", replay.Stdout, StringComparison.Ordinal);

        using var replayed = EngramDatabase.OpenInitialized(target.Home);
        var actual = AuthoredDigest.Read(replayed, journalSubset: true)
            .ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.Except(seeded[p.Key]).ToList());
        AuthoredDigest.Equal(expected, actual);
    }

    /// <summary>
    /// A TextWriter that writes a fact into the live store the moment the snapshot is reported —
    /// after the copy, before the journal. A journal read from the live store would carry it.
    /// </summary>
    private sealed class WriteAfterSnapshot(EngramHome home) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.StartsWith("Wrote ", StringComparison.Ordinal) == true)
            {
                using var other = EngramDatabase.Open(home, pooling: false);
                Remember(other, "/knowledge/late", "states", "written after the snapshot", T0.AddHours(5));
            }
        }
    }

    [Fact]
    public void Journal_IsWrittenFromTheSnapshot_NotTheLiveStore()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Seed(connection);

        var snapshot = Engram.Cli.BackupCommand.TakeNow(
            sandbox.Home,
            BackupSettings.Read(ConfigFile.Load(sandbox.Home.ConfigPath)),
            connection,
            BackupScope.Authored,
            new WriteAfterSnapshot(sandbox.Home),
            new StringWriter());

        Assert.NotNull(snapshot);
        Assert.Single(FactStore.SearchRanked(connection, "snapshot", 10));
        Assert.DoesNotContain("written after the snapshot", File.ReadAllText(FactJournal.PathIn(sandbox.Home)), StringComparison.Ordinal);
    }

    [Fact]
    public void Replay_OfAnAuthoredJournal_SaysWhatIsNotInIt()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Seed(connection);
        }

        Cli(source.Home, "take");
        using var target = new SandboxHome();

        var dry = Cli(target.Home, "replay", FactJournal.PathIn(source.Home));

        Assert.Contains("Journal scope: authored.", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("code facts are not in this journal", dry.Stdout, StringComparison.Ordinal);
    }

    /// <summary>I7: a journal whose header has no scope is a full one, and replay says only that.</summary>
    [Fact]
    public void Replay_OfAJournalWithNoScope_ReadsAsFull()
    {
        using var source = new SandboxHome(initialize: false);
        var journal = Path.Combine(source.Home.Root, "old-facts.jsonl");
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Seed(connection);
            using var writer = new StreamWriter(journal);
            FactJournal.WriteTo(connection, writer, pathPrefix: null, T0);
        }

        using var target = new SandboxHome();
        var dry = Cli(target.Home, "replay", journal);

        Assert.Contains("Journal scope: full.", dry.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("code facts are not", dry.Stdout, StringComparison.Ordinal);
    }

    /// <summary>Export keeps every fact and writes no scope: the filter is reachable only from backup.</summary>
    [Fact]
    public void Export_IsUnchanged_AllFactsAndNoScope()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Seed(connection);
        var writer = new StringWriter();

        FactJournal.WriteTo(connection, writer, pathPrefix: null, T0);

        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain("\"scope\":\"authored\"", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains("class Foo", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("class Bar v1", StringComparison.Ordinal));
    }

    // --- I7: artifacts written by main (3435ad3) restore and replay as before ---------------

    private static readonly string MainFixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "main-3435ad3");
    private const string MainSnapshot = "engram-20261119T202344Z-v19.db";

    [Fact]
    public void ASnapshotWrittenByMain_ListsAsFull_AndRestoresExactly_WithNoOmissionNotice()
    {
        using var sandbox = new SandboxHome(initialize: false);
        Directory.CreateDirectory(sandbox.Home.BackupDir);
        var copy = Path.Combine(sandbox.Home.BackupDir, MainSnapshot);
        File.Copy(Path.Combine(MainFixtures, MainSnapshot), copy);
        var expected = DigestOf(copy);

        var list = Cli(sandbox.Home, "list");
        var restore = Cli(sandbox.Home, "restore", MainSnapshot, "--apply");

        Assert.Contains(" full ", list.Stdout, StringComparison.Ordinal);
        Assert.Equal(0, restore.Exit);
        Assert.DoesNotContain("leaves out", restore.Stdout, StringComparison.Ordinal);
        AuthoredDigest.Equal(expected, DigestOf(sandbox.Home.DatabasePath));
    }

    [Fact]
    public void AJournalWrittenByMain_ReplaysAsFull_RegenerableLinesIncluded()
    {
        using var target = new SandboxHome(initialize: false);
        var snapshot = Path.Combine(MainFixtures, MainSnapshot);
        IReadOnlyDictionary<string, IReadOnlyList<string>> expected;
        int regenerable;
        try
        {
            using var source = EngramDatabase.Open(snapshot);
            expected = AuthoredDigest.Read(source, journalSubset: true);
            using var count = source.CreateCommand();
            count.CommandText = "SELECT count(*) FROM fact WHERE regenerable = 1;";
            regenerable = (int)(long)count.ExecuteScalar()!;
        }
        finally
        {
            EngramDatabase.ReleasePooledConnections(snapshot);
        }

        var replay = Cli(target.Home, "replay", Path.Combine(MainFixtures, FactJournal.FileName), "--apply");

        Assert.Equal(0, replay.Exit);
        Assert.Contains("Journal scope: full.", replay.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("code facts are not", replay.Stdout, StringComparison.Ordinal);
        using var replayed = EngramDatabase.Open(target.Home);
        AuthoredDigest.Equal(expected, AuthoredDigest.Read(replayed, journalSubset: true));
        using var replayedCount = replayed.CreateCommand();
        replayedCount.CommandText = "SELECT count(*) FROM fact WHERE regenerable = 1;";
        Assert.Equal(3, regenerable);
        Assert.Equal(regenerable, (int)(long)replayedCount.ExecuteScalar()!);
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
