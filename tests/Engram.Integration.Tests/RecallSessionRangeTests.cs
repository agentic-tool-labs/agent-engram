using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

public class RecallSessionRangeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(20000);

    // The previous CTE is an equivalence oracle only. All other ranking SQL stays identical.
    private const string LegacyOracle =
        """
        prior_sessions(session_id, label_index) AS (
          SELECT DISTINCT f.session_id, DENSE_RANK() OVER (ORDER BY f.session_id)
          FROM fact f JOIN entity e ON e.id = f.subject_id
          WHERE f.valid_to IS NULL AND substr(e.path, 1, 10) = '/sessions/'
            AND f.session_id IS NOT $currentSessionId
        ),

        """;

    // D84: the oracle minus its NULL-session row, which the new CTE drops on purpose.
    private const string LegacyOracleWithoutNullSession =
        """
        prior_sessions(session_id, label_index) AS (
          SELECT DISTINCT f.session_id, DENSE_RANK() OVER (ORDER BY f.session_id)
          FROM fact f JOIN entity e ON e.id = f.subject_id
          WHERE f.valid_to IS NULL AND substr(e.path, 1, 10) = '/sessions/'
            AND f.session_id IS NOT $currentSessionId AND f.session_id IS NOT NULL
        ),

        """;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EntityRange_PreservesEveryRankedColumnAndSessionLabel(bool overlap, bool vector)
    {
        Assert.SkipUnless(!vector || VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);
        using var sandbox = new SandboxHome(initialize: false);
        if (vector) VectorExtensionFile.InstallInto(sandbox.Home);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var (current, prior) = Seed(connection, nullSessionBody: "kestrel null session");
        if (vector)
        {
            VectorIndex.EnsureCreated(connection, new EmbeddingSpace("test", 4));
            VectorIndex.Write(connection, null, current, [1f, 0f, 0f, 0f]);
            VectorIndex.Write(connection, null, prior, [1f, 0f, 0f, 0f]);
        }

        var statement = RecallRanker.StatementFor(overlap, vector, "fact_vec");
        AssertRowsEqual(connection, WithOracle(statement, LegacyOracleWithoutNullSession), statement, vector);
    }

    [Fact]
    public void NoNullSessionNote_RowsMatchTheUnmodifiedLegacyOracle()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Seed(connection, nullSessionBody: null);

        var statement = RecallRanker.StatementFor(true, false, "fact_vec");
        AssertRowsEqual(connection, WithOracle(statement, LegacyOracle), statement, vector: false);
    }

    [Fact]
    public void NullSessionNote_NotACandidate_ShiftsEveryPriorLabelDownByOne()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Seed(connection, nullSessionBody: "gannets null session");
        var currentSession = SessionStore.FindSession(connection, "current");

        var statement = RecallRanker.StatementFor(true, false, "fact_vec");
        var before = Labels(Rows(connection, WithOracle(statement, LegacyOracle), currentSession, false));
        var after = Labels(Rows(connection, statement, currentSession, false));

        // prior's note and closed's object-bearing note; unmatched and the NULL note never rank.
        Assert.Equal(2, before.Count);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (factId, label) in before) Assert.Equal(label - 1, after[factId]);
    }

    [Fact]
    public void NullSessionNote_ThatMatches_RendersPQuestion_AndOthersTakeD84Numbering()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var (current, prior) = Seed(connection, nullSessionBody: "kestrel null session");
        var nullSession = FactIdOf(connection, "kestrel null session");
        var edge = FactIdOf(connection, "kestrel object-bearing note");

        var withCurrent = Rank(connection, SessionStore.FindSession(connection, "current"));
        Assert.Contains("· p?", LineOf(withCurrent, nullSession), StringComparison.Ordinal);
        Assert.Contains("· p2 ·", LineOf(withCurrent, prior), StringComparison.Ordinal);
        Assert.Contains("· p3 ·", LineOf(withCurrent, edge), StringComparison.Ordinal);

        // No current session: the current session's notes are prior, and still no NULL row. The
        // origin CASE compares with `=` since D90, so with no current session nothing is current
        // and the NULL note is a prior-session line that renders p? here too.
        var noCurrent = Rank(connection, null);
        var asPrior = noCurrent.Candidates.Single(c => c.FactId == nullSession);
        Assert.Equal(FactOrigin.PriorSession, asPrior.Origin);
        Assert.Contains("· p?", asPrior.Line, StringComparison.Ordinal);
        Assert.DoesNotContain(noCurrent.Candidates, c => c.Origin == FactOrigin.CurrentSession);
        Assert.Equal(FactOrigin.PriorSession, withCurrent.Candidates.Single(c => c.FactId == nullSession).Origin);
        Assert.Contains("· p2 ·", LineOf(noCurrent, current), StringComparison.Ordinal);
        Assert.Contains("· p3 ·", LineOf(noCurrent, prior), StringComparison.Ordinal);
        Assert.Contains("· p4 ·", LineOf(noCurrent, edge), StringComparison.Ordinal);
    }

    [Fact]
    public void PriorSessionNoteWithASessionButNoLabel_StillThrows()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Seed(connection, nullSessionBody: null);

        // Simulated drift: a session id with no session row is a prior-session note the CTE
        // cannot label, though its session_id is not NULL.
        Exec(connection, "PRAGMA foreign_keys = OFF;");
        FactStore.Remember(connection,
            new FactWrite("/sessions/drift/note", "note", "states", "kestrel drifted note", "session", "stated",
                SessionId: 999_999), Now);
        Exec(connection, "PRAGMA foreign_keys = ON;");

        var exception = Assert.Throws<InvalidOperationException>(() => Rank(connection, null));
        Assert.Contains("drifted apart", exception.Message, StringComparison.Ordinal);
    }

    // Sessions in creation order: unmatched, current, prior, closed (only an object-bearing note
    // stays live), moved (its entity renamed out of /sessions/).
    private static (long Current, long Prior) Seed(SqliteConnection connection, string? nullSessionBody)
    {
        // An earlier non-matching session must still count toward later pN labels.
        SessionFacts.Append(connection, "unmatched", "gannets resting quietly", null, null, null, Now);
        var current = SessionFacts.Append(connection, "current", "kestrel current note", null, null, null, Now);
        var prior = SessionFacts.Append(connection, "prior", "kestrel prior 東京 note", null, null, "助手", Now);
        var closed = SessionFacts.Append(connection, "closed", "kestrel closed note", null, null, null, Now);
        FactStore.Forget(connection, closed, "closed", Now.AddSeconds(1));
        FactStore.Remember(connection,
            new FactWrite("/sessions/structured/note", "note", "states", "kestrel object-bearing note", "session", "stated",
                SessionId: SessionStore.FindSession(connection, "closed"), ObjectPath: "/knowledge/target", ObjectKind: "concept"), Now);
        var moved = SessionFacts.Append(connection, "moved", "kestrel moved note", null, null, null, Now);
        if (nullSessionBody is not null)
        {
            FactStore.Remember(connection,
                new FactWrite("/sessions/🚀/null-session", "note", "states", nullSessionBody, "session", "stated"), Now);
        }
        FactStore.Remember(connection,
            new FactWrite("/sessions0/outside", "note", "states", "kestrel outside subtree", "project", "stated"), Now);

        using var rename = connection.CreateCommand();
        // Leave fact.path stale: the indexed entity path must remain authoritative.
        rename.CommandText = "UPDATE entity SET path = '/knowledge/moved-note' WHERE id = (SELECT subject_id FROM fact WHERE id = $id);";
        rename.Parameters.AddWithValue("$id", moved);
        rename.ExecuteNonQuery();
        return (current, prior);
    }

    private static string WithOracle(string statement, string oracle)
    {
        var start = statement.IndexOf("prior_sessions(session_id, label_index) AS (", StringComparison.Ordinal);
        var end = statement.IndexOf("candidates AS (", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var legacy = statement[..start] + oracle + statement[end..];
        Assert.NotEqual(legacy, statement);
        return legacy;
    }

    private static void AssertRowsEqual(SqliteConnection connection, string legacy, string statement, bool vector)
    {
        foreach (var session in new long?[] { null, SessionStore.FindSession(connection, "current") })
        {
            var expected = Rows(connection, legacy, session, vector);
            var actual = Rows(connection, statement, session, vector);
            Assert.NotEmpty(expected);
            Assert.Equal(expected.Count, actual.Count);
            for (var row = 0; row < expected.Count; row++) Assert.Equal(expected[row].Values, actual[row].Values);
        }
    }

    private static RankOutcome Rank(SqliteConnection connection, long? currentSession) =>
        RecallRanker.Rank(connection, "kestrel", RetrievalSettings.DefaultBudgetTokens, RetrievalSettings.DefaultSeedK,
            currentSession, Now, VectorLaneQuery.Stopped(VectorLaneState.Off, "test"));

    private static string LineOf(RankOutcome outcome, long factId) =>
        outcome.Candidates.Single(candidate => candidate.FactId == factId).Line;

    private static long FactIdOf(SqliteConnection connection, string body)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM fact WHERE body = $body AND valid_to IS NULL;";
        command.Parameters.AddWithValue("$body", body);
        return (long)command.ExecuteScalar()!;
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // Prior-session rows only (origin 2) that carry a label.
    private static Dictionary<long, long> Labels(List<Row> rows) =>
        rows.Where(row => (long)row["origin"] == 2 && row["label_index"] is not DBNull)
            .ToDictionary(row => (long)row["fact_id"], row => (long)row["label_index"]);

    private sealed record Row(string[] Names, object[] Values)
    {
        public object this[string name] => Values[Array.IndexOf(Names, name)];
    }

    private static List<Row> Rows(SqliteConnection connection, string sql, long? currentSession, bool vector)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$terms", "[\"kestrel\"]");
        command.Parameters.AddWithValue("$match", "\"kestrel\"");
        command.Parameters.AddWithValue("$seedK", 32);
        command.Parameters.AddWithValue("$currentSessionId", currentSession is { } id ? id : DBNull.Value);
        command.Parameters.AddWithValue("$now", Now.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$limit", 501);
        if (vector) command.Parameters.AddWithValue("$embedding", new float[] { 1f, 0f, 0f, 0f }.SelectMany(value => BitConverter.GetBytes(value)).ToArray());
        using var reader = command.ExecuteReader();
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<Row>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(new Row(names, values));
        }
        return rows;
    }
}
