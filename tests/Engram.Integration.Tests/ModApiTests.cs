using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2. Drives <see cref="ModApi.Execute"/> against a real SQLite store, so every row of the
/// contract that is not about HTTP itself is held here without a server.
/// </summary>
public class ModApiTests
{
    private const string Mod = "lens";
    private const string Session = "cc-session-1";

    private static (int Status, JsonNode Body) Call(
        SandboxHome sandbox, string op, JsonObject body, string? headerMod = null, TelemetryFeed? feed = null)
    {
        body["mod"] ??= Mod;
        var result = ModApi.Execute(
            sandbox.Home, new LocalRuntime(sandbox.Home), op, headerMod ?? Mod, Encoding.UTF8.GetBytes(body.ToJsonString()), feed);
        return (result.Status, JsonNode.Parse(result.Json)!);
    }

    private static List<TelemetryRecord> Telemetry(SandboxHome sandbox, string kind)
    {
        var path = Engram.Core.Telemetry.ResolvePath(sandbox.Home);
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Select(Engram.Core.Telemetry.TryParse)
            .OfType<TelemetryRecord>()
            .Where(r => r.Kind == kind)
            .ToList();
    }

    private static int LiveFactCount(SandboxHome sandbox)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM fact WHERE valid_to IS NULL;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string Remember(SandboxHome sandbox, string statement, string session = Session) =>
        (string)Call(sandbox, "remember", new JsonObject
        {
            ["session_id"] = session,
            ["statement"] = statement,
            ["evidence"] = "a test",
        }).Body["handle"]!;

    [Fact]
    public void Recall_Valid_WritesOneModCallAndNeitherRecallNorSessionOpen()
    {
        using var sandbox = new SandboxHome();

        var (status, body) = Call(sandbox, "recall", new JsonObject
        {
            ["session_id"] = Session,
            ["query"] = "BEGIN IMMEDIATE transaction",
            ["mode"] = "shadow",
        });

        Assert.Equal(200, status);
        Assert.Contains("SQLITE_BUSY_SNAPSHOT", (string)body["text"]!, StringComparison.Ordinal);
        var call = Assert.Single(Telemetry(sandbox, TelemetryEventKind.ModCall));
        Assert.Equal(Session, call.SessionId);
        Assert.Equal("recall", call.Tool);
        Assert.Equal(Mod, call.Mod);
        Assert.Equal("shadow", call.Mode);
        Assert.Equal((string)body["coverage"]!, call.Coverage);
        Assert.Null(call.FactCount);
        Assert.Empty(Telemetry(sandbox, TelemetryEventKind.Recall));
        Assert.Empty(Telemetry(sandbox, TelemetryEventKind.SessionOpen));
    }

    [Fact]
    public void ModCall_CarriesTheServersRecallDuration_OnRecallOnly()
    {
        using var sandbox = new SandboxHome();
        var handle = Remember(sandbox, "A note to retract and recall.");
        Call(sandbox, "recall", new JsonObject { ["session_id"] = Session, ["query"] = "retract recall" });
        Call(sandbox, "forget", new JsonObject { ["session_id"] = Session, ["fact_id"] = handle });

        var calls = Telemetry(sandbox, TelemetryEventKind.ModCall);
        var recall = Assert.Single(calls, c => c.Tool == "recall");

        Assert.NotNull(recall.DurationMs);
        Assert.True(recall.DurationMs >= 0 && recall.DurationMs < 60_000, recall.DurationMs.ToString());
        Assert.Null(recall.FactCount);
        Assert.All(calls.Where(c => c.Tool != "recall"), c => Assert.Null(c.DurationMs));
        Assert.Equal(2, calls.Count(c => c.Tool != "recall"));
    }

    [Fact]
    public void Recall_TextIsByteIdenticalToTheMcpToolForTheSameQuery()
    {
        using var sandbox = new SandboxHome();
        Remember(sandbox, "The build pipeline retries flaky uploads three times before failing.");

        foreach (var query in new[] { "BEGIN IMMEDIATE transaction", "flaky uploads retries", "zzzqqq nothing matches" })
        {
            var viaApi = (string)Call(sandbox, "recall", new JsonObject
            {
                ["session_id"] = Session,
                ["query"] = query,
            }).Body["text"]!;

            var viaMcp = EngramMcpTools.Recall(
                sandbox.Home, new McpSessionId(Session), new McpHomeState(true), new LocalRuntime(sandbox.Home),
                new SessionPinStore(), query);

            Assert.Equal(viaMcp, viaApi);
        }
    }

    [Theory]
    [InlineData(2001, 400)]
    [InlineData(2000, 200)]
    public void Recall_QueryLength_IsBoundedAtTwoThousand(int length, int expected)
    {
        using var sandbox = new SandboxHome();

        var (status, _) = Call(sandbox, "recall", new JsonObject
        {
            ["session_id"] = Session,
            ["query"] = new string('a', length),
        });

        Assert.Equal(expected, status);
        Assert.Equal(expected == 200 ? 1 : 0, Telemetry(sandbox, TelemetryEventKind.ModCall).Count);
    }

    [Theory]
    [InlineData(49, 400)]
    [InlineData(50, 200)]
    [InlineData(4000, 200)]
    [InlineData(4001, 400)]
    public void Recall_BudgetTokens_IsBoundedAtFiftyAndFourThousand(int budget, int expected)
    {
        using var sandbox = new SandboxHome();

        var (status, _) = Call(sandbox, "recall", new JsonObject
        {
            ["session_id"] = Session,
            ["query"] = "transaction",
            ["budget_tokens"] = budget,
        });

        Assert.Equal(expected, status);
    }

    [Theory]
    [InlineData("shadow", 200)]
    [InlineData("inject", 200)]
    [InlineData("Shadow", 400)]
    [InlineData("live", 400)]
    public void Recall_Mode_AcceptsOnlyShadowAndInject(string mode, int expected)
    {
        using var sandbox = new SandboxHome();

        var (status, _) = Call(sandbox, "recall", new JsonObject
        {
            ["session_id"] = Session,
            ["query"] = "transaction",
            ["mode"] = mode,
        });

        Assert.Equal(expected, status);
    }

    [Theory]
    [InlineData("recall")]
    [InlineData("remember")]
    [InlineData("forget")]
    [InlineData("captures")]
    [InlineData("tail")]
    public void WritingAndSessionOps_WithoutSessionId_Are400WithNoWriteAndNoTelemetry(string op)
    {
        using var sandbox = new SandboxHome();
        var before = LiveFactCount(sandbox);

        var (status, _) = Call(sandbox, op, new JsonObject
        {
            ["query"] = "transaction",
            ["statement"] = "a statement",
            ["evidence"] = "e",
            ["fact_id"] = "f1",
            ["since"] = 0,
        });

        Assert.Equal(400, status);
        Assert.Equal(before, LiveFactCount(sandbox));
        Assert.Empty(Telemetry(sandbox, TelemetryEventKind.ModCall));
    }

    [Theory]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    [InlineData("abc_DEF-123", true)]
    public void SessionId_AllowsOnlyLettersDigitsUnderscoreAndHyphen(string session, bool accepted)
    {
        using var sandbox = new SandboxHome();

        var (status, _) = Call(sandbox, "captures", new JsonObject { ["session_id"] = session, ["since"] = 0 });

        Assert.Equal(accepted ? 200 : 400, status);
    }

    [Theory]
    [InlineData(128, 200)]
    [InlineData(129, 400)]
    public void SessionId_IsBoundedAt128Characters(int length, int expected)
    {
        using var sandbox = new SandboxHome();

        var (status, _) = Call(sandbox, "captures", new JsonObject { ["session_id"] = new string('s', length), ["since"] = 0 });

        Assert.Equal(expected, status);
    }

    [Theory]
    [InlineData("lens", true)]
    [InlineData("a-1", true)]
    [InlineData("Lens", false)]
    [InlineData("le ns", false)]
    [InlineData("", false)]
    public void ModName_IsLowercaseLettersDigitsAndHyphens(string name, bool valid) =>
        Assert.Equal(valid, ModApi.IsModName(name));

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void ModName_IsBoundedAt32Characters(int length, bool valid) =>
        Assert.Equal(valid, ModApi.IsModName(new string('m', length)));

    [Fact]
    public void BodyModDifferentFromHeader_Is400AndWritesNothing()
    {
        using var sandbox = new SandboxHome();
        var before = LiveFactCount(sandbox);

        var (status, _) = Call(
            sandbox,
            "remember",
            new JsonObject { ["mod"] = "toasts", ["session_id"] = Session, ["statement"] = "x", ["evidence"] = "e" },
            headerMod: "lens");

        Assert.Equal(400, status);
        Assert.Equal(before, LiveFactCount(sandbox));
    }

    [Fact]
    public void BodyWithoutMod_AgainstAHeader_Is400()
    {
        using var sandbox = new SandboxHome();

        var result = ModApi.Execute(
            sandbox.Home, new LocalRuntime(sandbox.Home), "captures", "lens", """{"session_id":"s","since":0}"""u8);

        Assert.Equal(400, result.Status);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[1]")]
    [InlineData("""{"mod":"lens","budget_tokens":"many"}""")]
    public void MalformedBody_Is400(string body)
    {
        using var sandbox = new SandboxHome();

        var result = ModApi.Execute(sandbox.Home, new LocalRuntime(sandbox.Home), "recall", "lens", Encoding.UTF8.GetBytes(body));

        Assert.Equal(400, result.Status);
        Assert.Equal("bad_request", JsonNode.Parse(result.Json)!["error"]!.GetValue<string>());
    }

    [Fact]
    public void UnknownOp_Is404Json()
    {
        using var sandbox = new SandboxHome();

        var result = ModApi.Execute(sandbox.Home, new LocalRuntime(sandbox.Home), "foo", "lens", """{"mod":"lens"}"""u8);

        Assert.Equal(404, result.Status);
        Assert.Equal("not_found", JsonNode.Parse(result.Json)!["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("recall")]
    [InlineData("fact")]
    [InlineData("history")]
    [InlineData("forget")]
    [InlineData("remember")]
    [InlineData("captures")]
    [InlineData("path-facts")]
    [InlineData("tail")]
    public void HomeWithoutConfig_Is503NotInitialisedOnEveryOp(string op)
    {
        using var sandbox = new SandboxHome(initialize: false);

        var (status, body) = Call(sandbox, op, new JsonObject
        {
            ["session_id"] = Session,
            ["query"] = "q",
            ["fact_id"] = "f1",
            ["statement"] = "s",
            ["evidence"] = "e",
            ["since"] = 0,
            ["path"] = "/tmp/x",
        });

        Assert.Equal(503, status);
        Assert.Equal("not_initialised", (string)body["error"]!);
    }

    [Fact]
    public void Fact_ReturnsTheStoredFact_AndRejectsMalformedAndAbsentHandles()
    {
        using var sandbox = new SandboxHome();
        var handle = Remember(sandbox, "The deploy script lives in tools/deploy.sh.");

        var (status, body) = Call(sandbox, "fact", new JsonObject { ["fact_id"] = handle });

        Assert.Equal(200, status);
        Assert.Equal(handle, (string)body["handle"]!);
        Assert.Equal("The deploy script lives in tools/deploy.sh.", (string)body["body"]!);
        Assert.True((bool)body["live"]!);
        Assert.Null(body["valid_to"]);
        Assert.Equal(1, (int)body["versions"]!);

        Assert.Equal(400, Call(sandbox, "fact", new JsonObject { ["fact_id"] = "fx" }).Status);
        Assert.Equal(404, Call(sandbox, "fact", new JsonObject { ["fact_id"] = "f999999" }).Status);
    }

    [Fact]
    public void Fact_AfterForget_IsNotLive()
    {
        using var sandbox = new SandboxHome();
        var handle = Remember(sandbox, "A fact about to be retracted by a test.");
        Call(sandbox, "forget", new JsonObject { ["session_id"] = Session, ["fact_id"] = handle });

        var (_, body) = Call(sandbox, "fact", new JsonObject { ["fact_id"] = handle });

        Assert.False((bool)body["live"]!);
        Assert.NotNull(body["valid_to"]);
    }

    [Fact]
    public void History_ListsEveryVersionOldestFirst_WithTheReasonEachWasClosed()
    {
        using var sandbox = new SandboxHome();
        var first = Remember(sandbox, "The retry limit is three.");
        var revised = EngramMcpTools.Revise(
            sandbox.Home, new McpSessionId(Session), new McpHomeState(true), first, "The retry limit is five.", "raised after an outage");
        var secondHandle = System.Text.RegularExpressions.Regex.Match(revised, @"\[(f\d+)\]").Groups[1].Value;

        var (status, body) = Call(sandbox, "history", new JsonObject { ["fact_id"] = secondHandle });

        Assert.Equal(200, status);
        var versions = body["versions"]!.AsArray();
        Assert.Equal(2, versions.Count);
        Assert.Equal(first, (string)versions[0]!["handle"]!);
        Assert.Equal("The retry limit is three.", (string)versions[0]!["body"]!);
        Assert.NotNull(versions[0]!["closed_reason"]);
        Assert.Equal(secondHandle, (string)versions[1]!["handle"]!);
        Assert.Null(versions[1]!["valid_to"]);
        Assert.Null(versions[1]!["closed_reason"]);
    }

    [Fact]
    public void History_ClosedReason_IsWhatTheMemoryBrowserReportsForEachClosedVersion()
    {
        using var sandbox = new SandboxHome();
        var first = Remember(sandbox, "The retry limit is three.");
        var revised = EngramMcpTools.Revise(
            sandbox.Home, new McpSessionId(Session), new McpHomeState(true), first, "The retry limit is five.", "raised after an outage");
        var second = System.Text.RegularExpressions.Regex.Match(revised, @"\[(f\d+)\]").Groups[1].Value;
        Call(sandbox, "forget", new JsonObject { ["session_id"] = Session, ["fact_id"] = second });

        Dictionary<long, string> expected;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            expected = MemoryBrowser.Reasons(connection, [long.Parse(first[1..]), long.Parse(second[1..])]);
        }

        var (_, body) = Call(sandbox, "history", new JsonObject { ["fact_id"] = second });
        var versions = body["versions"]!.AsArray();

        Assert.Equal(2, versions.Count);
        Assert.Equal(expected[long.Parse(first[1..])], (string)versions[0]!["closed_reason"]!);
        Assert.Equal(expected[long.Parse(second[1..])], (string)versions[1]!["closed_reason"]!);
        Assert.NotEqual((string)versions[0]!["closed_reason"]!, (string)versions[1]!["closed_reason"]!);
    }

    [Fact]
    public void PathFacts_NulInThePath_IsAJsonErrorAndNeverAThrow()
    {
        using var sandbox = new SandboxHome();

        var (status, body) = Call(sandbox, "path-facts", new JsonObject { ["path"] = "/a\u0000b" });

        Assert.True(status is 400 or 500, status.ToString());
        Assert.NotNull(body["error"]);
        Assert.DoesNotContain(" at ", body.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Remember_RepeatedStatement_WritesAModCallEachTimeBecauseEachSucceeded()
    {
        using var sandbox = new SandboxHome();

        Remember(sandbox, "Same words again.");
        Assert.Single(Telemetry(sandbox, TelemetryEventKind.ModCall));
        Remember(sandbox, "Same words again.");

        Assert.Equal(2, Telemetry(sandbox, TelemetryEventKind.ModCall).Count);
        Assert.Equal(2, Telemetry(sandbox, TelemetryEventKind.ModCall).Count(c => c.Tool == "remember"));
    }

    [Fact]
    public void History_RejectsMalformedAndAbsentHandles()
    {
        using var sandbox = new SandboxHome();

        Assert.Equal(400, Call(sandbox, "history", new JsonObject { ["fact_id"] = "fx" }).Status);
        Assert.Equal(404, Call(sandbox, "history", new JsonObject { ["fact_id"] = "f999999" }).Status);
    }

    [Fact]
    public void Forget_ClosesALiveFact_AndWritesOneModCall()
    {
        using var sandbox = new SandboxHome();
        var handle = Remember(sandbox, "Something the mod will retract.");
        var callsBefore = Telemetry(sandbox, TelemetryEventKind.ModCall).Count;

        var (status, body) = Call(sandbox, "forget", new JsonObject { ["session_id"] = Session, ["fact_id"] = handle });

        Assert.Equal(200, status);
        Assert.True((bool)body["retracted"]!);
        var calls = Telemetry(sandbox, TelemetryEventKind.ModCall);
        Assert.Equal(callsBefore + 1, calls.Count);
        Assert.Equal("forget", calls[^1].Tool);
        Assert.Empty(Telemetry(sandbox, TelemetryEventKind.Remember));
    }

    [Fact]
    public void Forget_OnAnAlreadyClosedFact_IsRetractedFalseAndWritesNoModCall()
    {
        using var sandbox = new SandboxHome();
        var handle = Remember(sandbox, "Something retracted twice.");
        Call(sandbox, "forget", new JsonObject { ["session_id"] = Session, ["fact_id"] = handle });
        var callsBefore = Telemetry(sandbox, TelemetryEventKind.ModCall).Count;

        var (status, body) = Call(sandbox, "forget", new JsonObject { ["session_id"] = Session, ["fact_id"] = handle });

        Assert.Equal(200, status);
        Assert.False((bool)body["retracted"]!);
        Assert.Equal(callsBefore, Telemetry(sandbox, TelemetryEventKind.ModCall).Count);
    }

    private static string? SessionThatRetracted(SandboxHome sandbox, string handle)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT s.external_id FROM supersession x LEFT JOIN session s ON s.id = x.session_id WHERE x.old_fact_id = $id;";
        command.Parameters.AddWithValue("$id", long.Parse(handle[1..]));
        return command.ExecuteScalar() as string;
    }

    [Fact]
    public void Forget_StampsTheRequestsClaudeSessionOnTheRetraction()
    {
        using var sandbox = new SandboxHome();
        var handle = Remember(sandbox, "Retracted by a different session than wrote it.");

        Call(sandbox, "forget", new JsonObject { ["session_id"] = "cc-session-2", ["fact_id"] = handle });

        Assert.Equal("cc-session-2", SessionThatRetracted(sandbox, handle));
    }

    [Fact]
    public void Forget_RecordsTheSameReasonAsTheMcpTool()
    {
        using var sandbox = new SandboxHome();
        var viaApi = Remember(sandbox, "Retracted through the mod API.");
        var viaMcp = Remember(sandbox, "Retracted through the MCP tool.");
        Call(sandbox, "forget", new JsonObject { ["session_id"] = Session, ["fact_id"] = viaApi });
        EngramMcpTools.Forget(sandbox.Home, new McpSessionId(Session), new McpHomeState(true), viaMcp);

        string ReasonOf(string handle)
        {
            using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT reason FROM supersession WHERE old_fact_id = $id;";
            command.Parameters.AddWithValue("$id", long.Parse(handle[1..]));
            return (string)command.ExecuteScalar()!;
        }

        Assert.Equal(ReasonOf(viaMcp), ReasonOf(viaApi));
    }

    [Fact]
    public void Remember_TwiceWithTheSameStatement_IsCreatedFalseWithOneRow()
    {
        using var sandbox = new SandboxHome();
        var before = LiveFactCount(sandbox);

        var first = Call(sandbox, "remember", new JsonObject { ["session_id"] = Session, ["statement"] = "Same words.", ["evidence"] = "e" });
        var second = Call(sandbox, "remember", new JsonObject { ["session_id"] = Session, ["statement"] = "Same words.", ["evidence"] = "e" });

        Assert.True((bool)first.Body["created"]!);
        Assert.False((bool)second.Body["created"]!);
        Assert.Equal((string)first.Body["handle"]!, (string)second.Body["handle"]!);
        Assert.Equal(before + 1, LiveFactCount(sandbox));
        Assert.Empty(Telemetry(sandbox, TelemetryEventKind.Remember));
    }

    [Fact]
    public void Remember_AnchorsTheFactToTheClaudeCodeSessionRow()
    {
        using var sandbox = new SandboxHome();
        var handle = Remember(sandbox, "A note filed by a mod.");

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT s.external_id, s.host FROM fact f JOIN session s ON s.id = f.session_id WHERE f.id = $id;";
        command.Parameters.AddWithValue("$id", long.Parse(handle[1..]));
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(Session, reader.GetString(0));
        Assert.Equal(SessionStore.ClaudeCodeHost, reader.GetString(1));
    }

    [Fact]
    public void Remember_DetailsOverTheCeiling_Is400WithTheMessageAndWritesNothing()
    {
        using var sandbox = new SandboxHome();
        var before = LiveFactCount(sandbox);

        var (status, body) = Call(sandbox, "remember", new JsonObject
        {
            ["session_id"] = Session,
            ["statement"] = "Has enormous details.",
            ["evidence"] = "e",
            ["details"] = new string('x', 8000),
        });

        Assert.Equal(400, status);
        Assert.Contains("2,000-token ceiling", (string)body["detail"]!, StringComparison.Ordinal);
        Assert.Equal(before, LiveFactCount(sandbox));
        Assert.Empty(Telemetry(sandbox, TelemetryEventKind.ModCall));
    }

    [Fact]
    public void Remember_DetailsJustUnderTheCeiling_IsAccepted()
    {
        using var sandbox = new SandboxHome();

        var (status, _) = Call(sandbox, "remember", new JsonObject
        {
            ["session_id"] = Session,
            ["statement"] = "Has large but allowed details.",
            ["evidence"] = "e",
            ["details"] = new string('x', 7000),
        });

        Assert.Equal(200, status);
    }

    [Theory]
    [InlineData(null, 400)]
    [InlineData("", 400)]
    [InlineData("   ", 400)]
    [InlineData("e", 200)]
    public void Remember_EvidenceIsRequired(string? evidence, int expected)
    {
        using var sandbox = new SandboxHome();
        var body = new JsonObject { ["session_id"] = Session, ["statement"] = "Needs evidence." };
        if (evidence is not null)
        {
            body["evidence"] = evidence;
        }

        Assert.Equal(expected, Call(sandbox, "remember", body).Status);
    }

    [Theory]
    [InlineData(300, 200)]
    [InlineData(301, 400)]
    public void Remember_EvidenceIsBoundedAt300Characters(int length, int expected)
    {
        using var sandbox = new SandboxHome();

        var (status, _) = Call(sandbox, "remember", new JsonObject
        {
            ["session_id"] = Session,
            ["statement"] = "Evidence length.",
            ["evidence"] = new string('e', length),
        });

        Assert.Equal(expected, status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Remember_EmptyStatement_Is400(string statement)
    {
        using var sandbox = new SandboxHome();

        var (status, _) = Call(sandbox, "remember", new JsonObject
        {
            ["session_id"] = Session,
            ["statement"] = statement,
            ["evidence"] = "e",
        });

        Assert.Equal(400, status);
    }

    [Fact]
    public void Captures_ReturnsOnlyThisSessionsLiveStatedUserFacts_FromSinceOnward()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;
        long mine, forgotten, other;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            mine = UserFacts.Capture(connection, UserFactTopic.AboutYou, "I prefer tabs over spaces.", "session-a", now)!.Value;
            forgotten = UserFacts.Capture(connection, UserFactTopic.AboutYou, "I use a standing desk.", "session-a", now.AddSeconds(1))!.Value;
            other = UserFacts.Capture(connection, UserFactTopic.AboutYou, "I live in Lisbon.", "session-b", now.AddSeconds(2))!.Value;
            FactStore.Forget(connection, forgotten, "test", now.AddSeconds(3));
        }

        Remember(sandbox, "A note the model took, which lives under sessions.", "session-a");
        var callsBefore = Telemetry(sandbox, TelemetryEventKind.ModCall).Count;

        var (status, body) = Call(sandbox, "captures", new JsonObject { ["session_id"] = "session-a", ["since"] = 0 });

        Assert.Equal(200, status);
        var captures = body["captures"]!.AsArray();
        var capture = Assert.Single(captures);
        Assert.Equal(FactCatalog.HandleFor(mine), (string)capture!["handle"]!);
        Assert.Equal("I prefer tabs over spaces.", (string)capture["body"]!);
        Assert.DoesNotContain(captures, c => (string)c!["handle"]! == FactCatalog.HandleFor(other));
        Assert.DoesNotContain(captures, c => (string)c!["handle"]! == FactCatalog.HandleFor(forgotten));
        Assert.Equal(callsBefore, Telemetry(sandbox, TelemetryEventKind.ModCall).Count);
    }

    [Fact]
    public void Captures_SinceIsInclusive_AndOneSecondLaterExcludes()
    {
        using var sandbox = new SandboxHome();
        var moment = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            UserFacts.Capture(connection, UserFactTopic.AboutYou, "I drink tea.", "session-a", moment);
        }

        var at = Call(sandbox, "captures", new JsonObject { ["session_id"] = "session-a", ["since"] = moment.ToUnixTimeSeconds() });
        var after = Call(sandbox, "captures", new JsonObject { ["session_id"] = "session-a", ["since"] = moment.ToUnixTimeSeconds() + 1 });

        Assert.Single(at.Body["captures"]!.AsArray());
        Assert.Empty(after.Body["captures"]!.AsArray());
    }

    [Fact]
    public void Captures_NegativeOrMissingSince_Is400()
    {
        using var sandbox = new SandboxHome();

        Assert.Equal(400, Call(sandbox, "captures", new JsonObject { ["session_id"] = Session, ["since"] = -1 }).Status);
        Assert.Equal(400, Call(sandbox, "captures", new JsonObject { ["session_id"] = Session }).Status);
    }

    [Fact]
    public void Captures_AreCappedAtTwenty()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            for (var i = 0; i < 21; i++)
            {
                UserFacts.Capture(connection, UserFactTopic.AboutYou, $"I own gadget number {i}.", "session-a", now.AddSeconds(i));
            }
        }

        var (_, body) = Call(sandbox, "captures", new JsonObject { ["session_id"] = "session-a", ["since"] = 0 });

        Assert.Equal(20, body["captures"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("src/x.cs")]
    [InlineData("")]
    [InlineData("/tmp/repo/../etc/passwd")]
    [InlineData("C:\\repo\\..\\x.cs")]
    public void PathFacts_RelativeOrDotDotPath_Is400(string path)
    {
        using var sandbox = new SandboxHome();

        var (status, _) = Call(sandbox, "path-facts", new JsonObject { ["path"] = path });

        Assert.Equal(400, status);
    }

    [Fact]
    public void PathFacts_FileInADirectoryNoRepoCovers_IsEmptyWithNullEntityPath()
    {
        using var sandbox = new SandboxHome();
        var directory = Path.Combine(sandbox.Home.Root, "never-indexed");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "NOTES.md");
        File.WriteAllText(file, "# Notes\n");

        var (status, body) = Call(sandbox, "path-facts", new JsonObject { ["path"] = file });

        Assert.Equal(200, status);
        Assert.Null(body["entity_path"]);
        Assert.Null(body["repo"]);
        Assert.Empty(body["facts"]!.AsArray());
    }

    [Fact]
    public void PathFacts_AnIndexedRepoThatWasNeverEnrolled_IsStillServed()
    {
        using var sandbox = new SandboxHome();
        var (_, file) = IndexedRepo(sandbox, enroll: false);

        var (status, body) = Call(sandbox, "path-facts", new JsonObject { ["path"] = file });

        Assert.Equal(200, status);
        Assert.NotNull(body["entity_path"]);
        Assert.NotEmpty(body["facts"]!.AsArray());
    }

    [Fact]
    public void PathFacts_IndexedFile_ReturnsLiveFactsUnderItsEntityAndNotItsNeighbours()
    {
        using var sandbox = new SandboxHome();
        var (repo, file) = IndexedRepo(sandbox, enroll: true);

        var (status, body) = Call(sandbox, "path-facts", new JsonObject { ["path"] = file });

        Assert.Equal(200, status);
        var entityPath = (string)body["entity_path"]!;
        Assert.EndsWith("/NOTES.md", entityPath, StringComparison.Ordinal);
        var facts = body["facts"]!.AsArray();
        Assert.NotEmpty(facts);
        Assert.All(facts, f =>
        {
            var subject = (string)f!["subject_path"]!;
            Assert.True(subject == entityPath || subject.StartsWith(entityPath + "#", StringComparison.Ordinal), subject);
        });
        Assert.Empty(Telemetry(sandbox, TelemetryEventKind.ModCall));
        Assert.True(File.Exists(Path.Combine(repo, "NOTES.md.bak")));
    }

    [Fact]
    public void PathFacts_PredicateFilter_IsExactMatch()
    {
        using var sandbox = new SandboxHome();
        var (_, file) = IndexedRepo(sandbox, enroll: true);

        var all = Call(sandbox, "path-facts", new JsonObject { ["path"] = file }).Body["facts"]!.AsArray();
        var predicate = (string)all[0]!["predicate"]!;

        var matching = Call(sandbox, "path-facts", new JsonObject { ["path"] = file, ["predicate"] = predicate }).Body["facts"]!.AsArray();
        var partial = Call(sandbox, "path-facts", new JsonObject { ["path"] = file, ["predicate"] = predicate[..^1] }).Body["facts"]!.AsArray();
        var none = Call(sandbox, "path-facts", new JsonObject { ["path"] = file, ["predicate"] = "invariant" }).Body["facts"]!.AsArray();

        Assert.NotEmpty(matching);
        Assert.All(matching, f => Assert.Equal(predicate, (string)f!["predicate"]!));
        Assert.Empty(partial);
        Assert.Empty(none);
    }

    [Fact]
    public void PathFacts_FileOutsideTheRepoRoot_IsEmpty()
    {
        using var sandbox = new SandboxHome();
        var (repo, _) = IndexedRepo(sandbox, enroll: true);

        var sibling = repo + "-sibling";
        Directory.CreateDirectory(sibling);

        var (status, body) = Call(sandbox, "path-facts", new JsonObject { ["path"] = Path.Combine(sibling, "NOTES.md") });

        Assert.Equal(200, status);
        Assert.Null(body["entity_path"]);
    }

    [Fact]
    public void QueryWithLineBreaksAndAFakeRecord_WritesExactlyOneParseableModCall()
    {
        using var sandbox = new SandboxHome();
        var query = "transaction\n{\"kind\":\"recall\",\"session_id\":\"x\"}\r\u2028tail";

        var (status, _) = Call(sandbox, "recall", new JsonObject { ["session_id"] = Session, ["query"] = query });

        Assert.Equal(200, status);
        var lines = File.ReadAllLines(Engram.Core.Telemetry.ResolvePath(sandbox.Home));
        var modCalls = lines.Select(Engram.Core.Telemetry.TryParse).OfType<TelemetryRecord>()
            .Where(r => r.Kind == TelemetryEventKind.ModCall).ToList();
        var call = Assert.Single(modCalls);
        Assert.Equal(query, call.Query);
        Assert.Empty(Telemetry(sandbox, TelemetryEventKind.Recall));
        Assert.Equal(lines.Length, lines.Count(l => Engram.Core.Telemetry.TryParse(l) is not null));
    }

    [Fact]
    public void ModCallKind_IsADeclaredConstantAndListedInAll()
    {
        Assert.Equal("mod-call", TelemetryEventKind.ModCall);
        Assert.Contains(TelemetryEventKind.ModCall, TelemetryEventKind.All);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("[::1]", true)]
    [InlineData("evil.example", false)]
    [InlineData("127.0.0.1.evil.example", false)]
    [InlineData("localhost.evil.example", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("", false)]
    public void LoopbackHostCheck_AdmitsOnlyThisMachinesNames(string host, bool admitted) =>
        Assert.Equal(admitted, ServeCommand.IsLoopbackHost(host));

    private static (string Repo, string File) IndexedRepo(SandboxHome sandbox, bool enroll)
    {
        var repo = PathCanonicalizer.Canonical(Path.Combine(sandbox.Home.Root, "modapi-repo-" + Guid.NewGuid().ToString("N")[..8]));
        Directory.CreateDirectory(repo);
        var file = Path.Combine(repo, "NOTES.md");
        File.WriteAllText(file, "# Deployment notes\n\nShip on Tuesdays.\n\n## Rollback\n\nRevert the tag.\n");
        File.WriteAllText(Path.Combine(repo, "NOTES.md.bak"), "# Old notes\n\nStale.\n");

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(repo, Apply: true, Drain: false, Full: false),
            DateTimeOffset.UtcNow);

        if (enroll)
        {
            RepoEnrollment.Enroll(connection, CodeIndexer.ResolveIdentity(repo), repo, DateTimeOffset.UtcNow);
        }

        return (repo, file);
    }

    private static JsonNode Tail(
        SandboxHome sandbox, JsonObject? body = null, TelemetryFeed? feed = null, string session = Session)
    {
        body ??= new JsonObject();
        body["session_id"] ??= session;
        var (status, json) = Call(sandbox, "tail", body, feed: feed);
        Assert.Equal(200, status);
        return json;
    }

    private static long Head(SandboxHome sandbox) => (long)Tail(sandbox)["head"]!;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static IEnumerable<JsonNode> Rows(JsonNode part) => part["rows"]!.AsArray().Select(r => r!);

    private static JsonNode WriteWithBody(JsonNode response, string body) =>
        Assert.Single(Rows(response["writes"]!), r => (string)r["body"]! == body);

    [Fact]
    public void Tail_ClassifiesEveryWriterOrigin_AndCountsButNeverShowsACodeFact()
    {
        using var sandbox = new SandboxHome();
        var before = Head(sandbox);
        var now = DateTimeOffset.UtcNow;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            DirectiveFacts.Add(connection, "Always run the tests before committing.", now);
            InvariantFacts.Add(connection, "/repo/Store.cs", "Never opens a second connection.", now);
            SessionFacts.Append(connection, Session, "The compaction summary kept this.", null, null, CompactionDigest.HarvesterAgent, now);
            var capture = UserFacts.Capture(connection, UserFactTopic.AboutYou, "I prefer tea.", Session, now)!.Value;
            UserFacts.Restate(connection, capture, "Jim prefers tea.", Session, now);
            FactStore.Remember(
                connection,
                new FactWrite("/knowledge/misc/thing", "note", "states", "An unclassified fact.", "project", "stated", SessionId: null),
                now);
            FactStore.Remember(
                connection,
                new FactWrite("/code/repo/File.cs", "file", "declares", "class Derived.", "code", "observed", Regenerable: true),
                now);
        }

        Remember(sandbox, "A plain session note.", Session);
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            FactStore.Remember(
                connection,
                new FactWrite("/code/repo/Other.cs", "file", "declares", "class Last.", "code", "observed", Regenerable: true),
                now);
        }

        var response = Tail(sandbox, new JsonObject { ["after"] = before, ["scope"] = "all", ["limit"] = 50 });
        string OriginOf(string body) => (string)WriteWithBody(response, body)["origin"]!;

        Assert.Equal("directive", OriginOf("Always run the tests before committing."));
        Assert.Equal("invariant", OriginOf("Never opens a second connection."));
        Assert.Equal("compaction", OriginOf("The compaction summary kept this."));
        Assert.Equal("revision", OriginOf("Jim prefers tea."));
        Assert.Equal("other", OriginOf("An unclassified fact."));
        Assert.Equal("note", OriginOf("A plain session note."));
        Assert.DoesNotContain(Rows(response["writes"]!), r => ((string)r["body"]!).StartsWith("class ", StringComparison.Ordinal));
        Assert.Equal(0L, (long)response["writes"]!["skipped"]!);
        using var check = EngramDatabase.OpenInitialized(sandbox.Home);
        using var maxId = check.CreateCommand();
        maxId.CommandText = "SELECT MAX(id) FROM fact;";
        Assert.Equal((long)maxId.ExecuteScalar()!, (long)response["head"]!);
    }

    [Fact]
    public void Tail_AForgottenCaptureRestatedVerbatim_ReadsCapture_AndARestateReadsRevisionWithReplaces()
    {
        using var sandbox = new SandboxHome();
        var before = Head(sandbox);
        var now = DateTimeOffset.UtcNow;
        long original;
        long revised;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            original = UserFacts.Capture(connection, UserFactTopic.AboutYou, "I drink coffee.", Session, now)!.Value;
            FactStore.Forget(connection, original, "wrong", now);
            UserFacts.Capture(connection, UserFactTopic.AboutYou, "I drink coffee.", Session, now);
            var other = UserFacts.Capture(connection, UserFactTopic.AboutYou, "I drink water.", Session, now)!.Value;
            revised = UserFacts.Restate(connection, other, "Jim drinks water.", Session, now)!.Value;
        }

        var response = Tail(sandbox, new JsonObject { ["after"] = before });
        var rows = Rows(response["writes"]!).ToList();

        var coffee = rows.Where(r => (string)r["body"]! == "I drink coffee.").ToList();
        Assert.Equal(2, coffee.Count);
        Assert.All(coffee, r => Assert.Equal("capture", (string)r["origin"]!));
        Assert.All(coffee, r => Assert.Null(r["replaces"]));
        Assert.Single(coffee, r => (bool)r["live"]!);
        var revision = Assert.Single(rows, r => (string)r["body"]! == "Jim drinks water.");
        Assert.Equal("revision", (string)revision["origin"]!);
        Assert.Equal(FactCatalog.HandleFor(revised - 1), (string)revision["replaces"]!);
        Assert.True((bool)revision["live"]!);
        var superseded = Assert.Single(rows, r => (string)r["body"]! == "I drink water.");
        Assert.False((bool)superseded["live"]!);
    }

    [Theory]
    [InlineData("/directives/a-1234", "directs", "stated", false, "directive")]
    [InlineData("/directivesx/a", "directs", "stated", false, "other")]
    [InlineData("/knowledge/x", "invariant", "stated", false, "invariant")]
    [InlineData("/sessions/1/compaction-digest/abc", "notes", "inferred", false, "compaction")]
    [InlineData("/sessions/1/compaction-digest", "notes", "inferred", false, "note")]
    [InlineData("/sessions/1/another-agent/abc", "notes", "inferred", false, "note")]
    [InlineData("/user/about/x", "states", "stated", true, "revision")]
    [InlineData("/user/about/x", "states", "stated", false, "capture")]
    [InlineData("/user/about/x", "states", "inferred", false, "other")]
    [InlineData("/userx/about/x", "states", "stated", false, "other")]
    [InlineData("/sessions/1/abc", "notes", "inferred", false, "note")]
    public void OriginOf_FirstMatchWins_AndNearbyPathsDoNotMatch(
        string path, string predicate, string learnedVia, bool replaces, string expected) =>
        Assert.Equal(expected, ModApi.OriginOf(path, predicate, learnedVia, replaces));

    [Fact]
    public void Tail_FirstReadReturnsNoRowsInAnyList_OnlyTheCursors()
    {
        using var sandbox = new SandboxHome();
        Remember(sandbox, "Already there.", Session);
        var feed = new TelemetryFeed(sandbox.Home);

        var response = Tail(sandbox, feed: feed);

        Assert.Empty(Rows(response["writes"]!));
        Assert.Null(response["writes"]!["skipped"]);
        Assert.Empty(Rows(response["retractions"]!));
        Assert.True((long)response["head"]! > 0);
        Assert.True((long)response["now"]! > 0);
        Assert.False(string.IsNullOrEmpty((string)response["events"]!["epoch"]!));
        Assert.Equal(0L, (long)response["events"]!["head"]!);
        Assert.Empty(Rows(response["events"]!));
        Assert.Null(response["events"]!["skipped"]);
    }

    [Fact]
    public void Tail_AFirstReadOnAnEmptyStore_HasHeadZero()
    {
        using var sandbox = new SandboxHome();

        Assert.True((long)Tail(sandbox)["head"]! >= 0);
    }

    [Fact]
    public void Tail_AfterABurst_ReturnsTheNewestRowsFirst_AndCountsTheRestExactly()
    {
        using var sandbox = new SandboxHome();
        var before = Head(sandbox);
        for (var i = 0; i < 60; i++)
        {
            Remember(sandbox, $"Burst note number {i} about subject {i}.", Session);
        }

        var response = Tail(sandbox, new JsonObject { ["after"] = before, ["limit"] = 50 });
        var ids = Rows(response["writes"]!).Select(r => (long)r["id"]!).ToList();

        Assert.Equal(50, ids.Count);
        Assert.Equal(ids.OrderByDescending(id => id), ids);
        Assert.Equal((long)response["head"]!, ids[0]);
        Assert.Equal(10L, (long)response["writes"]!["skipped"]!);
    }

    [Fact]
    public void Tail_ScopeDefaultsToSession_AndAllAddsOtherSessionsRowsMarkedNotThisSession()
    {
        using var sandbox = new SandboxHome();
        var before = Head(sandbox);
        Remember(sandbox, "Mine to see.", Session);
        Remember(sandbox, "Someone else's note.", "cc-session-2");

        var defaulted = Tail(sandbox, new JsonObject { ["after"] = before });
        var explicitSession = Tail(sandbox, new JsonObject { ["after"] = before, ["scope"] = "session" });
        var all = Tail(sandbox, new JsonObject { ["after"] = before, ["scope"] = "all" });

        Assert.Equal(["Mine to see."], Rows(defaulted["writes"]!).Select(r => (string)r["body"]!));
        Assert.Equal(defaulted["writes"]!.ToJsonString(), explicitSession["writes"]!.ToJsonString());
        Assert.True((bool)WriteWithBody(all, "Mine to see.")["this_session"]!);
        Assert.False((bool)WriteWithBody(all, "Someone else's note.")["this_session"]!);
    }

    [Fact]
    public void Tail_AModForgetIsARetractionUnderSession_ButANullSessionForgetOnlyUnderAll()
    {
        using var sandbox = new SandboxHome();
        var mine = Remember(sandbox, "Retracted by this session.", Session);
        var orphan = Remember(sandbox, "Retracted by the command line.", Session);
        var since = Now();
        Call(sandbox, "forget", new JsonObject { ["session_id"] = Session, ["fact_id"] = mine });
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            FactStore.Forget(connection, long.Parse(orphan[1..]), "cli", DateTimeOffset.UtcNow);
        }

        var session = Tail(sandbox, new JsonObject { ["closed_after"] = since });
        var all = Tail(sandbox, new JsonObject { ["closed_after"] = since, ["scope"] = "all" });

        var onlyMine = Assert.Single(Rows(session["retractions"]!));
        Assert.Equal(mine, (string)onlyMine["handle"]!);
        Assert.True((bool)onlyMine["this_session"]!);
        Assert.Equal("Retracted by this session.", (string)onlyMine["body"]!);
        Assert.Equal("retracted by the user", (string)onlyMine["reason"]!);
        Assert.Equal("note", (string)onlyMine["origin"]!);
        Assert.Equal(2, Rows(all["retractions"]!).Count());
        Assert.False((bool)Assert.Single(Rows(all["retractions"]!), r => (string)r["handle"]! == orphan)["this_session"]!);
    }

    [Fact]
    public void Tail_ACodeFactForgetAndASupersedeAreNotRetractions()
    {
        using var sandbox = new SandboxHome();
        var since = Now();
        var now = DateTimeOffset.UtcNow;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            var code = FactStore.Remember(
                connection,
                new FactWrite("/code/repo/File.cs", "file", "declares", "class Gone.", "code", "observed", Regenerable: true),
                now).FactId;
            FactStore.Forget(connection, code, "file removed", now);
            var capture = UserFacts.Capture(connection, UserFactTopic.AboutYou, "I like rain.", Session, now)!.Value;
            UserFacts.Restate(connection, capture, "Jim likes rain.", Session, now);
        }

        var all = Tail(sandbox, new JsonObject { ["closed_after"] = since, ["scope"] = "all" });

        Assert.Empty(Rows(all["retractions"]!));
    }

    [Fact]
    public void Tail_ALateCommittingRetractionInsideTheSlackIsReturned_AndOneJustOutsideIsNot()
    {
        using var sandbox = new SandboxHome();
        var late = Remember(sandbox, "Stamped before the cursor, committed after.", Session);
        var tooOld = Remember(sandbox, "Stamped well before the cursor.", Session);
        var cursor = Now();
        Assert.Empty(Rows(Tail(sandbox, new JsonObject { ["closed_after"] = cursor, ["scope"] = "all" })["retractions"]!));

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            FactStore.Forget(connection, long.Parse(late[1..]), "r", DateTimeOffset.FromUnixTimeSeconds(cursor - 7));
            FactStore.Forget(connection, long.Parse(tooOld[1..]), "r", DateTimeOffset.FromUnixTimeSeconds(cursor - 11));
        }

        var second = Tail(sandbox, new JsonObject { ["closed_after"] = cursor, ["scope"] = "all" });

        Assert.Equal([late], Rows(second["retractions"]!).Select(r => (string)r["handle"]!));
    }

    private static string PlanOf(SandboxHome sandbox, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var lines = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            lines.Add(reader.GetString(reader.GetOrdinal("detail")));
        }

        return string.Join(" | ", lines);
    }

    [Fact]
    public void Tail_QueryPlans_SeekAndNeverScanFactOrSupersession()
    {
        using var sandbox = new SandboxHome();
        (string, object)[] write = [("$after", 0L), ("$head", 10L), ("$limit", 5), ("$session", "s")];
        (string, object)[] retraction = [("$since", 0L), ("$limit", 5), ("$session", "s")];

        var writes = PlanOf(sandbox, ModApi.TailWritesSql + ModApi.TailSessionFilter + ModApi.TailWritesOrder, write);
        var writesAll = PlanOf(sandbox, ModApi.TailWritesSql + ModApi.TailWritesOrder, write[..3]);
        (string, object)[] counted = [("$after", 0L), ("$head", 10L), ("$session", "s")];
        var count = PlanOf(sandbox, ModApi.TailWritesCountSql + ModApi.TailSessionFilter + ";", counted);
        var retractions = PlanOf(sandbox, ModApi.TailRetractionsSql + ModApi.TailSessionFilter + ModApi.TailRetractionsOrder, retraction);
        var head = PlanOf(sandbox, ModApi.TailHeadSql);

        Assert.Contains("SEARCH f USING INTEGER PRIMARY KEY", writes);
        Assert.Contains("SEARCH f USING INTEGER PRIMARY KEY", writesAll);
        Assert.Contains("SEARCH f USING INTEGER PRIMARY KEY", count);
        Assert.Contains("INDEX ix_supersession_new", writes);
        Assert.Contains("USING INDEX ix_supersession_retracted", retractions);
        Assert.Contains("INDEX ix_supersession_new", retractions);
        Assert.DoesNotContain("SCAN", head, StringComparison.Ordinal);
        foreach (var plan in new[] { writes, writesAll, count, retractions, head })
        {
            Assert.DoesNotContain("SCAN f", plan, StringComparison.Ordinal);
            Assert.DoesNotContain("SCAN x", plan, StringComparison.Ordinal);
            Assert.DoesNotContain("SCAN y", plan, StringComparison.Ordinal);
            Assert.DoesNotContain("SCAN supersession", plan, StringComparison.Ordinal);
            Assert.DoesNotContain("SCAN fact", plan, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Tail_WritesNoTelemetry_OnSuccessFailureOrFirstRead()
    {
        using var sandbox = new SandboxHome();
        Remember(sandbox, "Seeds the telemetry file.", Session);
        var path = Engram.Core.Telemetry.ResolvePath(sandbox.Home);
        var size = new FileInfo(path).Length;

        Tail(sandbox);
        Tail(sandbox, new JsonObject { ["after"] = 0, ["closed_after"] = 0 });
        var (status, _) = Call(sandbox, "tail", new JsonObject { ["session_id"] = Session, ["limit"] = 999 });

        Assert.Equal(400, status);
        Assert.Equal(size, new FileInfo(path).Length);
    }

    [Theory]
    [InlineData("after", -1)]
    [InlineData("closed_after", -1)]
    [InlineData("event_after", -1)]
    [InlineData("limit", 0)]
    [InlineData("limit", 51)]
    [InlineData("scope", "everything")]
    [InlineData("scope", "")]
    public void Tail_RejectsEachInvalidField(string field, object value)
    {
        using var sandbox = new SandboxHome();
        var body = new JsonObject { ["session_id"] = Session };
        body[field] = value is int number ? JsonValue.Create(number) : JsonValue.Create((string)value);

        Assert.Equal(400, Call(sandbox, "tail", body).Status);
    }

    [Fact]
    public void Tail_RejectsAnOverlongEpoch_AndAcceptsOneAtTheLimit()
    {
        using var sandbox = new SandboxHome();

        Assert.Equal(400, Call(sandbox, "tail", new JsonObject { ["session_id"] = Session, ["event_epoch"] = new string('e', 65) }).Status);
        Assert.Equal(200, Call(sandbox, "tail", new JsonObject { ["session_id"] = Session, ["event_epoch"] = new string('e', 64) }).Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public void Tail_AcceptsTheLimitBounds(int limit)
    {
        using var sandbox = new SandboxHome();

        Assert.Equal(200, Call(sandbox, "tail", new JsonObject { ["session_id"] = Session, ["limit"] = limit }).Status);
    }

    [Fact]
    public void Tail_WithAnAfterAheadOfTheHead_ReturnsNoRowsAndAHeadBehindIt()
    {
        using var sandbox = new SandboxHome();
        Remember(sandbox, "Present.", Session);
        var head = Head(sandbox);

        var response = Tail(sandbox, new JsonObject { ["after"] = head + 100 });

        Assert.Equal(head, (long)response["head"]!);
        Assert.Empty(Rows(response["writes"]!));
        Assert.Equal(0L, (long)response["writes"]!["skipped"]!);
    }

    private static TelemetryRecord Event(string session, string kind, string? query = null) =>
        new(DateTimeOffset.UtcNow.ToString("O"), session, kind, query);

    [Fact]
    public void Tail_Events_ReadFromTheCursorOnlyWithinTheSameEpoch()
    {
        using var sandbox = new SandboxHome();
        var feed = new TelemetryFeed(sandbox.Home);
        var epoch = (string)Tail(sandbox, feed: feed)["events"]!["epoch"]!;
        for (var i = 1; i <= 3; i++)
        {
            Engram.Core.Telemetry.Append(sandbox.Home, Event(Session, TelemetryEventKind.Remember, $"q{i}"));
        }

        feed.Poll();

        var fromOne = Tail(sandbox, new JsonObject { ["event_epoch"] = epoch, ["event_after"] = 1 }, feed)["events"]!;
        var otherEpoch = Tail(sandbox, new JsonObject { ["event_epoch"] = "not-the-epoch", ["event_after"] = 0 }, feed)["events"]!;
        var noEpoch = Tail(sandbox, new JsonObject { ["event_after"] = 0 }, feed)["events"]!;

        Assert.Equal([3L, 2L], Rows(fromOne).Select(r => (long)r["seq"]!));
        Assert.Equal("q3", (string)Rows(fromOne).First()["record"]!["query"]!);
        Assert.Equal(3L, (long)fromOne["head"]!);
        Assert.Equal(0L, (long)fromOne["skipped"]!);
        Assert.Empty(Rows(otherEpoch));
        Assert.Null(otherEpoch["skipped"]);
        Assert.Empty(Rows(noEpoch));
    }

    [Fact]
    public void Tail_Events_PastTheRingCountTheLostAndTheUnreturnedExactly()
    {
        using var sandbox = new SandboxHome();
        var feed = new TelemetryFeed(sandbox.Home);
        var epoch = (string)Tail(sandbox, feed: feed)["events"]!["epoch"]!;
        const int Written = TelemetryFeed.RingCapacity + 76;
        for (var i = 0; i < Written; i++)
        {
            Engram.Core.Telemetry.Append(sandbox.Home, Event(Session, TelemetryEventKind.Remember, $"q{i}"));
        }

        while (feed.Poll().Count > 0)
        {
        }

        var events = Tail(sandbox, new JsonObject { ["event_epoch"] = epoch, ["event_after"] = 0, ["limit"] = 50 }, feed)["events"]!;

        Assert.Equal(Written, (long)events["head"]!);
        Assert.Equal(50, Rows(events).Count());
        Assert.Equal(76L + (TelemetryFeed.RingCapacity - 50), (long)events["skipped"]!);
    }

    [Fact]
    public void Tail_Events_SessionScopeFiltersOnTheRecordsSessionId_AllDoesNot()
    {
        using var sandbox = new SandboxHome();
        var feed = new TelemetryFeed(sandbox.Home);
        var epoch = (string)Tail(sandbox, feed: feed)["events"]!["epoch"]!;
        Engram.Core.Telemetry.Append(sandbox.Home, Event(Session, TelemetryEventKind.ModCall, "mine"));
        Engram.Core.Telemetry.Append(sandbox.Home, Event("another-session", TelemetryEventKind.ModCall, "theirs"));
        Engram.Core.Telemetry.Append(sandbox.Home, Event(Session + "x", TelemetryEventKind.ModCall, "prefix-neighbour"));
        feed.Poll();

        var cursor = new JsonObject { ["event_epoch"] = epoch, ["event_after"] = 0 };
        var scoped = Tail(sandbox, (JsonObject)cursor.DeepClone(), feed)["events"]!;
        var all = Tail(sandbox, new JsonObject { ["event_epoch"] = epoch, ["event_after"] = 0, ["scope"] = "all" }, feed)["events"]!;

        Assert.Equal(["mine"], Rows(scoped).Select(r => (string)r["record"]!["query"]!));
        Assert.Equal(3, Rows(all).Count());
    }

    [Fact]
    public void Tail_WithNoFeed_ReportsEventsNull()
    {
        using var sandbox = new SandboxHome();

        Assert.Null(Tail(sandbox)["events"]);
    }
}
