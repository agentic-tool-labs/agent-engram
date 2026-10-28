using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// <c>engram import --revise</c>: a changed live record closes the live belief and becomes the new
/// head, an identical one is a no-op, and the whole batch is one transaction.
/// </summary>
public partial class ImportReviseTests
{
    private const long T0Unix = 1_786_000_000;
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(T0Unix);

    [GeneratedRegex(@"^import: applied=(true|false)( [a-z]+=\d+)+$")]
    private static partial Regex SummaryLine();

    [Fact]
    public void ImportRevise_AChangedBody_RevisesTheLiveBelief()
    {
        using var sandbox = new SandboxHome();
        long oldId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            oldId = Seed(connection, "/ledger/verdict", "states", "approved");
        }

        var run = Import(sandbox, apply: true, Record("/ledger/verdict", "rejected", T0Unix + 100, reason: "ledger verdict 100", evidence: "line 7"));

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("revised=1", run.Last);

        using var check = EngramDatabase.OpenInitialized(sandbox.Home);
        var newId = Scalar(check, "SELECT id FROM fact WHERE body = 'rejected'");
        Assert.Equal(T0Unix + 100, Scalar(check, $"SELECT valid_to FROM fact WHERE id = {oldId}"));
        Assert.Equal(newId, Scalar(check, $"SELECT superseded_by FROM fact WHERE id = {oldId}"));
        Assert.Equal(1L, Scalar(check, $"SELECT count(*) FROM fact WHERE id = {newId} AND valid_to IS NULL AND valid_from = {T0Unix + 100}"));
        Assert.Equal(1L, Scalar(check, $"SELECT count(*) FROM supersession WHERE old_fact_id = {oldId} AND new_fact_id = {newId} AND reason = 'ledger verdict 100' AND evidence = 'line 7'"));
    }

    [Fact]
    public void ImportRevise_ADifferentPredicate_IsNewNotARevision()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Seed(connection, "/ledger/verdict", "states", "approved");
        }

        var run = Import(sandbox, apply: true, Record("/ledger/verdict", "rejected", T0Unix + 100, predicate: "other"));

        Assert.Contains("new=1 revised=0", run.Last);
        using var check = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Equal(0L, Scalar(check, "SELECT count(*) FROM supersession"));
    }

    [Fact]
    public void ImportRevise_TheSameBatchTwice_WritesNothingTheSecondTime()
    {
        using var sandbox = new SandboxHome();
        Import(sandbox, apply: true, Record("/ledger/a", "one", T0Unix), Record("/ledger/b", "two", T0Unix));

        long facts;
        long supersessions;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            facts = Scalar(connection, "SELECT count(*) FROM fact");
            supersessions = Scalar(connection, "SELECT count(*) FROM supersession");
        }

        // Later valid_from on every record, as a producer stamping the forward time does.
        var again = Import(sandbox, apply: true, Record("/ledger/a", "one", T0Unix + 50), Record("/ledger/b", "two", T0Unix + 50));

        Assert.Contains("new=0 revised=0 unchanged=2", again.Last);
        using var check = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Equal(facts, Scalar(check, "SELECT count(*) FROM fact"));
        Assert.Equal(supersessions, Scalar(check, "SELECT count(*) FROM supersession"));
    }

    [Fact]
    public void ImportRevise_ThreeRecordsOnOneKey_ChainInFileOrder()
    {
        using var sandbox = new SandboxHome();
        var run = Import(
            sandbox,
            apply: true,
            Record("/ledger/chain", "A", T0Unix + 100),
            Record("/ledger/chain", "B", T0Unix + 200),
            Record("/ledger/chain", "C", T0Unix + 300));

        Assert.Contains("new=1 revised=2", run.Last);

        using var check = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Enumerable.Range(0, 3)
            .Select(i => Scalar(check, $"SELECT id FROM fact WHERE body = '{"ABC"[i]}'"))
            .ToArray();
        Assert.Equal(ids[1], Scalar(check, $"SELECT superseded_by FROM fact WHERE id = {ids[0]}"));
        Assert.Equal(ids[2], Scalar(check, $"SELECT superseded_by FROM fact WHERE id = {ids[1]}"));
        Assert.Equal(1L, Scalar(check, $"SELECT count(*) FROM fact WHERE id = {ids[2]} AND valid_to IS NULL AND superseded_by IS NULL"));
        Assert.Equal(2L, Scalar(check, "SELECT count(*) FROM supersession WHERE old_fact_id IN (SELECT id FROM fact WHERE path = '/ledger/chain')"));
    }

    [Fact]
    public void ImportRevise_AnOlderRecord_IsCountedStaleAndNotApplied()
    {
        using var sandbox = new SandboxHome();
        long headId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            headId = Seed(connection, "/ledger/verdict", "states", "current", T0.AddSeconds(500));
        }

        var run = Import(sandbox, apply: true, Record("/ledger/verdict", "older", T0Unix + 100));

        Assert.Contains("stale=1", run.Last);
        Assert.Contains("new=0 revised=0", run.Last);
        using var check = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Equal(1L, Scalar(check, $"SELECT count(*) FROM fact WHERE id = {headId} AND valid_to IS NULL"));
        Assert.Equal(0L, Scalar(check, "SELECT count(*) FROM fact WHERE body = 'older'"));
    }

    [Fact]
    public void Import_WithoutRevise_StillLeavesAChangedFactOut()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Seed(connection, "/ledger/verdict", "states", "approved");
        }

        var run = Import(sandbox, apply: true, revise: false, Record("/ledger/verdict", "rejected", T0Unix + 100));

        var expected =
            "Wrote 0 facts, leaving 0 already in the store." + Environment.NewLine
            + "  1 fact was left out: the store already believes something else about that subject and predicate, "
            + "and replay never closes a live belief to make room." + Environment.NewLine;
        Assert.Equal(expected, run.Stdout);
        using var check = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Equal(0L, Scalar(check, "SELECT count(*) FROM fact WHERE body = 'rejected'"));
    }

    [Fact]
    public void ImportRevise_ADbErrorMidBatch_WritesNothingAndExitsOne()
    {
        using var sandbox = new SandboxHome();
        long before;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Seed(connection, "/ledger/verdict", "states", "approved");
            Execute(
                connection,
                "CREATE TRIGGER fail_on_boom BEFORE INSERT ON supersession WHEN NEW.reason = 'boom' "
                + "BEGIN SELECT RAISE(ABORT, 'forced failure'); END;");
            before = Scalar(connection, "SELECT count(*) FROM fact");
        }

        var run = Import(
            sandbox,
            apply: true,
            Record("/ledger/n1", "x", T0Unix + 10),
            Record("/ledger/n2", "y", T0Unix + 10),
            Record("/ledger/verdict", "rejected", T0Unix + 100, reason: "boom"),
            Record("/ledger/n3", "z", T0Unix + 10));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("nothing was written", run.Stderr);
        using var check = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Equal(before, Scalar(check, "SELECT count(*) FROM fact"));
        Assert.Equal(1L, Scalar(check, "SELECT count(*) FROM fact WHERE body = 'approved' AND valid_to IS NULL"));
    }

    [Fact]
    public void ImportRevise_DryRun_ReportsWhatApplyThenDoes()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Seed(connection, "/ledger/changed", "states", "old");
            Seed(connection, "/ledger/same", "states", "same");
            Seed(connection, "/ledger/late", "states", "head", T0.AddSeconds(900));
        }

        Rec[] batch =
        [
            Record("/ledger/changed", "new", T0Unix + 100),
            Record("/ledger/same", "same", T0Unix + 100),
            Record("/ledger/late", "older", T0Unix + 100),
            Record("/ledger/chain", "A", T0Unix + 100),
            Record("/ledger/chain", "B", T0Unix + 200),
            Record("/ledger/fresh", "F", T0Unix + 100),
        ];

        long before;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            before = Scalar(connection, "SELECT count(*) FROM fact");
        }

        var dry = Import(sandbox, apply: false, batch);
        using (var check = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Assert.Equal(before, Scalar(check, "SELECT count(*) FROM fact"));
            Assert.Equal(0L, Scalar(check, "SELECT count(*) FROM supersession WHERE reason = 'import revise'"));
        }

        var applied = Import(sandbox, apply: true, batch);

        Assert.Contains("applied=false", dry.Last);
        Assert.Contains("applied=true", applied.Last);
        Assert.Equal(applied.Last.Replace("applied=true", "applied=false"), dry.Last);
        Assert.Contains("new=2 revised=2 unchanged=1 stale=1", dry.Last);
    }

    [Fact]
    public void ImportRevise_CarriesTheSyncFlagLikeEngramRevise()
    {
        using var sandbox = new SandboxHome();
        long oldId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            oldId = Seed(connection, "/ledger/flagged", "states", "old");
            FactSyncRequests.Insert(connection, null, oldId, T0Unix);
            Seed(connection, "/ledger/plain", "states", "old");
        }

        Import(sandbox, apply: true, Record("/ledger/flagged", "new", T0Unix + 100), Record("/ledger/plain", "new", T0Unix + 100));

        using var check = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Equal(1L, Scalar(check, "SELECT count(*) FROM fact_sync_request WHERE fact_id = (SELECT id FROM fact WHERE path = '/ledger/flagged' AND valid_to IS NULL)"));
        Assert.Equal(0L, Scalar(check, "SELECT count(*) FROM fact_sync_request WHERE fact_id = (SELECT id FROM fact WHERE path = '/ledger/plain' AND valid_to IS NULL)"));
    }

    [Fact]
    public void FactStoreRevise_InheritsTheFlagUnlessTold_AndClosesTheOldFact()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var first = Seed(connection, "/ledger/inherit", "states", "v1");
        FactSyncRequests.Insert(connection, null, first, T0Unix);
        var second = Seed(connection, "/ledger/override", "states", "v1");
        FactSyncRequests.Insert(connection, null, second, T0Unix);

        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            var inherited = FactStore.Revise(
                connection, transaction, first,
                new FactWrite("/ledger/inherit", "concept", "states", "v2", "project", "stated"),
                T0.AddSeconds(10), "why");
            var overridden = FactStore.Revise(
                connection, transaction, second,
                new FactWrite("/ledger/override", "concept", "states", "v2", "project", "stated"),
                T0.AddSeconds(10), "why", sync: false);
            transaction.Commit();

            Assert.Equal(first, inherited.SupersededFactId);
            Assert.True(FactSyncRequests.IsFlagged(connection, null, inherited.FactId));
            Assert.False(FactSyncRequests.IsFlagged(connection, null, overridden.FactId));
        }

        Assert.Equal(T0Unix + 10, Scalar(connection, $"SELECT valid_to FROM fact WHERE id = {first}"));
    }

    [Fact]
    public void ImportRevise_SummaryLine_IsLastAndParses()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Seed(connection, "/ledger/a", "states", "old");
        }

        var bundle = WriteBundle(
            sandbox,
            "{\"format\":\"engram-facts\"}",
            Record("/ledger/a", "new", T0Unix + 100).Json,
            "not json at all",
            "{\"subject\":\"/ledger/nobody\"}",
            Record("/ledger/b", "b", T0Unix + 100).Json);

        var dry = Run(sandbox, bundle, apply: false, revise: true);
        var applied = Run(sandbox, bundle, apply: true, revise: true);

        foreach (var run in new[] { dry, applied })
        {
            Assert.Matches(SummaryLine(), run.Last);
            Assert.Contains("unreadable=2", run.Last);
            var counts = Regex.Matches(run.Last, @" (new|revised|unchanged|stale|conflicted)=(\d+)")
                .Sum(m => int.Parse(m.Groups[2].Value));
            Assert.Equal(2, counts);
        }

        Assert.Equal(1, applied.Stdout.Split('\n').Count(l => l.StartsWith("import:", StringComparison.Ordinal)));
    }

    [Fact]
    public void Import_WithoutRevise_PrintsNoSummaryLine()
    {
        using var sandbox = new SandboxHome();

        var run = Import(sandbox, apply: true, revise: false, Record("/ledger/a", "x", T0Unix));

        Assert.DoesNotContain("import:", run.Stdout);
    }

    [Fact]
    public void Import_AnOldFlagProbe_RejectsUnknownFlagBeforeOpeningTheStore()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var bundle = WriteBundle(sandbox, Record("/ledger/a", "x", T0Unix).Json);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "import", bundle, "--bogus"], stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("unexpected argument '--bogus'", stderr.ToString());
        Assert.False(File.Exists(sandbox.Home.DatabasePath));

        var revised = CliApp.Run(["--home", sandbox.Home.Root, "import", bundle, "--revise"], new StringWriter(), new StringWriter());
        Assert.Equal(0, revised);
    }

    [Fact]
    public void ImportRevise_ARestampedResendOfASameKeyBatch_WritesNothing()
    {
        using var sandbox = new SandboxHome();
        Import(sandbox, apply: true, Record("/ledger/k", "A", T0Unix + 100), Record("/ledger/k", "B", T0Unix + 200));

        var again = DryThenApply(sandbox, Record("/ledger/k", "A", T0Unix + 1100), Record("/ledger/k", "B", T0Unix + 1200));

        Assert.Contains("new=0 revised=0 unchanged=2", again.Last);
        using var check = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Equal(2L, Scalar(check, "SELECT count(*) FROM fact WHERE path = '/ledger/k'"));
        Assert.Equal(1L, Scalar(check, "SELECT count(*) FROM supersession WHERE old_fact_id IN (SELECT id FROM fact WHERE path = '/ledger/k')"));
    }

    [Fact]
    public void ImportRevise_AResendPlusAnAppendedLine_WritesOnlyTheAppendedOne()
    {
        using var sandbox = new SandboxHome();
        Import(sandbox, apply: true, Record("/ledger/k", "A", T0Unix + 100), Record("/ledger/k", "B", T0Unix + 200));

        var again = DryThenApply(
            sandbox,
            Record("/ledger/k", "A", T0Unix + 1100),
            Record("/ledger/k", "B", T0Unix + 1200),
            Record("/ledger/k", "C", T0Unix + 1300));

        Assert.Contains("new=0 revised=1 unchanged=2", again.Last);
        using var check = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Equal(3L, Scalar(check, "SELECT count(*) FROM fact WHERE path = '/ledger/k'"));
        Assert.Equal(1L, Scalar(check, "SELECT count(*) FROM fact WHERE path = '/ledger/k' AND valid_to IS NULL AND body = 'C'"));
    }

    [Fact]
    public void ImportRevise_AChangeBackAfterAChain_IsRevisedNotCollapsed()
    {
        using var sandbox = new SandboxHome();
        Import(sandbox, apply: true, Record("/ledger/k", "A", T0Unix + 100), Record("/ledger/k", "B", T0Unix + 200));

        var again = DryThenApply(sandbox, Record("/ledger/k", "A", T0Unix + 300));

        Assert.Contains("new=0 revised=1 unchanged=0", again.Last);
        using var check = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Equal(3L, Scalar(check, "SELECT count(*) FROM fact WHERE path = '/ledger/k'"));
        Assert.Equal(1L, Scalar(check, "SELECT count(*) FROM fact WHERE path = '/ledger/k' AND valid_to IS NULL AND body = 'A'"));
    }

    [Fact]
    public void ImportRevise_AResentHeadFollowedByAChangeBack_SkipsOnlyTheResend()
    {
        using var sandbox = new SandboxHome();
        Import(sandbox, apply: true, Record("/ledger/k", "A", T0Unix + 100), Record("/ledger/k", "B", T0Unix + 200));

        var again = DryThenApply(sandbox, Record("/ledger/k", "B", T0Unix + 1100), Record("/ledger/k", "A", T0Unix + 1200));

        Assert.Contains("new=0 revised=1 unchanged=1", again.Last);
        using var check = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Equal(1L, Scalar(check, "SELECT count(*) FROM fact WHERE path = '/ledger/k' AND valid_to IS NULL AND body = 'A'"));
    }

    [Fact]
    public void ImportRevise_AClosedRecordOnTheKey_IsNotPartOfTheResendSequence()
    {
        using var sandbox = new SandboxHome();
        Import(sandbox, apply: true, Record("/ledger/k", "A", T0Unix + 100), Record("/ledger/k", "B", T0Unix + 200));
        var closed = new JsonObject
        {
            ["subject"] = "/ledger/k",
            ["predicate"] = "states",
            ["body"] = "A",
            ["valid_from"] = T0Unix + 50,
            ["valid_to"] = T0Unix + 60,
        }.ToJsonString();
        var bundle = WriteBundle(sandbox, closed, Record("/ledger/k", "B", T0Unix + 1200).Json);

        var dry = Run(sandbox, bundle, apply: false, revise: true);
        var applied = Run(sandbox, bundle, apply: true, revise: true);

        Assert.Equal(applied.Last.Replace("applied=true", "applied=false"), dry.Last);
        Assert.Contains("new=1 revised=0 unchanged=1", applied.Last);
    }

    [Fact]
    public void ImportRevise_ANewRecordWithNoValidFrom_PlansTheSameHeadTheApplyWrites()
    {
        using var sandbox = new SandboxHome();

        var run = DryThenApply(sandbox, Record("/ledger/k", "A", 0), Record("/ledger/k", "B", 100));

        Assert.Contains("new=1 revised=1 unchanged=0 stale=0", run.Last);
    }

    [Fact]
    public void ImportRevise_ALiveRecordWithASuccessorAfterARevisePathWrite_ConflictsInBothModes()
    {
        using var sandbox = new SandboxHome();
        var withSuccessor = new JsonObject
        {
            ["subject"] = "/ledger/k",
            ["predicate"] = "states",
            ["body"] = "Z",
            ["valid_from"] = T0Unix + 200,
            ["superseded_by"] = 999,
        }.ToJsonString();
        var bundle = WriteBundle(sandbox, Record("/ledger/k", "A", T0Unix + 100).Json, withSuccessor);

        var dry = Run(sandbox, bundle, apply: false, revise: true);
        var applied = Run(sandbox, bundle, apply: true, revise: true);

        Assert.Equal(applied.Last.Replace("applied=true", "applied=false"), dry.Last);
        Assert.Contains("new=1 revised=0 unchanged=0 stale=0 conflicted=1", applied.Last);
    }

    [Fact]
    public void ImportRevise_AClosedRowMatchingWithNoLiveHead_IsUnchanged()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            var id = Seed(connection, "/ledger/k", "states", "A");
            FactStore.Forget(connection, id, "retired", T0.AddSeconds(10));
        }

        var run = DryThenApply(sandbox, Record("/ledger/k", "A", T0Unix));

        Assert.Contains("new=0 revised=0 unchanged=1", run.Last);
    }

    [Fact]
    public void FactStoreRevise_WhenTheLiveFactOnTheKeyIsNotTheOneNamed_ThrowsAndWritesNothing()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Seed(connection, "/ledger/x", "states", "x1");
        var other = Seed(connection, "/ledger/y", "states", "y1");
        var before = Scalar(connection, "SELECT count(*) FROM fact");

        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            Assert.Throws<InvalidOperationException>(() => FactStore.Revise(
                connection, transaction, other,
                new FactWrite("/ledger/x", "concept", "states", "x2", "project", "stated"),
                T0.AddSeconds(10), "why"));
        }

        Assert.Equal(before, Scalar(connection, "SELECT count(*) FROM fact"));
        Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM fact WHERE path = '/ledger/x' AND valid_to IS NULL AND body = 'x1'"));
    }

    private static RunResult DryThenApply(SandboxHome sandbox, params Rec[] records)
    {
        var bundle = WriteBundle(sandbox, records.Select(r => r.Json).ToArray());
        var dry = Run(sandbox, bundle, apply: false, revise: true);
        var applied = Run(sandbox, bundle, apply: true, revise: true);

        Assert.Equal(applied.Last.Replace("applied=true", "applied=false"), dry.Last);
        return applied;
    }

    private sealed record Rec(string Json);

    private sealed record RunResult(int ExitCode, string Stdout, string Stderr)
    {
        public string Last => Stdout.TrimEnd().Split('\n')[^1].TrimEnd('\r');
    }

    private static Rec Record(
        string subject,
        string body,
        long validFrom,
        string predicate = "states",
        string? reason = null,
        string? evidence = null)
    {
        var node = new JsonObject
        {
            ["subject"] = subject,
            ["predicate"] = predicate,
            ["body"] = body,
            ["valid_from"] = validFrom,
        };
        if (reason is not null)
        {
            node["reason"] = reason;
        }

        if (evidence is not null)
        {
            node["evidence"] = evidence;
        }

        return new Rec(node.ToJsonString());
    }

    private static RunResult Import(SandboxHome sandbox, bool apply, params Rec[] records) =>
        Import(sandbox, apply, revise: true, records);

    private static RunResult Import(SandboxHome sandbox, bool apply, bool revise, params Rec[] records) =>
        Run(sandbox, WriteBundle(sandbox, records.Select(r => r.Json).ToArray()), apply, revise);

    private static string WriteBundle(SandboxHome sandbox, params string[] lines)
    {
        var path = Path.Combine(sandbox.Home.Root, $"bundle-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static RunResult Run(SandboxHome sandbox, string bundle, bool apply, bool revise)
    {
        var args = new List<string> { "--home", sandbox.Home.Root, "import", bundle };
        if (apply)
        {
            args.Add("--apply");
        }

        if (revise)
        {
            args.Add("--revise");
        }

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(args.ToArray(), stdout, stderr);
        return new RunResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private static long Seed(SqliteConnection connection, string path, string predicate, string body, DateTimeOffset? at = null) =>
        FactStore.Remember(
            connection,
            new FactWrite(path, "concept", predicate, body, "project", "stated"),
            at ?? T0).FactId;

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar()!);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
