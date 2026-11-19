using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// <see cref="FactStore.Refile"/> (D87): close-and-reassert, through the same chokepoints every
/// other write uses.
/// </summary>
public class FactRefileTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Refile_WritesTheSameBeliefAtTheTarget_AndClosesTheSourceOntoIt()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var source = FactStore.Remember(connection, new FactWrite(
            "/ah2/repo/t/train/1", "concept", "records", "train opened", "user", "stated",
            Evidence: "ah2 ledger kind=train", Details: "longer detail"), T0).FactId;

        var result = Refile(connection, source, "/tools/ah2/repo/t/train/1", T0.AddMinutes(1));

        Assert.Equal(RefileOutcome.Refiled, result.Outcome);
        var target = FactStore.ReadById(connection, result.TargetFactId!.Value)!;
        Assert.Equal("/tools/ah2/repo/t/train/1", target.SubjectPath);
        Assert.Equal(("records", "train opened", "longer detail"), (target.Predicate, target.Body, target.Details));
        Assert.Equal(("tool", "observed"), (target.Scope, target.LearnedVia));
        Assert.Equal($"ah2 ledger kind=train; re-filed from f{source}", target.Evidence);
        Assert.Equal(T0.AddMinutes(1).ToUnixTimeSeconds(), target.ValidFrom);
        Assert.Null(target.ValidTo);

        var closed = FactStore.ReadById(connection, source)!;
        Assert.Equal(T0.AddMinutes(1).ToUnixTimeSeconds(), closed.ValidTo);
        Assert.Equal(target.Id, closed.SupersededBy);

        // The source's belief content is untouched: append-only means only the close moved.
        Assert.Equal(("/ah2/repo/t/train/1", "user", "stated", "train opened"),
            (closed.SubjectPath, closed.Scope, closed.LearnedVia, closed.Body));

        Assert.Equal(("re-filed: test", target.Id), ReadSupersession(connection, source));
    }

    [Fact]
    public void Refile_OfAClosedSource_WritesNothing()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var source = Ledger(connection, "/ah2/a", "gone");
        FactStore.Forget(connection, source, "test", T0);
        var before = CountFacts(connection);

        var result = Refile(connection, source, "/tools/ah2/a", T0.AddMinutes(1));

        Assert.Equal(RefileOutcome.SourceNotLive, result.Outcome);
        Assert.Equal(before, CountFacts(connection));
    }

    [Fact]
    public void Refile_OntoAnIdenticalLiveBelief_ClosesTheSourceOntoIt_WithoutANewRow()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var existing = FactStore.Remember(connection, new FactWrite(
            "/tools/ah2/a", "concept", "records", "same", "tool", "observed"), T0).FactId;
        var source = Ledger(connection, "/ah2/a", "same");
        var before = CountFacts(connection);

        var result = Refile(connection, source, "/tools/ah2/a", T0.AddMinutes(1));

        Assert.Equal((RefileOutcome.AlreadyAtTarget, (long?)existing), (result.Outcome, result.TargetFactId));
        Assert.Equal(before, CountFacts(connection));
        Assert.Equal(existing, FactStore.ReadById(connection, source)!.SupersededBy);
        Assert.Null(FactStore.ReadById(connection, existing)!.ValidTo);
    }

    /// <summary>
    /// The nearest input that must NOT take the no-op path: same address and predicate, different
    /// body. The target's newer belief is never superseded by the source's older one.
    /// </summary>
    [Fact]
    public void Refile_OntoADifferentLiveBelief_LeavesBothAlone()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var existing = FactStore.Remember(connection, new FactWrite(
            "/tools/ah2/a", "concept", "records", "newer", "tool", "observed"), T0).FactId;
        var source = Ledger(connection, "/ah2/a", "older");
        var before = CountFacts(connection);

        var result = Refile(connection, source, "/tools/ah2/a", T0.AddMinutes(1));

        Assert.Equal(RefileOutcome.TargetConflict, result.Outcome);
        Assert.Equal(before, CountFacts(connection));
        Assert.Null(FactStore.ReadById(connection, source)!.ValidTo);
        Assert.Null(FactStore.ReadById(connection, existing)!.ValidTo);
    }

    [Fact]
    public void Refile_OfAnEdge_KeepsItsObject_AndTheEdgePartitionHoldsOneLiveRow()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var source = FactStore.Remember(connection, new FactWrite(
            "/ah2/repo/t/x", "concept", "relates", "x relates y", "user", "stated",
            ObjectPath: "/ah2/repo/t/y", ObjectKind: "concept"), T0).FactId;

        var result = Refile(connection, source, "/tools/ah2/repo/t/x", T0.AddMinutes(1));

        Assert.Equal(RefileOutcome.Refiled, result.Outcome);
        Assert.Equal(
            ["/ah2/repo/t/y"],
            Strings(connection, $"SELECT o.path FROM fact f JOIN entity o ON o.id = f.object_id WHERE f.id = {result.TargetFactId};"));
        Assert.Equal(
            [result.TargetFactId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            Strings(connection, "SELECT id FROM fact WHERE object_id IS NOT NULL AND valid_to IS NULL;"));
    }

    /// <summary>
    /// The CLAUDE.md guard for <c>fact_token</c>: incremental maintenance across a re-file equals a
    /// from-scratch recomputation, and <c>fact_fts</c> holds exactly the live facts.
    /// </summary>
    [Fact]
    public void Refile_KeepsFactTokenAndFtsInStep_WithAFromScratchRecomputation()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var a = Ledger(connection, "/ah2/repo/t/zanzibar", "zanzibar workflow opened");
        var b = Ledger(connection, "/ah2/repo/t/quokka", "quokka stream closed");
        Refile(connection, a, "/tools/ah2/repo/t/zanzibar", T0.AddMinutes(1));
        Refile(connection, b, "/tools/ah2/repo/t/quokka", T0.AddMinutes(1));

        var incremental = TokenRows(connection);
        FactTokenIndex.Rebuild(connection);
        Assert.Equal(TokenRows(connection), incremental);
        Assert.DoesNotContain(incremental, row => row.StartsWith($"{a}:", StringComparison.Ordinal));

        var report = StoreRepairer.Repair(connection, sandbox.Home, apply: false, T0.AddMinutes(2));
        Assert.False(report.FtsNeedsRebuild, $"fts missing {report.FtsMissing}, extra {report.FtsExtra}");
        Assert.False(report.TokenIndexNeedsRebuild, $"token missing {report.TokenMissing}, extra {report.TokenExtra}");
        Assert.Equal(["2"], Strings(connection, "SELECT count(*) FROM fact_fts WHERE fact_fts MATCH 'zanzibar OR quokka';"));
    }

    private static RefileResult Refile(SqliteConnection connection, long source, string target, DateTimeOffset now)
    {
        using var transaction = EngramDatabase.BeginWrite(connection);
        var result = FactStore.Refile(connection, transaction, source, target, "tool", "observed", "re-filed: test", now);
        transaction.Commit();
        return result;
    }

    private static long Ledger(SqliteConnection connection, string path, string body) =>
        FactStore.Remember(connection, new FactWrite(path, "concept", "records", body, "user", "stated"), T0).FactId;

    private static long CountFacts(SqliteConnection connection) =>
        long.Parse(Strings(connection, "SELECT count(*) FROM fact;")[0], System.Globalization.CultureInfo.InvariantCulture);

    private static (string Reason, long? New) ReadSupersession(SqliteConnection connection, long old)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT reason, new_fact_id FROM supersession WHERE old_fact_id = $id;";
        command.Parameters.AddWithValue("$id", old);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }

    private static List<string> TokenRows(SqliteConnection connection) =>
        Strings(connection, "SELECT fact_id || ':' || token FROM fact_token ORDER BY fact_id, token;");

    private static List<string> Strings(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        }

        return rows;
    }
}
