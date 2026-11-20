using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Spec §3.2: the primary guard on the SQL ranker, not a timing test. Pins the properties that
/// were actually captured via <c>EXPLAIN QUERY PLAN</c> against a real store before this assertion
/// text was written ("pin reality, not a hope") — not the properties hoped for.
/// </summary>
/// <remarks>
/// Prior sessions are enumerated from <c>session</c> with a stop-at-first probe over
/// <c>ix_fact_session</c> (D84, superseding D81's entity-first two-partition form). Entity paths
/// stay authoritative, and every prior session is still enumerated even when it contributes no
/// candidates.
/// </remarks>
public class RecallRankerPlanTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// <c>ANALYZE</c> is deliberately not exercised here. Captured at this fixture's 5-fact scale, it
    /// changes the plan qualitatively — <c>SEARCH f USING INTEGER PRIMARY KEY (rowid=?)</c> becomes a
    /// plain <c>SCAN f</c> throughout, because the optimizer correctly judges that scanning 5 rows is
    /// cheaper than an indexed nested loop. That is real ANALYZE behavior, but it is a small-table
    /// artifact unrelated to the question §3.2 (and NEEDS-EVIDENCE 6) actually asks — what the
    /// planner does at real scale — and asserting against it here would either pin a fact about a
    /// 5-row table forever or force this guard to swallow scan lines with no bearing on the ranker's
    /// real behavior. The <c>ANALYZE</c> question is answered against the 50,097-fact fixture
    /// instead, and reported narratively rather than pinned to a test that cannot see that store.
    /// </summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public void TheRankingStatement_SeeksFactsAndTokensByIndex(bool overlapAvailable, bool vectorAvailable, bool filtered)
    {
        Assert.SkipUnless(!vectorAvailable || VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new SandboxHome(initialize: false);
        if (vectorAvailable) VectorExtensionFile.InstallInto(sandbox.Home);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        if (vectorAvailable) VectorIndex.EnsureCreated(connection, new EmbeddingSpace("test", 4));

        for (var i = 0; i < 5; i++)
        {
            FactStore.Remember(
                connection,
                new FactWrite(
                    "/knowledge/testing/plan-" + i, "note", "states",
                    $"Fact number {i} about kestrel loopback binding.", "project", "stated"),
                T0);
        }

        var lines = CapturePlan(connection, overlapAvailable, vectorAvailable, filtered);
        Assert.Contains(lines, line => line.Contains("SEARCH f USING INDEX ix_fact_session (session_id=?)", StringComparison.Ordinal));
        var unexpectedFactScans = UnexplainedFactScans(lines);

        Assert.True(
            unexpectedFactScans.Count == 0,
            $"overlapAvailable={overlapAvailable}: unexpected SCAN of fact — "
                + $"{string.Join(" | ", unexpectedFactScans)}\nfull plan:\n{string.Join("\n", lines)}");

        // D90's CTEs. `excluded` seeks the directive subtree by path range and, on a default call,
        // the default-excluded scopes by the partial scope index; the current-session arm seeks by
        // session beside prior_sessions' probe; `scored` is built once for its two readers.
        Assert.Contains("SEARCH e USING COVERING INDEX sqlite_autoindex_entity_1 (path>? AND path<?)", lines);
        if (!filtered)
        {
            // A filter may seek ix_fact_scope for its own predicate, so only the default pins it.
            Assert.Contains("SEARCH f USING INDEX ix_fact_scope (scope=?)", lines);
        }
        Assert.Equal(overlapAvailable ? 2 : 1, lines.Count(line => line.Contains("SEARCH f USING INDEX ix_fact_session (session_id=?)", StringComparison.Ordinal)));
        Assert.Contains("MATERIALIZE scored", lines);

        var tokenAccess = lines.Where(line => line.Contains("fact_token", StringComparison.Ordinal)).ToList();
        Assert.True(
            tokenAccess.All(line => line.Contains("SEARCH", StringComparison.Ordinal) && line.Contains("PRIMARY KEY", StringComparison.Ordinal))
                || tokenAccess.Count == 0,
            $"overlapAvailable={overlapAvailable}: fact_token reached other than by "
                + $"a primary-key search — {string.Join(" | ", tokenAccess)}");
    }

    /// <summary>
    /// Falsification: <see cref="TheRankingStatement_SeeksFactsAndTokensByIndex"/>
    /// must be able to fail, or it proves nothing. Runs the guard's own logic against a plan with an
    /// injected, unexplained scan of <c>fact</c> and confirms it is caught.
    /// </summary>
    [Fact]
    public void Guard_CatchesAnUnexplainedScanOfFact()
    {
        var lines = new List<string> { "SCAN f USING INDEX some_other_index", "SEARCH ft USING PRIMARY KEY (token=?)" };

        Assert.NotEmpty(UnexplainedFactScans(lines));
    }

    private static List<string> UnexplainedFactScans(IEnumerable<string> lines) => lines
        .Where(line => line.Contains("SCAN", StringComparison.Ordinal))
        .Where(line => line.Contains(" f ", StringComparison.Ordinal) || line.Contains(" f2", StringComparison.Ordinal) || line.EndsWith(" f", StringComparison.Ordinal))
        .ToList();

    private static List<string> CapturePlan(SqliteConnection connection, bool overlapAvailable, bool vectorAvailable, bool filtered)
    {
        var text = RecallRanker.StatementFor(overlapAvailable, vectorAvailable, "fact_vec", filtered);
        if (filtered)
        {
            Assert.Contains("json_each($types)", text, StringComparison.Ordinal);
        }

        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN\n" + text;
        command.Parameters.AddWithValue("$terms", "[]");
        command.Parameters.AddWithValue("$match", "\"kestrel\"");
        command.Parameters.AddWithValue("$seedK", 32);
        command.Parameters.AddWithValue("$currentSessionId", DBNull.Value);
        command.Parameters.AddWithValue("$now", T0.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$limit", 501);
        if (filtered)
        {
            command.Parameters.AddWithValue("$types", "[\"user\"]");
        }

        if (vectorAvailable)
        {
            command.Parameters.AddWithValue("$embedding", new float[] { 1f, 0f, 0f, 0f }.SelectMany(value => BitConverter.GetBytes(value)).ToArray());
        }

        using var reader = command.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
        {
            lines.Add(reader.GetString(3));
        }

        return lines;
    }
}
