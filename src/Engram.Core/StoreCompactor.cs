using Microsoft.Data.Sqlite;

namespace Engram.Core;

public sealed record CompactReport(
    bool Applied,
    string? Path,
    int LivePruned,
    int ClosedPruned,
    int ProtectedByAuthoredHistory,
    int SupersessionsRemoved,
    int VectorsRemoved,
    int EntitiesRemoved,
    int EntitiesKeptForRenameHistory,
    int FileStatesRemoved,
    int ReposDeregistered,
    string? SnapshotName,
    bool Vacuumed,
    IReadOnlyList<string> Notes,
    int AuthoredLeftInPlace = 0,
    IReadOnlyList<string>? AuthoredPaths = null)
{
    public int FactsPruned => LivePruned + ClosedPruned;

    public bool AnythingToPrune =>
        FactsPruned > 0 || SupersessionsRemoved > 0 || VectorsRemoved > 0
        || EntitiesRemoved > 0 || FileStatesRemoved > 0 || ReposDeregistered > 0;
}

/// <summary>A retired registry row <c>compact --retired</c> left alone this run, and why.</summary>
public sealed record RetiredSkip(string RepoPath, string Reason);

/// <summary>
/// What <c>compact --retired</c> found or did. <see cref="Prefixes"/> holds one report per
/// removed (or, in a dry run, removable) prefix; <see cref="Supersessions"/> and
/// <see cref="Vectors"/> are batch totals, because a supersession row or an orphaned vector is
/// not attributable to one prefix.
/// </summary>
public sealed record RetiredCompactReport(
    bool Applied,
    bool Auto,
    IReadOnlyList<CompactReport> Prefixes,
    IReadOnlyList<RetiredSkip> Skipped,
    int Supersessions,
    int Vectors,
    string? SnapshotName,
    bool Vacuumed,
    IReadOnlyList<string> Notes);

/// <summary>
/// Prunes what can be regenerated, and only that (D8): rows with <c>regenerable = 1</c>,
/// keyed off that column alone and never <c>learned_via</c> or where a fact lives (D23).
/// Without <c>--path</c> it prunes closed regenerable facts — superseded index history,
/// which a re-index can produce again. With a path prefix it prunes the whole regenerable
/// subtree, live rows included: the detached-repo case, where the source is gone and the
/// facts describe files nobody can open.
/// </summary>
/// <remarks>
/// <para>A regenerable fact that shares a supersession edge with an authored fact is
/// excluded in both directions: revising a code fact into a belief, or correcting a belief
/// with one, makes the pair part of authored history, and deleting either half leaves the
/// survivor explaining a revision nobody can read.</para>
///
/// <para>Path mode also clears <c>file_state</c> for every file the prefix touches, and that
/// is load-bearing rather than tidy: pruned facts with surviving file state would make the
/// next index run see unchanged blob hashes and rewrite nothing — a silent, permanent loss.
/// A file's state goes when its facts go <em>or</em> when the prefix names something inside
/// it, because either way the file must be re-read to be whole again.</para>
///
/// <para>Deleting a closed fact requires schema version 3: the version 2 delete trigger
/// re-deleted an FTS entry <c>fact_fts_close</c> had already removed, and FTS5 fails the
/// statement with "database disk image is malformed".</para>
///
/// <para><c>--retired</c> (D97) is path mode applied to every retired registry row at once: the
/// same selection, the same predicates, the union of every prefix's targets in
/// <c>temp.compact_target</c> before any entity, file-state or registry delete runs, because
/// <see cref="EntitySql"/> looks through the target set.</para>
/// </remarks>
public static class StoreCompactor
{
    /// <summary>
    /// How long a retired row stays visible — in doctor and in the dry run — before
    /// <c>--auto</c> may remove it (D97). A constant, not a key: one knob is a rule.
    /// </summary>
    public const int AutoRemoveGraceDays = 7;

    /// <summary>
    /// The largest prefix <c>--auto</c> removes, in entities under it (D97, E7). Measured on a
    /// real-store copy: a removal's cost tracks entities (R² 0.929) and vectors (0.965), not
    /// facts (0.770), and the entity fit puts 2,000 at about 0.56 s. Entities rather than vectors
    /// because doctor can count them through <c>entity.path</c> without a fact scan, so the verb
    /// and doctor use one count (<see cref="EntitiesUnder"/>) and cannot disagree about a prefix.
    /// </summary>
    public const int AutoRemoveCap = 2_000;

    /// <summary>
    /// A transaction slower than this is outside the envelope D97 predicts for one automatic
    /// prefix; it still commits, and its <c>finished</c> record says <c>over_budget</c>.
    /// </summary>
    public static readonly TimeSpan AutoOverBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The E7 entity fit (165 µs per entity plus 232 ms), used only to tell someone typing the
    /// manual verb roughly how long other sessions' writes will fail.
    /// </summary>
    internal static TimeSpan EstimateWriteLock(long entities) =>
        TimeSpan.FromMilliseconds(232 + (entities * 0.165));

    /// <summary>The <c>trigger</c> a <see cref="TelemetryEventKind.RetiredRemoval"/> record carries.</summary>
    public const string TriggerAuto = "auto";

    /// <inheritdoc cref="TriggerAuto"/>
    public const string TriggerManual = "manual";

    /// <summary>
    /// The one normalisation of a <c>--path</c> prefix: trailing <c>/</c> stripped, and what is
    /// left empty becomes the root <c>/</c>. Null when the value is not a rooted memory path
    /// (empty, or not starting with <c>/</c>), which the verb reports as a usage error.
    /// </summary>
    public static string? NormalizePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.StartsWith('/'))
        {
            return null;
        }

        var trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    public static CompactReport Compact(
        SqliteConnection connection,
        EngramHome home,
        string? path,
        bool apply,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(home);

        var prefix = path is null
            ? null
            : NormalizePath(path) ?? throw new ArgumentException("a path starts with /", nameof(path));

        try
        {
            var measured = Measure(connection, null, [prefix], listAuthored: false);
            var report = measured.Single();

            if (!apply)
            {
                return report;
            }

            var snapshot = BackupStore.Take(connection, home, now, label: "pre-compact");
            BackupStore.PruneAfterWrite(home, snapshot);

            using (var transaction = EngramDatabase.BeginWrite(connection))
            {
                Delete(connection, transaction, [prefix], measured);
                transaction.Commit();
            }

            Execute(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
            Execute(connection, null, "VACUUM;");

            return report with
            {
                Applied = true,
                SnapshotName = Path.GetFileName(snapshot.Path),
                Vacuumed = true,
            };
        }
        finally
        {
            // Temp schema, so nothing in the store file — dropped anyway, because the
            // connection may be a pooled one with a long life ahead of it.
            Execute(connection, null, "DROP TABLE IF EXISTS temp.compact_target;");
        }
    }

    /// <summary>
    /// Seams a test needs to stage what only concurrency or a crash produces. Null in production.
    /// </summary>
    /// <param name="AfterCheck">Runs after the git re-check and before any write.</param>
    /// <param name="AfterPrefix">Runs after each prefix's removal commits.</param>
    /// <param name="BeforeCommit">Runs inside the write transaction, just before it commits.</param>
    internal sealed record RetiredHooks(
        Action? AfterCheck = null, Action<string>? AfterPrefix = null, Action? BeforeCommit = null);

    /// <summary>
    /// <c>engram compact --retired</c> (D97). Removes what every retired registry row left
    /// behind, through path mode's own predicates. A row is removed only when it passes the
    /// re-check — its checkout is on disk, git resolves it positively now, and a live row at that
    /// checkout carries the resolved identity and last scanned completely — so a repo absent on
    /// this machine, or a git failure at apply time, never deletes anything.
    /// </summary>
    /// <param name="auto">
    /// The session-start mode: at most <b>one</b> prefix per run — the oldest retirement past
    /// <see cref="AutoRemoveGraceDays"/> with at most <see cref="AutoRemoveCap"/> entities — in one
    /// transaction, then a passive checkpoint and no VACUUM. One prefix, because back-to-back
    /// transactions starve a writer polling in its busy handler (E6: 6 of 6 failed across 25
    /// prefixes). Requires <paramref name="apply"/>: an ambient dry run has no reader.
    /// </param>
    /// <param name="beforeWrite">
    /// Manual apply only: called with the estimated write-lock time before the snapshot, so the
    /// verb can say that other sessions' writes will fail until it finishes.
    /// </param>
    public static RetiredCompactReport CompactRetired(
        SqliteConnection connection,
        EngramHome home,
        bool apply,
        bool auto,
        DateTimeOffset now,
        Action<TimeSpan>? beforeWrite = null) =>
        CompactRetired(connection, home, apply, auto, now, beforeWrite, hooks: null);

    internal static RetiredCompactReport CompactRetired(
        SqliteConnection connection,
        EngramHome home,
        bool apply,
        bool auto,
        DateTimeOffset now,
        Action<TimeSpan>? beforeWrite,
        RetiredHooks? hooks)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(home);
        if (auto && !apply)
        {
            throw new ArgumentException("--auto needs --apply", nameof(auto));
        }

        var skipped = new List<RetiredSkip>();
        var eligible = new List<Eligible>();
        var graceStart = now.AddDays(-AutoRemoveGraceDays).ToUnixTimeSeconds();

        // Oldest retirement first, which is the order --auto takes them in.
        foreach (var row in ReadRetired(connection))
        {
            if (auto && eligible.Count == 1)
            {
                skipped.Add(new RetiredSkip(row.RepoPath, "waiting: automatic removal takes one prefix per session start"));
                continue;
            }

            if (auto && row.DetachedAt > graceStart)
            {
                var due = DateTimeOffset.FromUnixTimeSeconds(row.DetachedAt).AddDays(AutoRemoveGraceDays);
                skipped.Add(new RetiredSkip(row.RepoPath, $"inside the {AutoRemoveGraceDays}-day grace period (removable from {due:yyyy-MM-dd})"));
                continue;
            }

            if (Recheck(connection, row, out var checkout, out var resolved) is { } reason)
            {
                skipped.Add(new RetiredSkip(row.RepoPath, reason));
                continue;
            }

            if (auto && EntitiesUnder(connection, row.RepoPath) is var entities && entities > AutoRemoveCap)
            {
                skipped.Add(new RetiredSkip(
                    row.RepoPath,
                    $"too large to remove automatically ({entities} entities; the cap is {AutoRemoveCap}) — run engram compact --retired --apply"));
                continue;
            }

            eligible.Add(new Eligible(row.RepoPath, checkout!, resolved!));
        }

        try
        {
            if (!apply)
            {
                var dry = Measure(connection, null, eligible.Select(e => (string?)e.RepoPath).ToList(), listAuthored: true);
                var dryNotes = dry.Notes.ToList();
                if (SyncScopeStopsResolving(connection, home, eligible.Select(e => e.RepoPath).ToHashSet(StringComparer.Ordinal)) is { } syncNote)
                {
                    dryNotes.Add(syncNote);
                }

                return new RetiredCompactReport(
                    Applied: false, auto, dry.PerPrefix, skipped, dry.Supersessions, dry.Vectors,
                    SnapshotName: null, Vacuumed: false, dryNotes);
            }

            if (eligible.Count == 0)
            {
                // No eligible prefix: no snapshot, no telemetry, no write (D69).
                return new RetiredCompactReport(
                    Applied: true, auto, [], skipped, 0, 0, SnapshotName: null, Vacuumed: false, []);
            }

            if (!auto)
            {
                beforeWrite?.Invoke(EstimateWriteLock(eligible.Sum(e => (long)EntitiesUnder(connection, e.RepoPath))));
            }

            hooks?.AfterCheck?.Invoke();

            var snapshot = BackupStore.Take(connection, home, now, label: "pre-compact");
            BackupStore.PruneAfterWrite(home, snapshot);

            var trigger = auto ? TriggerAuto : TriggerManual;
            Note(home, "started", trigger, repo: null);
            try
            {
                var removed = new List<CompactReport>();
                var notes = new List<string>();
                var supersessions = 0;
                var vectors = 0;
                var overBudget = false;

                // One transaction: the whole batch for the manual verb, the one chosen prefix for
                // --auto. Either way each prefix is whole or untouched.
                var survivors = new List<Eligible>();
                // T is the time the write lock is held: from BEGIN IMMEDIATE returning to commit.
                var timer = new System.Diagnostics.Stopwatch();
                using (var transaction = EngramDatabase.BeginWrite(connection))
                {
                    timer.Start();
                    // The git half of the re-check ran before the lock; this half runs under
                    // it, so an index that un-retired a row since then wins.
                    foreach (var candidate in eligible)
                    {
                        if (StillRetired(connection, transaction, candidate))
                        {
                            survivors.Add(candidate);
                        }
                        else
                        {
                            skipped.Add(new RetiredSkip(candidate.RepoPath, "no longer retired, or its winner is gone, since the check"));
                        }
                    }

                    if (survivors.Count > 0)
                    {
                        var prefixes = survivors.Select(s => (string?)s.RepoPath).ToList();
                        var measured = Measure(connection, transaction, prefixes, listAuthored: true);
                        Delete(connection, transaction, prefixes, measured);
                        hooks?.BeforeCommit?.Invoke();
                        transaction.Commit();
                        timer.Stop();

                        removed.AddRange(measured.PerPrefix.Select(r => r with { Applied = true }));
                        notes.AddRange(measured.Notes);
                        supersessions = measured.Supersessions;
                        vectors = measured.Vectors;
                    }
                }

                overBudget = auto && survivors.Count > 0 && timer.Elapsed > AutoOverBudget;
                foreach (var survivor in survivors)
                {
                    Note(home, "removed", trigger, survivor.RepoPath);
                    hooks?.AfterPrefix?.Invoke(survivor.RepoPath);
                }

                var vacuumed = false;
                if (auto)
                {
                    // SQLite reuses the freed pages; reclaiming disk stays with a manual compact.
                    Execute(connection, null, "PRAGMA wal_checkpoint(PASSIVE);");
                }
                else
                {
                    Execute(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
                    Execute(connection, null, "VACUUM;");
                    vacuumed = true;
                }

                Note(home, "finished", trigger, repo: null, overBudget);
                return new RetiredCompactReport(
                    Applied: true, auto, removed, skipped, supersessions, vectors,
                    Path.GetFileName(snapshot.Path), vacuumed, notes);
            }
            catch
            {
                Note(home, "failed", trigger, repo: null);
                throw;
            }
        }
        finally
        {
            Execute(connection, null, "DROP TABLE IF EXISTS temp.compact_target;");
        }
    }

    private sealed record RetiredRow(string RepoPath, string? DiskPath, long DetachedAt);

    private sealed record Eligible(string RepoPath, string Checkout, string Resolved);

    private static List<RetiredRow> ReadRetired(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT repo_path, disk_path, detached_at FROM repo_registry WHERE detached_at IS NOT NULL ORDER BY detached_at, repo_path;";
        using var reader = command.ExecuteReader();
        var rows = new List<RetiredRow>();
        while (reader.Read())
        {
            rows.Add(new RetiredRow(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2)));
        }

        return rows;
    }

    /// <summary>
    /// The apply-time re-check's first half, the one that shells out to git and so runs before
    /// the write transaction. One implementation for both modes. Null when the row passes,
    /// otherwise the reason it is skipped.
    /// </summary>
    private static string? Recheck(SqliteConnection connection, RetiredRow row, out string? checkout, out string? resolved)
    {
        checkout = null;
        resolved = null;

        if (row.DiskPath is null || !Directory.Exists(row.DiskPath))
        {
            return "checkout not on this machine";
        }

        checkout = PathCanonicalizer.Canonical(row.DiskPath);
        if (!CodeIndexer.TryResolveIdentity(checkout, out var identity, out var gitFailure))
        {
            return $"could not ask git for its origin ({gitFailure})";
        }

        resolved = identity;
        var winner = FindWinner(connection, null, checkout, identity);
        if (winner is null)
        {
            return $"no live registration at {checkout} carries its current origin ({identity})";
        }

        return winner.Value.SuppressedReason is { } suppressed
            ? $"the winning registration's last full scan was {suppressed}"
            : null;
    }

    /// <summary>
    /// The live row at <paramref name="checkout"/> whose identity is <paramref name="identity"/>,
    /// with its suppression reason; null when there is none.
    /// </summary>
    private static (string RepoPath, string? SuppressedReason)? FindWinner(
        SqliteConnection connection, SqliteTransaction? transaction, string checkout, string identity)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT repo_path, disk_path, last_scan_suppressed_reason FROM repo_registry
            WHERE identity = $identity AND detached_at IS NULL AND disk_path IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$identity", identity);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (PathCanonicalizer.Canonical(reader.GetString(1)) == checkout)
            {
                return (reader.GetString(0), reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }

        return null;
    }

    /// <summary>The re-check's second half, under the write lock.</summary>
    private static bool StillRetired(SqliteConnection connection, SqliteTransaction transaction, Eligible candidate)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT 1 FROM repo_registry WHERE repo_path = $p AND detached_at IS NOT NULL;";
            command.Parameters.AddWithValue("$p", candidate.RepoPath);
            if (command.ExecuteScalar() is null)
            {
                return false;
            }
        }

        // The same winner test as before the lock, suppression included, so a truncated scan that
        // lands in between keeps the row.
        return FindWinner(connection, transaction, candidate.Checkout, candidate.Resolved) is { SuppressedReason: null };
    }

    /// <summary>
    /// The configured <c>[sync]</c> <c>repo:</c> scope, when it resolves today and would resolve to
    /// nothing once <paramref name="removed"/> are gone (spec §5). Uses the one resolver sync uses.
    /// </summary>
    internal static string? SyncScopeStopsResolving(SqliteConnection connection, EngramHome home, IReadOnlySet<string> removed)
    {
        if (removed.Count == 0)
        {
            return null;
        }

        var scope = SyncSettings.Read(ConfigFile.Load(home.ConfigPath)).Scope;
        if (!SyncScope.TryParse(scope, out var kind, out var value, out _) || kind != SyncScopeKind.Repo)
        {
            return null;
        }

        var rows = Sync.ReadRepoRegistry(connection);
        if (!SyncScope.ResolveRepo(rows, value!).Found
            || SyncScope.ResolveRepo(rows.Where(r => !removed.Contains(r.RepoPath)).ToList(), value!).Found)
        {
            return null;
        }

        return $"[sync] scope '{scope}' would stop resolving: no enrolled repo would match '{value}', and sync export would stop";
    }

    private static void Note(EngramHome home, string phase, string trigger, string? repo, bool overBudget = false)
    {
        if (File.Exists(home.ConfigPath))
        {
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTimeOffset.UtcNow.ToString("o"),
                SessionId: "cli",
                Kind: TelemetryEventKind.RetiredRemoval,
                Phase: phase,
                Repo: repo,
                Trigger: trigger,
                OverBudget: overBudget ? true : null));
        }
    }

    /// <summary>
    /// Entities under a prefix, any kind: the size <see cref="AutoRemoveCap"/> is measured in.
    /// The one count the verb and doctor share, served by <c>entity.path</c>'s unique index.
    /// </summary>
    internal static int EntitiesUnder(SqliteConnection connection, string prefix) =>
        Scalar(connection, null, $"SELECT count(*) FROM entity WHERE {Subtree("entity.path")};", Parameters(prefix));

    /// <summary>
    /// Selection and counts for one or more modes, each a prefix or null (closed history). Fills
    /// <c>temp.compact_target</c> with the union of every mode's targets first, so every count
    /// that looks through the target set — entities above all — sees the whole batch.
    /// </summary>
    private sealed record Measured(
        IReadOnlyList<CompactReport> PerPrefix,
        int Supersessions,
        int Vectors,
        bool VectorsCountable,
        IReadOnlyList<string> Notes)
    {
        /// <summary>The one-mode report <see cref="Compact"/> returns.</summary>
        public CompactReport Single() =>
            PerPrefix[0] with { SupersessionsRemoved = Supersessions, VectorsRemoved = Vectors, Notes = Notes };
    }

    private static Measured Measure(
        SqliteConnection connection, SqliteTransaction? transaction, IReadOnlyList<string?> modes, bool listAuthored)
    {
        Execute(connection, transaction, "DROP TABLE IF EXISTS temp.compact_target;");
        Execute(
            connection,
            transaction,
            "CREATE TEMP TABLE compact_target (id INTEGER PRIMARY KEY, was_live INTEGER NOT NULL, mode INTEGER NOT NULL);");

        for (var i = 0; i < modes.Count; i++)
        {
            Execute(
                connection,
                transaction,
                "INSERT OR IGNORE INTO temp.compact_target (id, was_live, mode) "
                + $"SELECT id, valid_to IS NULL, {i} FROM fact "
                + $"WHERE {ModeSql(modes[i])} AND NOT {AuthoredDependencySql};",
                Parameters(modes[i]));
        }

        var notes = new List<string>();
        var supersessions = Scalar(
            connection,
            transaction,
            "SELECT count(*) FROM supersession WHERE old_fact_id IN (SELECT id FROM temp.compact_target) "
            + "OR new_fact_id IN (SELECT id FROM temp.compact_target);");

        // Orphaned vectors — rows whose fact is already gone — are swept alongside the
        // targets (D36 makes stray vector rows compact's problem). Counting touches the
        // vec0 module, which is loaded per connection and may simply not be installed;
        // that costs the sweep and nothing else.
        var vectors = 0;
        var vectorsCountable = false;
        if (VectorIndex.Exists(connection))
        {
            try
            {
                vectors = Scalar(
                    connection,
                    transaction,
                    $"SELECT count(*) FROM {VectorIndex.TableName} "
                    + "WHERE fact_id IN (SELECT id FROM temp.compact_target) "
                    + "OR fact_id NOT IN (SELECT id FROM fact);");
                vectorsCountable = true;
            }
            catch (SqliteException e) when (e.Message.Contains("no such module", StringComparison.OrdinalIgnoreCase))
            {
                notes.Add("a vector index exists but sqlite-vec is not loaded here; its rows were left alone");
            }
        }

        var perPrefix = new List<CompactReport>(modes.Count);
        for (var i = 0; i < modes.Count; i++)
        {
            var mode = modes[i];
            var parameters = Parameters(mode);
            var live = Scalar(connection, transaction, $"SELECT count(*) FROM temp.compact_target WHERE was_live = 1 AND mode = {i};");
            var closed = Scalar(connection, transaction, $"SELECT count(*) FROM temp.compact_target WHERE was_live = 0 AND mode = {i};");
            var protectedCount = Scalar(
                connection,
                transaction,
                $"SELECT count(*) FROM fact WHERE {ModeSql(mode)} AND {AuthoredDependencySql};",
                parameters);

            var entities = 0;
            var entitiesKept = 0;
            var fileStates = 0;
            var repos = 0;
            var authoredLeft = 0;
            var authoredPaths = new List<string>();
            if (mode is not null)
            {
                entities = Scalar(connection, transaction, EntitySql("SELECT count(*) FROM entity"), parameters);
                entitiesKept = Scalar(connection, transaction, EntitySql("SELECT count(*) FROM entity", aliased: true), parameters);
                fileStates = Scalar(connection, transaction, FileStateSql("SELECT count(*) FROM file_state"), parameters);
                repos = Scalar(connection, transaction, RegistrySql("SELECT count(*) FROM repo_registry"), parameters);

                // repo_enrollment is authored truth — the user's yes/no/later decision — never
                // residue of indexing like repo_registry. Counting or deleting it here would let
                // `compact` silently un-decline a repo the user declined (D8).

                if (listAuthored)
                {
                    // Authored facts under the prefix stay at their path, unchanged (U3); the
                    // report says how many and where, so nobody mistakes them for residue.
                    authoredLeft = Scalar(connection, transaction, $"SELECT count(*) FROM fact WHERE regenerable = 0 AND {Subtree("fact.path")};", parameters);
                    authoredPaths = Texts(
                        connection,
                        transaction,
                        $"SELECT DISTINCT path FROM fact WHERE regenerable = 0 AND {Subtree("fact.path")} ORDER BY path LIMIT 20;",
                        parameters);
                }
            }

            perPrefix.Add(new CompactReport(
                Applied: false,
                mode,
                live,
                closed,
                protectedCount,
                SupersessionsRemoved: 0,
                VectorsRemoved: vectorsCountable ? VectorsFor(connection, transaction, i) : 0,
                entities,
                entitiesKept,
                fileStates,
                repos,
                SnapshotName: null,
                Vacuumed: false,
                Notes: [],
                authoredLeft,
                authoredPaths));
        }

        return new Measured(perPrefix, supersessions, vectors, vectorsCountable, notes);
    }

    private static int VectorsFor(SqliteConnection connection, SqliteTransaction? transaction, int mode) =>
        Scalar(
            connection,
            transaction,
            $"SELECT count(*) FROM {VectorIndex.TableName} WHERE fact_id IN (SELECT id FROM temp.compact_target WHERE mode = {mode});");

    private static void Delete(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<string?> modes, Measured measured)
    {
        Execute(
            connection,
            transaction,
            "DELETE FROM supersession WHERE old_fact_id IN (SELECT id FROM temp.compact_target) "
            + "OR new_fact_id IN (SELECT id FROM temp.compact_target);");

        // A surviving fact may point into the target through a regenerable-only
        // chain; the pointer has to go before the row it names does, or the delete
        // trips foreign keys. Its valid_to stays — clearing the pointer does not
        // reopen the belief.
        Execute(
            connection,
            transaction,
            "UPDATE fact SET superseded_by = NULL "
            + "WHERE superseded_by IN (SELECT id FROM temp.compact_target) "
            + "AND id NOT IN (SELECT id FROM temp.compact_target);");

        Execute(connection, transaction, "DELETE FROM fact WHERE id IN (SELECT id FROM temp.compact_target);");

        if (measured.VectorsCountable && measured.Vectors > 0)
        {
            Execute(
                connection,
                transaction,
                $"DELETE FROM {VectorIndex.TableName} WHERE fact_id NOT IN (SELECT id FROM fact);");
        }

        // After every fact delete, so EntitySql's "no remaining reference" sees the whole batch.
        foreach (var mode in modes)
        {
            if (mode is null)
            {
                continue;
            }

            var parameters = Parameters(mode);
            Execute(connection, transaction, EntitySql("DELETE FROM entity"), parameters);
            Execute(connection, transaction, FileStateSql("DELETE FROM file_state"), parameters);
            Execute(connection, transaction, RegistrySql("DELETE FROM repo_registry"), parameters);

            // repo_enrollment must not appear above: it is authored truth, not derived
            // state, and this method may only touch what can be regenerated (D8).
        }
    }

    /// <summary>
    /// The regenerable facts a mode selects — the one predicate the selection and the protected
    /// count share.
    /// </summary>
    /// <remarks>
    /// In path mode the unary plus disqualifies <c>ix_fact_regenerable</c>, so the planner serves
    /// <see cref="Subtree"/> from <c>ix_fact_path</c> instead (D97, the remedy D92 used for
    /// <c>PruneIneligible</c>). Without it, and with no <c>sqlite_stat1</c>, the planner takes the
    /// equality on the partial index and walks every regenerable fact: measured on the real store,
    /// 130–143 ms against 21.6–22.8 ms for the smallest prefix (process floor about 22 ms). Closed
    /// mode keeps the plain form; its selection has no path to serve it.
    /// </remarks>
    private static string ModeSql(string? prefix) =>
        prefix is null
            ? "regenerable = 1 AND valid_to IS NOT NULL"
            : $"+regenerable = 1 AND {Subtree("fact.path")}";

    /// <summary>The path-mode selection, exposed so a test can pin its plan.</summary>
    internal static string PathModeSelectionSql => $"SELECT id FROM fact WHERE {ModeSql("/")} AND NOT {AuthoredDependencySql};";

    /// <summary>
    /// The SQL value of a normalised prefix. The root <c>/</c> binds as the empty string, which
    /// makes every boundary predicate below read "every path starting with <c>/</c>" — the root
    /// is a parameter form, never a second definition of "under".
    /// </summary>
    private static (string Name, object Value)[] Parameters(string? prefix) =>
        prefix is null ? [] : [("$p", prefix == "/" ? string.Empty : prefix)];

    /// <summary>An edge in either direction to a fact that cannot be regenerated.</summary>
    private const string AuthoredEdgeSql =
        "EXISTS (SELECT 1 FROM supersession s "
        + "JOIN fact other ON other.id = "
        + "CASE WHEN s.old_fact_id = fact.id THEN s.new_fact_id ELSE s.old_fact_id END "
        + "WHERE (s.old_fact_id = fact.id OR s.new_fact_id = fact.id) "
        + "AND other.regenerable = 0)";

    /// <summary>
    /// Authored truth depends on this fact: it shares a supersession edge with an authored
    /// fact, or a judged relation (either end), a review date or a sync request names it. None
    /// of those three tables cascades, so deleting such a fact would fail the whole call on a
    /// foreign key, and none of them is regenerated by an index run. A predicate on the outer
    /// <c>fact</c> row. One EXISTS per column, so each probe uses that column's index.
    /// </summary>
    internal const string AuthoredDependencySql =
        "(" + AuthoredEdgeSql
        + " OR EXISTS (SELECT 1 FROM fact_relation r WHERE r.fact_id = fact.id)"
        + " OR EXISTS (SELECT 1 FROM fact_relation r WHERE r.related_id = fact.id)"
        + " OR EXISTS (SELECT 1 FROM fact_review v WHERE v.fact_id = fact.id)"
        + " OR EXISTS (SELECT 1 FROM fact_sync_request q WHERE q.fact_id = fact.id))";

    /// <summary>
    /// <paramref name="column"/> equals the prefix or sits under it, where "under" crosses
    /// a segment boundary — <c>/</c> into a subtree, <c>#</c> into a file's symbols — so
    /// <c>/code/api</c> never captures <c>/code/api-v2</c>. Bound to the empty string (the
    /// root, see <see cref="Parameters"/>) it matches every path that starts with <c>/</c>.
    /// </summary>
    /// <remarks>
    /// Written as an equality and two half-open ranges — <c>[$p/, $p0)</c> and <c>[$p#, $p$)</c>,
    /// each upper bound the byte after the separator — so an index on the column can serve it
    /// (D97, A3; D95's technique). The <c>substr</c> form it replaced meant the same thing and
    /// could not: every prefix's selection scanned every regenerable fact, 379–805 ms per prefix
    /// on the real store. BINARY collation is what makes the byte ranges mean "starts with".
    /// </remarks>
    internal static string Subtree(string column) =>
        $"({column} = $p "
        + $"OR ({column} >= ($p || '/') AND {column} < ($p || '0')) "
        + $"OR ({column} >= ($p || '#') AND {column} < ($p || '$')))";

    /// <summary>
    /// Code entities under the prefix that no surviving fact references. <paramref name="aliased"/>
    /// false selects the ones to delete; true selects the ones kept only because a rename filed
    /// an alias against them, which <c>ON DELETE CASCADE</c> would otherwise remove silently and
    /// no index run rebuilds.
    /// </summary>
    /// <remarks>
    /// <c>+kind</c> for the reason <see cref="ModeSql"/> has <c>+regenerable</c>: otherwise the
    /// planner walks every code entity through <c>ix_entity_kind</c> rather than serving
    /// <see cref="Subtree"/> from the path index — measured on the real store, 78–104 ms against
    /// 23–27 ms for the smallest retired prefix (process floor about 22 ms).
    /// </remarks>
    internal static string EntitySql(string head, bool aliased = false) =>
        $"{head} WHERE +kind IN ('repo', 'module', 'file', 'symbol', 'section') "
        + $"AND {Subtree("entity.path")} "
        // Written to hold before and after the fact delete: in a dry run the targets still
        // exist, so "no remaining reference" has to look through them. Two NOT EXISTS rather
        // than one with an OR: the OR kept either index from being used and scanned fact once
        // per entity (1,340 entities took 76.8 s).
        + "AND NOT EXISTS (SELECT 1 FROM fact WHERE fact.subject_id = entity.id "
        + "AND fact.id NOT IN (SELECT id FROM temp.compact_target)) "
        + "AND NOT EXISTS (SELECT 1 FROM fact WHERE fact.object_id = entity.id "
        + "AND fact.id NOT IN (SELECT id FROM temp.compact_target)) "
        + $"AND {(aliased ? string.Empty : "NOT ")}EXISTS (SELECT 1 FROM entity_alias a WHERE a.entity_id = entity.id);";

    /// <summary>
    /// A file's memory path is <c>repo_path || '/' || path</c> (the grammar's ForFile). The
    /// prefix relation runs both ways: the file under the prefix, or the prefix naming a
    /// symbol inside the file — either way the file must be re-read on the next index run.
    /// </summary>
    private static string FileStateSql(string head) =>
        $"{head} WHERE {Subtree("(repo_path || '/' || path)")} "
        + "OR (substr($p, 1, length(repo_path || '/' || path)) = (repo_path || '/' || path) "
        + "AND substr($p, length(repo_path || '/' || path) + 1, 1) IN ('/', '#'));";

    /// <summary>A repo path is never a symbol, so only the <c>/</c> range of <see cref="Subtree"/>.</summary>
    private static string RegistrySql(string head) =>
        $"{head} WHERE repo_path = $p "
        + "OR (repo_path >= ($p || '/') AND repo_path < ($p || '0'));";

    private static int Scalar(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (int)(long)command.ExecuteScalar()!;
    }

    private static List<string> Texts(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}
