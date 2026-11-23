using System.Text;
using System.Text.Json.Nodes;
using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// D106: <c>lesson-feedback</c> records a reflection model's judgement of a delivered lesson into
/// <c>salience</c>, and the role block is chosen by those counts through one function.
/// </summary>
public class LessonFeedbackTests
{
    private const string Mod = "digest";
    private const string Session = "cc-session-1";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static (int Status, JsonNode Body) Feedback(SandboxHome sandbox, long factId, string verdict) =>
        Feedback(sandbox, FactCatalog.HandleFor(factId), verdict);

    private static (int Status, JsonNode Body) Feedback(SandboxHome sandbox, string handle, string verdict)
    {
        var body = new JsonObject { ["mod"] = Mod, ["session_id"] = Session, ["fact_id"] = handle, ["verdict"] = verdict };
        var result = ModApi.Execute(
            sandbox.Home, new LocalRuntime(sandbox.Home), "lesson-feedback", Mod, Encoding.UTF8.GetBytes(body.ToJsonString()));
        return (result.Status, JsonNode.Parse(result.Json)!);
    }

    private static long Lesson(SandboxHome sandbox, string statement, string role = "main", string session = "writer")
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var transaction = EngramDatabase.BeginWrite(connection);
        var (id, outcome) = RoleNotes.Append(connection, transaction, session, role, statement, null, "a test", Now);
        Assert.Equal(RoleWrite.Written, outcome);
        transaction.Commit();
        return id;
    }

    private static (long Access, long? LastAccessed, long Confirmations, double Score)? Salience(SandboxHome sandbox, long factId)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT access_count, last_accessed, confirmations, score FROM salience WHERE fact_id = $id;";
        command.Parameters.AddWithValue("$id", factId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetInt64(2), reader.GetDouble(3))
            : null;
    }

    private static long? ReviewAfter(SandboxHome sandbox, long factId)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT review_after FROM fact_review WHERE fact_id = $id;";
        command.Parameters.AddWithValue("$id", factId);
        return command.ExecuteScalar() as long?;
    }

    private static string TableDump(SandboxHome sandbox, string sql)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new StringBuilder();
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                rows.Append(reader.IsDBNull(i) ? "null" : reader.GetValue(i).ToString()).Append('|');
            }

            rows.Append('\n');
        }

        return rows.ToString();
    }

    private static List<TelemetryRecord> Records(SandboxHome sandbox)
    {
        var path = Telemetry.ResolvePath(sandbox.Home);
        return File.Exists(path)
            ? File.ReadAllLines(path).Select(Telemetry.TryParse).OfType<TelemetryRecord>().ToList()
            : [];
    }

    [Fact]
    public void Applied_AddsAnAccessAndAConfirmation_AndNeverWritesTheScore()
    {
        using var sandbox = new SandboxHome();
        var lesson = Lesson(sandbox, "Run the targeted tests first.");
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.Equal(200, Feedback(sandbox, lesson, "applied").Status);
        Assert.Equal(200, Feedback(sandbox, lesson, "applied").Status);

        var (access, lastAccessed, confirmations, score) = Salience(sandbox, lesson)!.Value;
        Assert.Equal(2, access);
        Assert.Equal(2, confirmations);
        Assert.InRange(lastAccessed!.Value, before, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.Equal(0.5, score);
        Assert.Null(ReviewAfter(sandbox, lesson));
    }

    [Fact]
    public void Contradicted_AddsAnAccessOnly_AndMakesTheLessonDueNow()
    {
        using var sandbox = new SandboxHome();
        var lesson = Lesson(sandbox, "Always squash commits.");
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.Equal(200, Feedback(sandbox, lesson, "contradicted").Status);

        var (access, _, confirmations, _) = Salience(sandbox, lesson)!.Value;
        Assert.Equal(1, access);
        Assert.Equal(0, confirmations);
        Assert.InRange(ReviewAfter(sandbox, lesson)!.Value, before, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [Fact]
    public void Contradicted_DoesNotMoveAnEarlierReviewDateLater()
    {
        using var sandbox = new SandboxHome();
        var lesson = Lesson(sandbox, "Always squash commits.");
        var earlier = Now.AddDays(-3).ToUnixTimeSeconds();
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            FactReview.Set(connection, null, lesson, earlier, earlier);
        }

        Feedback(sandbox, lesson, "contradicted");

        Assert.Equal(earlier, ReviewAfter(sandbox, lesson));
    }

    // The nearest input that must still move: a review set for later is brought forward to now.
    [Fact]
    public void Contradicted_BringsALaterReviewDateForwardToNow()
    {
        using var sandbox = new SandboxHome();
        var lesson = Lesson(sandbox, "Always squash commits.");
        var later = Now.AddDays(30).ToUnixTimeSeconds();
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            FactReview.Set(connection, null, lesson, later, later);
        }

        Feedback(sandbox, lesson, "contradicted");

        Assert.True(ReviewAfter(sandbox, lesson) < later);
    }

    [Fact]
    public void RecordsItsOwnKind_WithTheVerdict_AndNoRememberJudgeOrModCall()
    {
        using var sandbox = new SandboxHome();
        var lesson = Lesson(sandbox, "Run the targeted tests first.");

        Feedback(sandbox, lesson, "contradicted");

        var record = Assert.Single(Records(sandbox));
        Assert.Equal(TelemetryEventKind.LessonFeedback, record.Kind);
        Assert.Equal(Session, record.SessionId);
        Assert.Equal(Mod, record.Mod);
        Assert.Equal(FactCatalog.HandleFor(lesson), record.FactId);
        Assert.Equal("contradicted", record.Verdict);
    }

    [Fact]
    public void TouchesNoFactOrSupersessionRow()
    {
        using var sandbox = new SandboxHome();
        var lesson = Lesson(sandbox, "Run the targeted tests first.");
        const string facts = "SELECT id, subject_id, predicate, body, details, scope, valid_from, valid_to, superseded_by, path FROM fact ORDER BY id;";
        const string supersessions = "SELECT * FROM supersession;";
        var factsBefore = TableDump(sandbox, facts);
        var supersessionsBefore = TableDump(sandbox, supersessions);

        Feedback(sandbox, lesson, "applied");
        Feedback(sandbox, lesson, "contradicted");

        Assert.Equal(factsBefore, TableDump(sandbox, facts));
        Assert.Equal(supersessionsBefore, TableDump(sandbox, supersessions));
    }

    public static TheoryData<string> NotARoleLesson => ["session note", "project note", "closed role fact", "unknown id"];

    [Theory]
    [MemberData(nameof(NotARoleLesson))]
    public void OnAnythingButALiveRoleLesson_Is400_AndChangesNothing(string target)
    {
        using var sandbox = new SandboxHome();
        Lesson(sandbox, "A real lesson, so the tables are not trivially empty.");
        long id;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            id = target switch
            {
                "session note" => SessionFacts.Append(connection, "s1", "A working note.", null, null, null, Now),
                "project note" => ProjectNote(connection),
                "closed role fact" => ClosedLesson(connection),
                _ => 999_999,
            };
        }

        var salienceBefore = TableDump(sandbox, "SELECT * FROM salience;");
        var reviewBefore = TableDump(sandbox, "SELECT * FROM fact_review;");

        var (status, _) = Feedback(sandbox, id, "contradicted");

        Assert.Equal(400, status);
        Assert.Equal(salienceBefore, TableDump(sandbox, "SELECT * FROM salience;"));
        Assert.Equal(reviewBefore, TableDump(sandbox, "SELECT * FROM fact_review;"));
        Assert.Empty(Records(sandbox));
    }

    [Theory]
    [InlineData("helpful")]
    [InlineData("Applied")]
    [InlineData("")]
    public void AnUnknownVerdict_Is400_AndChangesNothing(string verdict)
    {
        using var sandbox = new SandboxHome();
        var lesson = Lesson(sandbox, "Run the targeted tests first.");

        Assert.Equal(400, Feedback(sandbox, lesson, verdict).Status);
        Assert.Null(Salience(sandbox, lesson));
    }

    [Fact]
    public void IsNotALookup()
    {
        Assert.True(ModApi.IsKnownOp("lesson-feedback"));
        Assert.False(ModApi.IsLookup("lesson-feedback"));
    }

    private static long ProjectNote(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var transaction = EngramDatabase.BeginWrite(connection);
        var (id, _) = ProjectNotes.Append(connection, transaction, "s1", "acme", "We ship on Tuesdays.", null, "e", Now);
        transaction.Commit();
        return id;
    }

    private static long ClosedLesson(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        long id;
        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            (id, _) = RoleNotes.Append(connection, transaction, "s1", "main", "A lesson later forgotten.", null, "e", Now);
            transaction.Commit();
        }

        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            Assert.True(FactStore.Forget(connection, transaction, id, "a test", Now, null));
            transaction.Commit();
        }

        return id;
    }

    // --- Selection (D106) ---

    private static void Judge(SandboxHome sandbox, long id, int applied, int contradicted)
    {
        for (var i = 0; i < applied; i++)
        {
            Assert.Equal(200, Feedback(sandbox, id, "applied").Status);
        }

        for (var i = 0; i < contradicted; i++)
        {
            Assert.Equal(200, Feedback(sandbox, id, "contradicted").Status);
        }
    }

    private static IReadOnlyDictionary<long, (int Confirmations, int AccessCount)> ReadJudgements(SandboxHome sandbox)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT fact_id, confirmations, access_count FROM salience;";
        using var reader = command.ExecuteReader();
        var judgements = new Dictionary<long, (int, int)>();
        while (reader.Read())
        {
            judgements[reader.GetInt64(0)] = (reader.GetInt32(1), reader.GetInt32(2));
        }

        return judgements;
    }

    /// <summary>
    /// Both summary paths over one seeded store with rows in every group: four proven (so the cap
    /// of three is exercised), one judged against once (still eligible), three set aside, and plain
    /// lessons beneath. Asserts the expected order as well as agreement, so reverting either path
    /// — or both — to id order reddens it.
    /// </summary>
    [Fact]
    public void ReadDeliveredAndSelectDelivered_AgreeOnAStoreWithEveryGroup()
    {
        using var sandbox = new SandboxHome();
        var ids = Enumerable.Range(1, 10)
            .Select(i => Lesson(sandbox, $"Role lesson {i}.", session: "writer-" + (i % 4)))
            .ToList();

        Judge(sandbox, ids[0], applied: 3, contradicted: 0); // proven, 3
        Judge(sandbox, ids[1], applied: 1, contradicted: 0); // proven, 1 — the fourth, falls to newest
        Judge(sandbox, ids[2], applied: 2, contradicted: 1); // proven, 2
        Judge(sandbox, ids[3], applied: 1, contradicted: 0); // proven, 1
        Judge(sandbox, ids[9], applied: 0, contradicted: 2); // set aside, the newest id
        Judge(sandbox, ids[8], applied: 0, contradicted: 3); // set aside
        Judge(sandbox, ids[7], applied: 0, contradicted: 2); // set aside
        Judge(sandbox, ids[6], applied: 0, contradicted: 1); // judged against once: still eligible

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var viaSql = RoleNotes.ReadDelivered(connection, "main").Select(f => f.Id).ToList();
        var viaMemory = RoleNotes.SelectDelivered(FactCatalog.ReadLongTerm(connection, Now), "main", ReadJudgements(sandbox))
            .Select(f => f.Id)
            .ToList();

        // Proven by confirmations then id (ids[0]=3, ids[2]=2, ids[3]=1 beats ids[1]=1 on id),
        // then the two newest of the rest (ids[6], ids[5]); the set-aside ones never fill.
        Assert.Equal([ids[0], ids[2], ids[3], ids[6], ids[5]], viaSql);
        Assert.Equal(viaSql, viaMemory);
    }

    [Fact]
    public void SetAsideLessons_FillOnlyWhenThereAreFewerThanFive()
    {
        using var sandbox = new SandboxHome();
        var ids = Enumerable.Range(1, 4).Select(i => Lesson(sandbox, $"Role lesson {i}.", session: "writer-" + (i % 2))).ToList();
        Judge(sandbox, ids[3], applied: 0, contradicted: 2);
        Judge(sandbox, ids[2], applied: 0, contradicted: 2);

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Equal([ids[1], ids[0], ids[3], ids[2]], RoleNotes.ReadDelivered(connection, "main").Select(f => f.Id));
    }

    [Fact]
    public void ASetAsideLesson_IsStillRecalled_AndListedForReview()
    {
        using var sandbox = new SandboxHome();
        var lesson = Lesson(sandbox, "Always rebase the zebrafish branch before merging.");
        Judge(sandbox, lesson, applied: 0, contradicted: 2);

        var recall = EngramMcpTools.Recall(
            sandbox.Home, new McpSessionId("mcp-1"), new McpHomeState(true), new LocalRuntime(sandbox.Home),
            new SessionPinStore(), "zebrafish branch rebase");
        var stdout = new StringWriter();
        Assert.Equal(0, CliApp.Run(["--home", sandbox.Home.Root, "review", "list"], stdout, new StringWriter()));

        Assert.Contains(FactCatalog.HandleFor(lesson), recall, StringComparison.Ordinal);
        Assert.Contains("zebrafish", stdout.ToString(), StringComparison.Ordinal);
    }
}
