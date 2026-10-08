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
        SandboxHome sandbox, string op, JsonObject body, string? headerMod = null)
    {
        body["mod"] ??= Mod;
        var result = ModApi.Execute(
            sandbox.Home, new LocalRuntime(sandbox.Home), op, headerMod ?? Mod, Encoding.UTF8.GetBytes(body.ToJsonString()));
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
}
