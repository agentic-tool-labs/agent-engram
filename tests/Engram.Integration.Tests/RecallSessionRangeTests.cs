using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

public class RecallSessionRangeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(20000);

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
        FactStore.Remember(connection,
            new FactWrite("/sessions/🚀/null-session", "note", "states", "kestrel null session", "session", "stated"), Now);
        FactStore.Remember(connection,
            new FactWrite("/sessions0/outside", "note", "states", "kestrel outside subtree", "project", "stated"), Now);

        using (var rename = connection.CreateCommand())
        {
            // Leave fact.path stale: the indexed entity path must remain authoritative.
            rename.CommandText = "UPDATE entity SET path = '/knowledge/moved-note' WHERE id = (SELECT subject_id FROM fact WHERE id = $id);";
            rename.Parameters.AddWithValue("$id", moved);
            rename.ExecuteNonQuery();
        }
        if (vector)
        {
            VectorIndex.EnsureCreated(connection, new EmbeddingSpace("test", 4));
            VectorIndex.Write(connection, null, current, [1f, 0f, 0f, 0f]);
            VectorIndex.Write(connection, null, prior, [1f, 0f, 0f, 0f]);
        }

        var statement = RecallRanker.StatementFor(overlap, vector, "fact_vec");
        var start = statement.IndexOf("prior_sessions(session_id, label_index) AS (", StringComparison.Ordinal);
        var end = statement.IndexOf("candidates AS (", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        // The previous CTE is an equivalence oracle only. All other ranking SQL stays identical.
        var legacy = statement[..start] +
            """
            prior_sessions(session_id, label_index) AS (
              SELECT DISTINCT f.session_id, DENSE_RANK() OVER (ORDER BY f.session_id)
              FROM fact f JOIN entity e ON e.id = f.subject_id
              WHERE f.valid_to IS NULL AND substr(e.path, 1, 10) = '/sessions/'
                AND f.session_id IS NOT $currentSessionId
            ),

            """ + statement[end..];
        Assert.NotEqual(legacy, statement);
        foreach (var session in new long?[] { null, SessionStore.FindSession(connection, "current") })
        {
            var expected = Rows(connection, legacy, session, vector);
            var actual = Rows(connection, statement, session, vector);
            Assert.NotEmpty(expected);
            Assert.Equal(expected.Count, actual.Count);
            for (var row = 0; row < expected.Count; row++) Assert.Equal(expected[row], actual[row]);
        }
    }

    private static List<object[]> Rows(SqliteConnection connection, string sql, long? currentSession, bool vector)
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
        var rows = new List<object[]>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(values);
        }
        return rows;
    }
}
