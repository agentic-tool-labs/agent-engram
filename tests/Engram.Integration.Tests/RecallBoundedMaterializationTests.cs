using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// D90: recall joins and scores only the first <c>$limit</c> eligible overlap hits plus every
/// lexical, vector and current-session hit. Each case compares the bounded call with an unbounded
/// oracle — the same statement with <c>minCandidates</c> above every candidate, which materializes
/// everything — and checks the oracle's totals against its own rows, so a totals defect cannot hide
/// by being in both.
/// </summary>
public class RecallBoundedMaterializationTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch.AddDays(20000);
    private static readonly VectorLaneQuery Off = VectorLaneQuery.Stopped(VectorLaneState.Off, "test");
    private const int Unbounded = 100_000;
    private const int Budget = 3; // $limit 4

    // Query "alpha beta gamma zeta" ranks the fixture by construction:
    //   1-7   six tool facts and a directive, "alpha beta gamma" (score 3), never returned by default
    //   8-19  twelve project facts, "alpha beta" (score 2), tied, so ordered by handle
    //   then  user facts "alpha beta" interleave with those; "zeta" and the session notes score 1
    // At seedK 1 the lexical lane's one hit is the "zeta" fact, whose overlap rank is far below the
    // prefix; the current-session note is deeper still.
    private const string Deep = "alpha beta gamma zeta";

    private sealed record Fixture(long Lexical, long CurrentNote, long CurrentSession, long Directive, IReadOnlyList<long> Tools, IReadOnlyList<long> User);

    private static Fixture Seed(SqliteConnection connection, int busyNotes = 0)
    {
        var tools = Enumerable.Range(0, 6).Select(i => Write(connection, $"tool/{i}", $"alpha beta gamma ledger {i}", "tool")).ToList();
        var directive = DirectiveFacts.Add(connection, "alpha beta gamma directive", T0);
        for (var i = 0; i < 12; i++)
        {
            Write(connection, $"project/{i}", $"alpha beta project {i}", "project");
        }

        var user = Enumerable.Range(0, 6).Select(i => Write(connection, $"user/{i}", $"alpha beta preference {i}", "user")).ToList();
        var lexical = Write(connection, "project/zeta", "zeta", "project");
        SessionFacts.Append(connection, "earlier", "gamma earlier note", null, null, null, T0);
        var currentNote = SessionFacts.Append(connection, "current", "gamma current note", null, null, null, T0);
        for (var i = 0; i < busyNotes; i++)
        {
            SessionFacts.Append(connection, "current", $"gamma busy note {i}", null, null, null, T0);
        }

        return new Fixture(lexical, currentNote, SessionStore.FindSession(connection, "current")!.Value, directive, tools, user);
    }

    [Fact]
    public void ALexicalHitAndACurrentSessionNote_RankedBelowThePrefix_AreStillMaterializedExactly()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection);

        var (bounded, _) = AssertMatchesOracle(connection, Deep, ids.CurrentSession);

        var note = bounded.Candidates.Single(c => c.FactId == ids.CurrentNote);
        Assert.Equal(FactOrigin.CurrentSession, note.Origin);
        Assert.True(note.OverlapRank > Budget + 1 + 7, $"the note's overlap rank {note.OverlapRank} must lie below the prefix");
        var lexical = bounded.Candidates.Single(c => c.FactId == ids.Lexical);
        Assert.Equal(1, lexical.LexicalRank);
        Assert.True(lexical.OverlapRank > Budget + 1 + 7, $"the lexical hit's overlap rank {lexical.OverlapRank} must lie below the prefix");
        // It keeps the overlap rank it has among every hit, so two lanes found it.
        Assert.Equal(1, bounded.CorroboratedTotal);
    }

    [Fact]
    public void WithNoCurrentSession_ThePrefixAloneFillsTheWindow_PastTheExcludedTopRanks()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection);

        var (bounded, oracle) = AssertMatchesOracle(connection, "alpha beta gamma", currentSession: null);

        Assert.Equal(Budget + 1, bounded.Candidates.Count);
        Assert.All(bounded.Candidates, c => Assert.Null(c.LexicalRank));
        Assert.DoesNotContain(bounded.Candidates, c => c.FactId == ids.Directive || ids.Tools.Contains(c.FactId!.Value));
        Assert.DoesNotContain(bounded.Candidates, c => c.Origin == FactOrigin.CurrentSession);
        // The window ends inside the twelve tied overlap scores, yet nothing past it ties the last row
        // kept: ranks are unique, so fused is strictly lower. The deep overlap-only hits are counted.
        Assert.True(bounded.Candidates[^1].Fused > oracle.Candidates[Budget + 1].Fused);
        Assert.True(bounded.MatchedTotal > bounded.Candidates.Count + 10);
    }

    [Theory]
    [InlineData(0)]   // $limit 1
    [InlineData(500)] // every eligible hit fits
    public void TheBudgetSetsThePrefix_AtBothExtremes(int budget)
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection);

        AssertMatchesOracle(connection, Deep, ids.CurrentSession, budget: budget);
        AssertMatchesOracle(connection, "alpha beta gamma", null, budget: budget);
    }

    [Fact]
    public void ExplainsLargerLimit_RaisesThePrefix()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Seed(connection);

        var (bounded, _) = AssertMatchesOracle(connection, "alpha beta gamma", null, minCandidates: 9);

        Assert.Equal(9, bounded.Candidates.Count);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("session")]
    [InlineData("project,user")]
    public void ATypesFilter_TakesItsPrefixOverEligibleHitsOnly(string types)
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection, busyNotes: 3);
        Assert.True(MemoryScopes.TryNormalize(types.Split(','), out var filter, out _));

        var (bounded, _) = AssertMatchesOracle(connection, "alpha beta gamma", ids.CurrentSession, types: filter);

        Assert.All(bounded.Candidates, c => Assert.Contains(c.Source!.Scope, filter!));
        Assert.Equal(Budget + 1, bounded.Candidates.Count);
    }

    [Fact]
    public void ACurrentSessionWithMoreMatchingNotesThanTheLimit_FillsTheWindowWithThem()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection, busyNotes: 6);

        var (bounded, _) = AssertMatchesOracle(connection, "alpha beta gamma", ids.CurrentSession);

        Assert.All(bounded.Candidates, c => Assert.Equal(FactOrigin.CurrentSession, c.Origin));
    }

    [Fact]
    public void APinReordersOnlyTheMaterializedRows_AsItDidTheWindow()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection);
        var window = Rank(connection, Deep, ids.CurrentSession).Candidates;
        var pins = new HashSet<long> { window[^1].FactId!.Value };

        var pinned = Rank(connection, Deep, ids.CurrentSession, pins: pins);
        var oracle = Rank(connection, Deep, ids.CurrentSession, minCandidates: Unbounded);
        var expected = RecallEngine.ApplyPinBoost(oracle.Candidates.Take(Budget + 1).Select(Unpacked).ToList(), pins);

        Assert.Equal(expected.Select(Unpacked), pinned.Candidates.Select(Unpacked));
        Assert.True(pinned.Candidates.Single(c => pins.Contains(c.FactId!.Value)).Pinned);
    }

    [Theory]
    [InlineData("the and")]  // stopwords only
    [InlineData("!!! ...")]  // no token at all: the sentinel MATCH
    [InlineData("omega")]    // porter reaches "omegas"; the literal token index does not: zero overlap hits
    public void QueriesWithFewOrNoOverlapHits_MatchTheOracle(string query)
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection);
        var lexOnly = Write(connection, "project/omegas", "omegas and the rest", "project");

        var (bounded, _) = AssertMatchesOracle(connection, query, ids.CurrentSession, seedK: 2);

        if (query == "omega")
        {
            var row = Assert.Single(bounded.Candidates);
            Assert.Equal(lexOnly, row.FactId);
            Assert.Null(row.OverlapRank);
            Assert.Equal(1, bounded.MatchedTotal);
        }
    }

    [Fact]
    public void ALexicalOnlyFact_BesideManyOverlapHits_IsCountedOnce()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Seed(connection);
        var lexOnly = Write(connection, "project/omegas", "omegas", "project");

        var (bounded, oracle) = AssertMatchesOracle(connection, "alpha omega", null, seedK: 1);

        var row = bounded.Candidates.Single(c => c.FactId == lexOnly);
        Assert.Null(row.OverlapRank);
        Assert.Equal(1, row.LexicalRank);
        Assert.Equal(1, oracle.Candidates.Count(c => c.OverlapRank is null));
    }

    [Fact]
    public void AStoreOfFiveFacts_IsMaterializedWhole()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        for (var i = 0; i < 5; i++)
        {
            Write(connection, $"small/{i}", $"alpha small {i}", "project");
        }

        var (bounded, _) = AssertMatchesOracle(connection, "alpha", null, budget: 10);

        Assert.Equal(5, bounded.Candidates.Count);
    }

    [Fact]
    public void WithTheOverlapLaneUnavailable_OnlyLexAndVecAreMaterialized_AndNothingCorroborates()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection);
        Execute(connection, "DELETE FROM schema_meta WHERE key = 'fact_token_version';");
        Assert.Equal(FactTokenIndexState.Unbuilt, FactTokenIndex.ReadState(connection));

        var (bounded, _) = AssertMatchesOracle(connection, Deep, ids.CurrentSession, seedK: 5);

        Assert.All(bounded.Candidates, c => Assert.Null(c.OverlapRank));
        Assert.Equal(0, bounded.CorroboratedTotal);
    }

    // U1 (D90): totals count overlap hits without joining them, so a token row a closed fact left
    // behind is counted — and, because `scored` still requires a live fact, never returned.
    [Fact]
    public void AStaleTokenRow_IsCountedButNeverReturned()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection);
        var closed = Write(connection, "project/closed", "alpha beta gamma closed", "project");
        var before = Rank(connection, "alpha beta gamma", null, minCandidates: Unbounded);
        FactStore.Forget(connection, closed, "closed", T0.AddSeconds(1));
        Execute(connection, $"INSERT INTO fact_token(token, fact_id) VALUES ('alpha', {closed}), ('beta', {closed}), ('gamma', {closed});");

        var after = Rank(connection, "alpha beta gamma", null, minCandidates: Unbounded);

        Assert.DoesNotContain(after.Candidates, c => c.FactId == closed);
        Assert.Equal(before.MatchedTotal, after.MatchedTotal);
        Assert.Equal(before.Candidates.Count - 1, after.Candidates.Count);
        Assert.DoesNotContain(ids.Directive, after.Candidates.Select(c => c.FactId!.Value));
    }

    [Fact]
    public void WithTheVectorLaneOn_ALexAndVecFactOutsideOverlap_CorroboratesAndMatchesTheOracle()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new VectorSandbox();
        var connection = sandbox.Connection;
        var ids = Seed(connection);
        var lexOnly = Write(connection, "project/omegas", "omegas", "project");
        var deepVector = ids.User[^1];
        VectorIndex.EnsureCreated(connection, new EmbeddingSpace("stand-in-embed", 8));
        VectorIndex.Write(connection, null, lexOnly, Unit(0));
        VectorIndex.Write(connection, null, deepVector, Unit(1));
        var vector = new VectorLaneQuery(VectorLaneState.Queried, "test", Unit(0), new EmbeddingSpace("stand-in-embed", 8));

        var (bounded, _) = AssertMatchesOracle(connection, "alpha omega", ids.CurrentSession, seedK: 2, vector: vector);

        var row = bounded.Candidates.Single(c => c.FactId == lexOnly);
        Assert.Null(row.OverlapRank);
        Assert.NotNull(row.LexicalRank);
        Assert.NotNull(row.VectorRank);
        Assert.True(bounded.CorroboratedTotal >= 1);
    }

    private static float[] Unit(int axis)
    {
        var vector = new float[8];
        vector[axis] = 1f;
        return vector;
    }

    private static (RankOutcome Bounded, RankOutcome Oracle) AssertMatchesOracle(
        SqliteConnection connection, string query, long? currentSession, int budget = Budget, int seedK = 1,
        int minCandidates = 0, IReadOnlyList<string>? types = null, VectorLaneQuery? vector = null)
    {
        var bounded = Rank(connection, query, currentSession, budget, seedK, minCandidates, types, vector: vector);
        var oracle = Rank(connection, query, currentSession, budget, seedK, Unbounded, types, vector: vector);

        // The oracle materializes every candidate, so its totals must agree with its own rows.
        Assert.True(oracle.Candidates.Count < Unbounded);
        Assert.Equal(oracle.Candidates.Count, oracle.MatchedTotal);
        Assert.Equal(oracle.Candidates.Count(c => (c.OverlapRank is null ? 0 : 1) + (c.LexicalRank is null ? 0 : 1) + (c.VectorRank is null ? 0 : 1) >= 2), oracle.CorroboratedTotal);

        var limit = Math.Max(budget + 1, minCandidates);
        Assert.Equal(oracle.Candidates.Take(limit).Select(Unpacked), bounded.Candidates.Select(Unpacked));
        Assert.Equal(oracle.MatchedTotal, bounded.MatchedTotal);
        Assert.Equal(oracle.CorroboratedTotal, bounded.CorroboratedTotal);
        Assert.Equal(oracle.Coverage, bounded.Coverage);
        return (bounded, oracle);
    }

    // Packing differs by design past the window, since ApplyBudget skips rather than stops.
    private static RecallCandidate Unpacked(RecallCandidate candidate) => candidate with { Packed = false };

    private static RankOutcome Rank(
        SqliteConnection connection, string query, long? currentSession, int budget = Budget, int seedK = 1,
        int minCandidates = 0, IReadOnlyList<string>? types = null, IReadOnlySet<long>? pins = null, VectorLaneQuery? vector = null) =>
        RecallRanker.Rank(connection, query, budget, seedK, currentSession, T0.AddSeconds(5), vector ?? Off, minCandidates, pins, types);

    private static long Write(SqliteConnection connection, string name, string body, string scope) =>
        FactStore.Remember(connection, new FactWrite($"/knowledge/{name}", "concept", "is", body, scope, "stated"), T0).FactId;

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
