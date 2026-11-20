using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// D97: <c>compact --retired</c> (manual and <c>--auto</c>) and <c>compact --path /</c>. Retired rows
/// are produced the real way — a full scan, a remote repointed, a second full scan — so the
/// re-check runs against real git and real checkouts.
/// </summary>
[Collection(SqlitePoolCollection.Name)]
public class RetiredRemovalTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    // ---- the switch -----------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("\"yes\"")]
    public void Auto_KeyOffAbsentOrUnparseable_IsASilentNoOp(string? value)
    {
        using var sandbox = new SandboxHome();
        if (value is not null)
        {
            SetKey(sandbox, value);
        }

        var retired = Retire(sandbox, "noop", daysAgo: 8);
        var before = FactsUnder(sandbox, retired.RetiredPath);

        var (exit, stdout, _) = Cli(sandbox, "--retired", "--apply", "--auto");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(before, FactsUnder(sandbox, retired.RetiredPath));
        Assert.Empty(Snapshots(sandbox));
        Assert.Empty(RemovalEvents(sandbox));
    }

    [Fact]
    public void Auto_UnparseableKey_IsAProblem_AndTrueIsNot()
    {
        Assert.Contains(
            IndexingSettings.Read(ConfigFile.Parse("[indexing]\nauto_remove_retired = \"yes\"\n")).Problems,
            p => p.Contains("auto_remove_retired", StringComparison.Ordinal));

        var on = IndexingSettings.Read(ConfigFile.Parse("[indexing]\nauto_remove_retired = true\n"));
        Assert.True(on.AutoRemoveRetired);
        Assert.Empty(on.Problems);

        var absent = IndexingSettings.Read(ConfigFile.Parse("[indexing]\n"));
        Assert.False(absent.AutoRemoveRetired);
        Assert.Empty(absent.Problems);
    }

    [Fact]
    public void Auto_KeyOn_RemovesThroughTheVerb()
    {
        using var sandbox = new SandboxHome();
        SetKey(sandbox, "true");
        var retired = Retire(sandbox, "verb", daysAgo: 8);

        var (exit, _, _) = Cli(sandbox, "--retired", "--apply", "--auto");

        Assert.Equal(0, exit);
        Assert.Equal(0, FactsUnder(sandbox, retired.RetiredPath));
    }

    // ---- --auto bounds --------------------------------------------------------------------

    [Fact]
    public void Auto_InsideGrace_RemovesNothing_NoSnapshotNoTelemetry()
    {
        using var sandbox = new SandboxHome();
        var retired = Retire(sandbox, "grace", daysAgo: 6);
        var before = FactsUnder(sandbox, retired.RetiredPath);

        var report = Run(sandbox, apply: true, auto: true);

        Assert.Empty(report.Prefixes);
        Assert.Contains(report.Skipped, s => s.RepoPath == retired.RetiredPath && s.Reason.Contains("grace", StringComparison.Ordinal));
        Assert.Equal(before, FactsUnder(sandbox, retired.RetiredPath));
        Assert.Empty(Snapshots(sandbox));
        Assert.Empty(RemovalEvents(sandbox));
    }

    [Fact]
    public void Auto_PastGrace_RemovesThePrefix_WithSnapshotTelemetryAndNoVacuum()
    {
        using var sandbox = new SandboxHome();
        var retired = Retire(sandbox, "past", daysAgo: 8);
        var winnerBefore = FactsUnder(sandbox, retired.WinnerPath);
        Assert.True(FactsUnder(sandbox, retired.RetiredPath) > 0);

        var report = Run(sandbox, apply: true, auto: true);

        var removed = Assert.Single(report.Prefixes);
        Assert.Equal(retired.RetiredPath, removed.Path);
        Assert.False(report.Vacuumed);
        Assert.Equal(0, FactsUnder(sandbox, retired.RetiredPath));
        Assert.Equal(winnerBefore, FactsUnder(sandbox, retired.WinnerPath));
        Assert.Equal(0L, Count(sandbox, $"SELECT COUNT(*) FROM repo_registry WHERE repo_path = '{retired.RetiredPath}';"));
        Assert.Equal(1L, Count(sandbox, $"SELECT COUNT(*) FROM repo_registry WHERE repo_path = '{retired.WinnerPath}';"));
        Assert.Single(Snapshots(sandbox));

        // No VACUUM: the freed pages are still on the freelist.
        Assert.True(Count(sandbox, "PRAGMA freelist_count;") > 0);

        var events = RemovalEvents(sandbox);
        Assert.Equal(["started", "removed", "finished"], events.Select(e => e.Phase));
        Assert.All(events, e => Assert.Equal(StoreCompactor.TriggerAuto, e.Trigger));
        Assert.Equal(retired.RetiredPath, events[1].Repo);
        Assert.All(events, e => Assert.Null(e.FactCount));
        Assert.All(events, e => Assert.Null(e.OverBudget));
    }

    [Fact]
    public void Auto_TakesOnePrefixPerRun_TheOldestRetirement()
    {
        using var sandbox = new SandboxHome();
        var middle = Retire(sandbox, "one-b", daysAgo: 9);
        var oldest = Retire(sandbox, "one-c", daysAgo: 12);
        var newest = Retire(sandbox, "one-a", daysAgo: 8);

        var report = Run(sandbox, apply: true, auto: true);

        Assert.Equal(oldest.RetiredPath, Assert.Single(report.Prefixes).Path);
        Assert.Equal(0, FactsUnder(sandbox, oldest.RetiredPath));
        Assert.True(FactsUnder(sandbox, middle.RetiredPath) > 0);
        Assert.True(FactsUnder(sandbox, newest.RetiredPath) > 0);

        Assert.Equal(middle.RetiredPath, Assert.Single(Run(sandbox, apply: true, auto: true).Prefixes).Path);
        Assert.Equal(newest.RetiredPath, Assert.Single(Run(sandbox, apply: true, auto: true).Prefixes).Path);
    }

    [Fact]
    public void Auto_OldestOverTheCap_IsSkipped_AndTheNextIsRemoved()
    {
        using var sandbox = new SandboxHome();
        var large = Retire(sandbox, "skip-large", daysAgo: 12);
        var small = Retire(sandbox, "skip-small", daysAgo: 8);
        Pad(sandbox, large.RetiredPath, StoreCompactor.AutoRemoveCap + 1);

        var report = Run(sandbox, apply: true, auto: true);

        Assert.Equal(small.RetiredPath, Assert.Single(report.Prefixes).Path);
        Assert.Contains(report.Skipped, s => s.RepoPath == large.RetiredPath && s.Reason.Contains("too large", StringComparison.Ordinal));
        Assert.True(FactsUnder(sandbox, large.RetiredPath) > 0);
    }

    [Fact]
    public void Auto_TransactionOverTheBudget_StillCommits_AndSaysSo()
    {
        using var sandbox = new SandboxHome();
        var retired = Retire(sandbox, "slow", daysAgo: 8);

        Run(sandbox, apply: true, auto: true,
            new StoreCompactor.RetiredHooks(BeforeCommit: () => Thread.Sleep(StoreCompactor.AutoOverBudget + TimeSpan.FromMilliseconds(100))));

        Assert.Equal(0, FactsUnder(sandbox, retired.RetiredPath));
        var finished = Assert.Single(RemovalEvents(sandbox), e => e.Phase == "finished");
        Assert.True(finished.OverBudget);
    }

    [Fact]
    public void Manual_Apply_PrintsTheWriterWarningFirst()
    {
        using var sandbox = new SandboxHome();
        Retire(sandbox, "warn", daysAgo: 0);

        var (exit, stdout, _) = Cli(sandbox, "--retired", "--apply");

        Assert.Equal(0, exit);
        Assert.StartsWith("writes from running sessions (captures, remembers) will fail until this finishes, about ", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_DryRun_PrintsNoWriterWarning()
    {
        using var sandbox = new SandboxHome();
        Retire(sandbox, "nowarn", daysAgo: 0);

        var (_, stdout, _) = Cli(sandbox, "--retired");

        Assert.DoesNotContain("writes from running sessions", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Auto_OverTheCap_IsLeftForTheManualVerb()
    {
        using var sandbox = new SandboxHome();
        var retired = Retire(sandbox, "large", daysAgo: 8);
        Pad(sandbox, retired.RetiredPath, StoreCompactor.AutoRemoveCap + 1);

        var auto = Run(sandbox, apply: true, auto: true);

        Assert.Empty(auto.Prefixes);
        Assert.Contains(auto.Skipped, s => s.Reason.Contains("too large", StringComparison.Ordinal));
        Assert.True(FactsUnder(sandbox, retired.RetiredPath) > StoreCompactor.AutoRemoveCap);
        Assert.Empty(Snapshots(sandbox));

        var manual = Run(sandbox, apply: true, auto: false);

        Assert.Single(manual.Prefixes);
        Assert.Equal(0, FactsUnder(sandbox, retired.RetiredPath));
    }

    [Fact]
    public void Auto_CrashBetweenPrefixes_LeavesWholePrefixes_AndTheNextRunFinishes()
    {
        using var sandbox = new SandboxHome();
        var first = Retire(sandbox, "crash-a", daysAgo: 8);
        var second = Retire(sandbox, "crash-b", daysAgo: 8);
        var ordered = new[] { first, second }.OrderBy(r => r.RetiredPath, StringComparer.Ordinal).ToArray();
        var secondBefore = FactsUnder(sandbox, ordered[1].RetiredPath);

        Assert.Throws<InvalidOperationException>(() => Run(
            sandbox, apply: true, auto: true,
            new StoreCompactor.RetiredHooks(AfterPrefix: _ => throw new InvalidOperationException("crash"))));

        Assert.Equal(0, FactsUnder(sandbox, ordered[0].RetiredPath));
        Assert.Equal(secondBefore, FactsUnder(sandbox, ordered[1].RetiredPath));
        var phases = RemovalEvents(sandbox).Select(e => e.Phase).ToList();
        Assert.Contains("started", phases);
        Assert.DoesNotContain("finished", phases);

        var next = Run(sandbox, apply: true, auto: true);

        Assert.Equal(ordered[1].RetiredPath, Assert.Single(next.Prefixes).Path);
        Assert.Equal(0, FactsUnder(sandbox, ordered[1].RetiredPath));
    }

    /// <summary>
    /// The manual batch is one transaction: a throw after it commits cannot leave half of it.
    /// The nearest contrast to the auto crash test above.
    /// </summary>
    [Fact]
    public void Manual_RemovesTheWholeBatchInOneTransaction()
    {
        using var sandbox = new SandboxHome();
        var first = Retire(sandbox, "batch-a", daysAgo: 0);
        var second = Retire(sandbox, "batch-b", daysAgo: 0);

        Assert.Throws<InvalidOperationException>(() => Run(
            sandbox, apply: true, auto: false,
            new StoreCompactor.RetiredHooks(AfterPrefix: _ => throw new InvalidOperationException("crash"))));

        Assert.Equal(0, FactsUnder(sandbox, first.RetiredPath));
        Assert.Equal(0, FactsUnder(sandbox, second.RetiredPath));
    }

    // ---- the re-check, both modes ----------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recheck_CheckoutAbsent_Skips(bool auto)
    {
        using var sandbox = new SandboxHome();
        var retired = Retire(sandbox, "absent", daysAgo: 8);
        var before = FactsUnder(sandbox, retired.RetiredPath);
        Directory.Delete(retired.Repo, recursive: true);

        var report = Run(sandbox, apply: true, auto);

        Assert.Empty(report.Prefixes);
        Assert.Contains(report.Skipped, s => s.Reason == "checkout not on this machine");
        Assert.Equal(before, FactsUnder(sandbox, retired.RetiredPath));
        Assert.Empty(Snapshots(sandbox));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recheck_GitUnknown_Skips(bool auto)
    {
        using var sandbox = new SandboxHome();
        var retired = Retire(sandbox, "unknown", daysAgo: 8);
        var before = FactsUnder(sandbox, retired.RetiredPath);
        File.AppendAllText(Path.Combine(retired.Repo, ".git", "config"), "[[[broken\n");

        var report = Run(sandbox, apply: true, auto);

        Assert.Empty(report.Prefixes);
        Assert.Contains(report.Skipped, s => s.Reason.Contains("could not ask git", StringComparison.Ordinal));
        Assert.Equal(before, FactsUnder(sandbox, retired.RetiredPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recheck_WinnerLastScanSuppressed_Skips(bool auto)
    {
        using var sandbox = new SandboxHome();
        var retired = Retire(sandbox, "suppressed", daysAgo: 8);
        var before = FactsUnder(sandbox, retired.RetiredPath);
        Exec(sandbox, $"UPDATE repo_registry SET last_scan_suppressed_reason = 'truncated' WHERE repo_path = '{retired.WinnerPath}';");

        var report = Run(sandbox, apply: true, auto);

        Assert.Empty(report.Prefixes);
        Assert.Contains(report.Skipped, s => s.Reason.Contains("truncated", StringComparison.Ordinal));
        Assert.Equal(before, FactsUnder(sandbox, retired.RetiredPath));
    }

    [Fact]
    public void Recheck_WinnerRetiredSince_Skips()
    {
        using var sandbox = new SandboxHome();
        var retired = Retire(sandbox, "winner-gone", daysAgo: 8);
        var before = FactsUnder(sandbox, retired.RetiredPath);
        Exec(sandbox, $"UPDATE repo_registry SET detached_at = 1 WHERE repo_path = '{retired.WinnerPath}';");

        var report = Run(sandbox, apply: true, auto: false);

        Assert.DoesNotContain(report.Prefixes, p => p.Path == retired.RetiredPath);
        Assert.Contains(report.Skipped, s => s.RepoPath == retired.RetiredPath && s.Reason.Contains("no live registration", StringComparison.Ordinal));
        Assert.Equal(before, FactsUnder(sandbox, retired.RetiredPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recheck_UnRetiredBetweenTheGitCheckAndTheTransaction_DeletesNothing(bool auto)
    {
        using var sandbox = new SandboxHome();
        var retired = Retire(sandbox, "race", daysAgo: 8);
        var before = FactsUnder(sandbox, retired.RetiredPath);

        var report = Run(
            sandbox, apply: true, auto,
            new StoreCompactor.RetiredHooks(AfterCheck: () =>
                Exec(sandbox, $"UPDATE repo_registry SET detached_at = NULL WHERE repo_path = '{retired.RetiredPath}';")));

        Assert.Empty(report.Prefixes);
        Assert.Contains(report.Skipped, s => s.Reason.Contains("since the check", StringComparison.Ordinal));
        Assert.Equal(before, FactsUnder(sandbox, retired.RetiredPath));
        Assert.DoesNotContain(RemovalEvents(sandbox), e => e.Phase == "removed");
    }

    // ---- what is never removed --------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthoredFactUnderThePrefix_IsKept_AndListed(bool auto)
    {
        using var sandbox = new SandboxHome();
        var retired = Retire(sandbox, "authored", daysAgo: 8);
        var note = retired.RetiredPath + "/Program.cs";
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            FactStore.Remember(connection, new FactWrite(note, "file", "states", "Keep the widget sealed.", "project", "stated"), Now);
        }

        var report = Run(sandbox, apply: true, auto);

        var removed = Assert.Single(report.Prefixes);
        Assert.Equal(1, removed.AuthoredLeftInPlace);
        Assert.Equal([note], removed.AuthoredPaths);
        Assert.Equal(1L, Count(sandbox, $"SELECT COUNT(*) FROM fact WHERE regenerable = 0 AND path = '{note}';"));
        Assert.Equal(0L, Count(sandbox, $"SELECT COUNT(*) FROM fact WHERE regenerable = 1 AND path LIKE '{retired.RetiredPath}/%';"));
    }

    [Fact]
    public void Removal_NeverTouchesEnrollment()
    {
        using var sandbox = new SandboxHome();
        var retired = Retire(sandbox, "enrolled", daysAgo: 8);
        Exec(sandbox, $"INSERT INTO repo_enrollment (identity, state, source, decided_at, last_root) VALUES ('{retired.RetiredIdentity}', 'enrolled', 'user', 1, '{retired.Repo}');");

        Run(sandbox, apply: true, auto: false);

        Assert.Equal(1L, Count(sandbox, $"SELECT COUNT(*) FROM repo_enrollment WHERE identity = '{retired.RetiredIdentity}';"));
    }

    // ---- manual mode ----------------------------------------------------------------------

    [Fact]
    public void Manual_DryRun_CountsWhatApplyRemoves_AndWritesNothing()
    {
        using var sandbox = new SandboxHome();
        var retired = Retire(sandbox, "dry", daysAgo: 0);
        var before = FactsUnder(sandbox, retired.RetiredPath);

        var dry = Run(sandbox, apply: false, auto: false);

        var counted = Assert.Single(dry.Prefixes);
        Assert.False(dry.Applied);
        Assert.Equal(before, counted.FactsPruned);
        Assert.Equal(before, FactsUnder(sandbox, retired.RetiredPath));
        Assert.Empty(Snapshots(sandbox));
        Assert.Empty(RemovalEvents(sandbox));

        var applied = Run(sandbox, apply: true, auto: false);

        Assert.Equal(counted with { Applied = true }, Assert.Single(applied.Prefixes), ReportComparer.Instance);
    }

    [Fact]
    public void Manual_Apply_IgnoresGraceAndCap_OneSnapshot_Vacuums_TriggerManual()
    {
        using var sandbox = new SandboxHome();
        var first = Retire(sandbox, "manual-a", daysAgo: 0);
        var second = Retire(sandbox, "manual-b", daysAgo: 0);

        var report = Run(sandbox, apply: true, auto: false);

        Assert.Equal(2, report.Prefixes.Count);
        Assert.True(report.Vacuumed);
        Assert.Equal(0, FactsUnder(sandbox, first.RetiredPath));
        Assert.Equal(0, FactsUnder(sandbox, second.RetiredPath));
        Assert.Single(Snapshots(sandbox));
        Assert.Equal(0L, Count(sandbox, "PRAGMA freelist_count;"));

        var events = RemovalEvents(sandbox);
        Assert.Equal(["started", "removed", "removed", "finished"], events.Select(e => e.Phase));
        Assert.All(events, e => Assert.Equal(StoreCompactor.TriggerManual, e.Trigger));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoRetiredRows_ReportsZero_NoSnapshot(bool apply)
    {
        using var sandbox = new SandboxHome();

        var report = Run(sandbox, apply, auto: false);

        Assert.Empty(report.Prefixes);
        Assert.Empty(report.Skipped);
        Assert.Empty(Snapshots(sandbox));
        Assert.Empty(RemovalEvents(sandbox));
    }

    // ---- usage ----------------------------------------------------------------------------

    [Theory]
    [InlineData("--retired", "--auto")]
    [InlineData("--path", "/projects/x", "--auto", "--apply")]
    [InlineData("--auto", "--apply")]
    [InlineData("--retired", "--path", "/projects/x")]
    [InlineData("--path", "")]
    [InlineData("--path", "projects/x")]
    public void UsageErrors_ExitTwo_AndCountNothing(params string[] args)
    {
        using var sandbox = new SandboxHome();

        var (exit, stdout, stderr) = Cli(sandbox, args);

        Assert.Equal(2, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.StartsWith("error:", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Usage_RetiredApplyAuto_IsAccepted()
    {
        using var sandbox = new SandboxHome();

        Assert.Equal(0, Cli(sandbox, "--retired", "--apply", "--auto").Exit);
        Assert.Equal(0, Cli(sandbox, "--retired").Exit);
    }

    // ---- --path / (3b) ---------------------------------------------------------------------

    [Theory]
    [InlineData("/", "/")]
    [InlineData("//", "/")]
    [InlineData("/projects/x/", "/projects/x")]
    [InlineData("/projects/x", "/projects/x")]
    [InlineData("", null)]
    [InlineData("projects/x", null)]
    public void NormalizePath_IsTheOneRule(string input, string? expected) =>
        Assert.Equal(expected, StoreCompactor.NormalizePath(input));

    [Theory]
    [InlineData("/")]
    [InlineData("//")]
    public void PathRoot_DryRun_CountsTheWholeRegenerableSubtree(string root)
    {
        using var sandbox = new SandboxHome();
        Retire(sandbox, "root", daysAgo: 0);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        FactStore.Remember(connection, new FactWrite("/knowledge/x", "note", "states", "Authored stays.", "project", "stated"), Now);

        var report = StoreCompactor.Compact(connection, sandbox.Home, root, apply: false, Now);

        Assert.Equal("/", report.Path);
        Assert.Equal(Count(sandbox, "SELECT COUNT(*) FROM fact WHERE regenerable = 1 AND valid_to IS NULL;"), report.LivePruned);
        Assert.Equal(Count(sandbox, "SELECT COUNT(*) FROM fact WHERE regenerable = 1 AND valid_to IS NOT NULL;"), report.ClosedPruned);
        Assert.Equal(Count(sandbox, "SELECT COUNT(*) FROM file_state;"), report.FileStatesRemoved);
        Assert.Equal(Count(sandbox, "SELECT COUNT(*) FROM repo_registry;"), report.ReposDeregistered);
        Assert.True(report.LivePruned > 0);
        Assert.True(report.EntitiesRemoved > 0);
    }

    [Fact]
    public void PathRoot_Apply_DropsTheCodeIndex_KeepsAuthoredAndEnrollment()
    {
        using var sandbox = new SandboxHome();
        var retired = Retire(sandbox, "root-apply", daysAgo: 0);
        Exec(sandbox, $"INSERT INTO repo_enrollment (identity, state, source, decided_at, last_root) VALUES ('{retired.RetiredIdentity}', 'enrolled', 'user', 1, '{retired.Repo}');");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        FactStore.Remember(connection, new FactWrite("/knowledge/x", "note", "states", "Authored stays.", "project", "stated"), Now);

        StoreCompactor.Compact(connection, sandbox.Home, "/", apply: true, Now);

        Assert.Equal(0L, Count(sandbox, "SELECT COUNT(*) FROM fact WHERE regenerable = 1;"));
        Assert.Equal(0L, Count(sandbox, "SELECT COUNT(*) FROM file_state;"));
        Assert.Equal(0L, Count(sandbox, "SELECT COUNT(*) FROM repo_registry;"));
        Assert.Equal(1L, Count(sandbox, "SELECT COUNT(*) FROM fact WHERE path = '/knowledge/x';"));
        Assert.Equal(1L, Count(sandbox, "SELECT COUNT(*) FROM repo_enrollment;"));
    }

    // ---- index-served Subtree (A3) ---------------------------------------------------------

    [Theory]
    [InlineData("/p/code/a", "/p/code/a", true)]
    [InlineData("/p/code/a", "/p/code/a/x", true)]
    [InlineData("/p/code/a", "/p/code/a#S", true)]
    [InlineData("/p/code/a", "/p/code/a-2", false)]
    [InlineData("/p/code/a", "/p/code/ab", false)]
    [InlineData("/p/code/a", "/p/code/a0", false)]
    [InlineData("/p/code/a", "/p/code/a$", false)]
    [InlineData("/p/code/a", "/p/code", false)]
    [InlineData("/", "/anything/at/all", true)]
    [InlineData("/", "/x#y", true)]
    public void Subtree_MatchesTheSameAsTheSubstrFormItReplaced(string prefix, string path, bool expected)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var bound = prefix == "/" ? string.Empty : prefix;

        Assert.Equal(expected, Matches(connection, StoreCompactor.Subtree("$x"), bound, path));
        Assert.Equal(expected, Matches(connection, OldSubtree, bound, path));
    }

    /// <summary>The form before A3, kept as the equivalence oracle.</summary>
    private const string OldSubtree =
        "($x = $p OR (substr($x, 1, length($p)) = $p AND substr($x, length($p) + 1, 1) IN ('/', '#')))";

    private static bool Matches(SqliteConnection connection, string predicate, string prefix, string path)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {predicate};";
        command.Parameters.AddWithValue("$p", prefix);
        command.Parameters.AddWithValue("$x", path);
        return (long)command.ExecuteScalar()! == 1;
    }

    /// <summary>
    /// The predicate alone, so the planner's choice between indexes (which depends on statistics
    /// an empty store does not have) cannot hide whether the path index can serve it at all. The
    /// substr form it replaced plans as a SCAN here. What the real store chooses is E9.
    /// </summary>
    [Fact]
    public void Subtree_IsServableByThePathIndex()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN SELECT count(*) FROM fact WHERE {StoreCompactor.Subtree("fact.path")};";
        command.Parameters.AddWithValue("$p", "/projects/p/code/a");
        using var reader = command.ExecuteReader();
        var plan = new List<string>();
        while (reader.Read())
        {
            plan.Add(reader.GetString(3));
        }

        Assert.Contains(plan, line => line.Contains("ix_fact_path", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.StartsWith("SCAN fact", StringComparison.Ordinal));
    }

    /// <summary>
    /// The real selection and the entity sweep, with their leading equality in front of
    /// <c>Subtree</c>. Without the unary plus each plans on its partial or kind index and walks
    /// every regenerable fact or code entity — 130–143 ms against 21.6–22.8 ms for the selection,
    /// 78–104 against 23–27 ms for the entities, on the real store (D97). Asserts the plan only;
    /// the timing is the measurement it stands for, since a plan is not a clock (D60).
    /// </summary>
    [Theory]
    [InlineData("selection", "ix_fact_path", "ix_fact_regenerable")]
    [InlineData("entities", "sqlite_autoindex_entity_1", "ix_entity_kind")]
    public void PathMode_IsServedByThePathIndex_NotTheLeadingEquality(string which, string expected, string forbidden)
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Execute(connection, "CREATE TEMP TABLE compact_target (id INTEGER PRIMARY KEY, was_live INTEGER, mode INTEGER);");
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + (which == "selection"
            ? StoreCompactor.PathModeSelectionSql
            : StoreCompactor.EntitySql("SELECT count(*) FROM entity"));
        command.Parameters.AddWithValue("$p", "/projects/p/code/a");
        using var reader = command.ExecuteReader();
        var plan = new List<string>();
        while (reader.Read())
        {
            plan.Add(reader.GetString(3));
        }

        Assert.Contains(plan, line => line.Contains(expected, StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains(forbidden, StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.StartsWith("SCAN fact", StringComparison.Ordinal) || line.StartsWith("SCAN entity", StringComparison.Ordinal));
    }

    // ---- doctor ---------------------------------------------------------------------------

    [Fact]
    public void Doctor_KeyOff_WarnsWithTheVerb_AndNamesTheKeyAsAnOption()
    {
        using var sandbox = new SandboxHome();
        Retire(sandbox, "doc-off", daysAgo: 0);

        var row = Assert.Single(Doctor(sandbox, keyOn: false));

        Assert.Equal(DiagnosisState.Warn, row.State);
        Assert.Equal("engram compact --retired", row.Fix);
        Assert.Contains("auto_remove_retired = true", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_KeyOn_InsideGrace_IsOk_WithTheDate()
    {
        using var sandbox = new SandboxHome();
        Retire(sandbox, "doc-grace", daysAgo: 2);

        var row = Assert.Single(Doctor(sandbox, keyOn: true));

        Assert.Equal(DiagnosisState.Ok, row.State);
        Assert.Contains("removed automatically after " + Now.AddDays(5).ToString("yyyy-MM-dd"), row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_KeyOn_PastGraceOverTheCap_WarnsTooLarge()
    {
        using var sandbox = new SandboxHome();
        var retired = Retire(sandbox, "doc-large", daysAgo: 8);
        Pad(sandbox, retired.RetiredPath, StoreCompactor.AutoRemoveCap + 1);

        var row = Assert.Single(Doctor(sandbox, keyOn: true));

        Assert.Equal(DiagnosisState.Warn, row.State);
        Assert.Contains("too large to remove automatically", row.Detail, StringComparison.Ordinal);
        Assert.Equal("engram compact --retired", row.Fix);
    }

    [Theory]
    [InlineData(9, DiagnosisState.Warn)]
    [InlineData(7, DiagnosisState.Ok)]
    public void Doctor_KeyOn_OverdueByMoreThanADay_Warns_AndNotBefore(int daysAgo, DiagnosisState expected)
    {
        using var sandbox = new SandboxHome();
        Retire(sandbox, "doc-overdue", daysAgo);

        var row = Assert.Single(Doctor(sandbox, keyOn: true));

        Assert.Equal(expected, row.State);
        if (expected == DiagnosisState.Warn)
        {
            Assert.Contains("automatic removal has not run; it runs at session start", row.Detail, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Doctor_CheckoutAbsent_IsOk_NeverWarn(bool keyOn)
    {
        using var sandbox = new SandboxHome();
        var retired = Retire(sandbox, "doc-absent", daysAgo: 30);
        Directory.Delete(retired.Repo, recursive: true);

        var row = Assert.Single(Doctor(sandbox, keyOn));

        Assert.Equal(DiagnosisState.Ok, row.State);
        Assert.Contains("not on this machine", row.Detail, StringComparison.Ordinal);
    }

    // ---- helpers --------------------------------------------------------------------------

    private sealed record Retired(string Repo, string RetiredPath, string RetiredIdentity, string WinnerPath);

    /// <summary>
    /// A checkout indexed under one origin, repointed, and fully indexed again: the first row is
    /// retired by the real Phase 2 path. Then backdated, so grace can be staged.
    /// </summary>
    private static Retired Retire(SandboxHome sandbox, string name, int daysAgo)
    {
        var first = $"git@example.com:acme/{name}-a";
        var repo = RegistryRetirementTests.Checkout(sandbox, name, first);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        FullIndex(connection, sandbox, repo);
        RegistryRetirementTests.RunGit(repo, "remote", "set-url", "origin", $"git@example.com:acme/{name}-b");
        FullIndex(connection, sandbox, repo);

        var retiredPath = Text(connection, $"SELECT repo_path FROM repo_registry WHERE identity = '{first}' AND detached_at IS NOT NULL;");
        var winnerPath = Text(connection, $"SELECT repo_path FROM repo_registry WHERE identity = 'git@example.com:acme/{name}-b' AND detached_at IS NULL;");
        Execute(connection, $"UPDATE repo_registry SET detached_at = {Now.AddDays(-daysAgo).ToUnixTimeSeconds()} WHERE repo_path = '{retiredPath}';");
        return new Retired(repo, retiredPath, first, winnerPath);
    }

    private static void FullIndex(SqliteConnection connection, SandboxHome sandbox, string repo) =>
        CodeIndexer.Index(
            connection, sandbox.Home, ConfigFile.Empty, IndexingSettings.Default,
            new IndexOptions(repo, Apply: true, Drain: false, Full: true),
            Now);

    private static RetiredCompactReport Run(
        SandboxHome sandbox, bool apply, bool auto, StoreCompactor.RetiredHooks? hooks = null)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        return StoreCompactor.CompactRetired(connection, sandbox.Home, apply, auto, Now, beforeWrite: null, hooks);
    }

    private static (int Exit, string Stdout, string Stderr) Cli(SandboxHome sandbox, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CompactCommand.Run(sandbox.Home.Root, args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static IReadOnlyList<Diagnosis> Doctor(SandboxHome sandbox, bool keyOn)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var settings = IndexingSettings.Read(ConfigFile.Parse($"[indexing]\nauto_remove_retired = {(keyOn ? "true" : "false")}\n"));
        return Diagnostics.CheckRetiredPaths(connection, settings, Now);
    }

    private static void SetKey(SandboxHome sandbox, string value)
    {
        var config = File.ReadAllText(sandbox.Home.ConfigPath);
        Assert.Contains("# auto_remove_retired = false", config, StringComparison.Ordinal);
        File.WriteAllText(sandbox.Home.ConfigPath, config.Replace("# auto_remove_retired = false", "auto_remove_retired = " + value, StringComparison.Ordinal));
    }

    /// <summary>Regenerable facts under the prefix, live and closed.</summary>
    private static int FactsUnder(SandboxHome sandbox, string prefix) =>
        (int)Count(sandbox, $"SELECT COUNT(*) FROM fact WHERE regenerable = 1 AND (path = '{prefix}' OR path LIKE '{prefix}/%' OR path LIKE '{prefix}#%');");

    private static void Pad(SandboxHome sandbox, string prefix, int count)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        for (var i = 0; i < count; i++)
        {
            FactStore.Remember(
                connection,
                new FactWrite($"{prefix}/pad/F{i}.cs", "file", "declares", $"pad {i}", "code", "observed", Regenerable: true),
                Now);
        }
    }

    private static string[] Snapshots(SandboxHome sandbox) =>
        Directory.Exists(sandbox.Home.BackupDir)
            ? Directory.GetFiles(sandbox.Home.BackupDir, "*pre-compact*")
            : [];

    private static List<TelemetryRecord> RemovalEvents(SandboxHome sandbox)
    {
        var path = Telemetry.ResolvePath(sandbox.Home);
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Select(Telemetry.TryParse)
            .Where(r => r?.Kind == TelemetryEventKind.RetiredRemoval)
            .Select(r => r!)
            .ToList();
    }

    private static long Count(SandboxHome sandbox, string sql)
    {
        using var connection = EngramDatabase.Open(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static void Exec(SandboxHome sandbox, string sql)
    {
        using var connection = EngramDatabase.Open(sandbox.Home);
        Execute(connection, sql);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    /// <summary>Value equality for reports, with the path list compared by content.</summary>
    private sealed class ReportComparer : IEqualityComparer<CompactReport>
    {
        public static readonly ReportComparer Instance = new();

        public bool Equals(CompactReport? x, CompactReport? y) =>
            x is not null && y is not null
            && x with { AuthoredPaths = null, Notes = Array.Empty<string>() } == y with { AuthoredPaths = null, Notes = Array.Empty<string>() }
            && (x.AuthoredPaths ?? []).SequenceEqual(y.AuthoredPaths ?? []);

        public int GetHashCode(CompactReport obj) => 0;
    }
}
