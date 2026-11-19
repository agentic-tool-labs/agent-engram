using System.Text;
using System.Text.Json.Nodes;
using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2. Recall's <c>types</c> filter (D85): one registry, one predicate, applied at <c>lex</c> and
/// <c>scored</c>, with the default leaving out only <c>tool</c>.
/// </summary>
public class RecallTypesTests
{
    private const string Query = "quokka";
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly VectorLaneQuery Off = VectorLaneQuery.Stopped(VectorLaneState.Off, "off");

    private sealed record Ids(long Tool, long User, long Directive, long Code, long Project, long Legacy, long Current, long Prior, long? CurrentSession);

    private static Ids Seed(SqliteConnection connection, bool withTool = true)
    {
        long Add(string path, string body, string scope) =>
            FactStore.Remember(connection, new FactWrite(path, "note", "states", body, scope, "stated"), T0).FactId;

        var tool = withTool ? Add("/tools/ah2/rec1", "quokka ledger record", MemoryScopes.Tool) : 0;
        var user = Add("/user/about-you/quokka", "quokka user preference", MemoryScopes.User);
        var directive = DirectiveFacts.Add(connection, "quokka directive must always hold", T0);
        var code = Add("/projects/p/code/r/Quokka.cs", "quokka code symbol", MemoryScopes.Code);
        var project = Add("/knowledge/quokka", "quokka project knowledge", MemoryScopes.Project);
        var legacy = Add("/misc/quokka", "quokka legacy scope fact", "legacy");
        var prior = SessionFacts.Append(connection, "prior", "quokka prior note", null, null, null, T0);
        var current = SessionFacts.Append(connection, "current", "quokka current note", null, null, null, T0);
        return new Ids(tool, user, directive, code, project, legacy, current, prior, SessionStore.FindSession(connection, "current"));
    }

    private static RankOutcome Rank(SqliteConnection connection, long? session, IReadOnlyList<string>? types, VectorLaneQuery? vector = null) =>
        RecallRanker.Rank(connection, Query, RetrievalSettings.DefaultBudgetTokens, RetrievalSettings.DefaultSeedK,
            session, T0.AddSeconds(5), vector ?? Off, types: types);

    private static IReadOnlyList<string> Normalized(params string[] types)
    {
        Assert.True(MemoryScopes.TryNormalize(types, out var normalized, out var error), error);
        return normalized!;
    }

    private static HashSet<long> FactIds(RankOutcome outcome) => outcome.Candidates.Select(c => c.FactId!.Value).ToHashSet();

    [Fact]
    public void Default_NeverReturnsToolRows_AndDoesNotCountThem()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection);

        var outcome = Rank(connection, ids.CurrentSession, null);
        var onlyTool = Rank(connection, ids.CurrentSession, Normalized(MemoryScopes.Tool));

        Assert.Contains(ids.Tool, FactIds(onlyTool)); // it matches the query
        Assert.DoesNotContain(ids.Tool, FactIds(outcome));
        Assert.Equal(outcome.Candidates.Count, outcome.MatchedTotal);
    }

    [Fact]
    public void Tool_ReturnsOnlyLedgerRows_WithCoverageOverThem()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection);

        var outcome = Rank(connection, ids.CurrentSession, Normalized(MemoryScopes.Tool));

        Assert.Equal([ids.Tool], FactIds(outcome));
        Assert.Equal(1, outcome.MatchedTotal);
    }

    [Fact]
    public void Session_ReturnsCurrentAndPriorNotes_CurrentFirst()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection);

        var outcome = Rank(connection, ids.CurrentSession, Normalized(MemoryScopes.Session));

        Assert.Equal([ids.Current, ids.Prior], outcome.Candidates.Select(c => c.FactId!.Value));
        Assert.Equal(FactOrigin.CurrentSession, outcome.Candidates[0].Origin);
        Assert.Equal(FactOrigin.PriorSession, outcome.Candidates[1].Origin);
    }

    [Fact]
    public void User_ReturnsUserFacts_AndStillExcludesDirectives()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection);

        var outcome = Rank(connection, ids.CurrentSession, Normalized(MemoryScopes.User));

        Assert.Equal([ids.User], FactIds(outcome));
        Assert.DoesNotContain(ids.Directive, FactIds(outcome));
    }

    [Fact]
    public void User_TheLexLaneIsFilteredBeforeItsLimit_SoAUserFactOutrankedByCodeStillGetsALexicalRank()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection);
        for (var i = 0; i < RetrievalSettings.DefaultSeedK + 8; i++)
        {
            FactStore.Remember(connection,
                new FactWrite($"/projects/p/code/r/Q{i}.cs", "note", "states", "quokka quokka quokka", MemoryScopes.Code, "stated"), T0);
        }

        Assert.Null(Rank(connection, ids.CurrentSession, null).Candidates.Single(c => c.FactId == ids.User).LexicalRank);
        Assert.Equal(1, Rank(connection, ids.CurrentSession, Normalized(MemoryScopes.User)).Candidates.Single(c => c.FactId == ids.User).LexicalRank);
    }

    [Fact]
    public void Agent_WithNoAgentRows_IsCoverageNoneWithAGapsLine()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection);

        var text = RecallRanker.Pack(connection, Query, 500, RetrievalSettings.DefaultSeedK, ids.CurrentSession, T0, Off,
            types: Normalized(MemoryScopes.Agent)).Text;

        Assert.Contains("coverage: none", text, StringComparison.Ordinal);
        Assert.Contains("\ngaps: ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AScopeOutsideTheRegistry_IsReturnedByNoFilter_ButStillByDefault()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection);

        Assert.Contains(ids.Legacy, FactIds(Rank(connection, ids.CurrentSession, null)));
        foreach (var type in MemoryScopes.All)
        {
            Assert.DoesNotContain(ids.Legacy, FactIds(Rank(connection, ids.CurrentSession, Normalized(type))));
        }
    }

    [Fact]
    public void Code_WithTheVectorLaneUnavailable_KeepsTheNote_AndTheFilter()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection);
        var down = VectorLaneQuery.Stopped(VectorLaneState.Unavailable, "endpoint refused");

        var detail = RecallRanker.PackWithOutcome(connection, Query, 500, RetrievalSettings.DefaultSeedK, ids.CurrentSession, T0, down,
            types: Normalized(MemoryScopes.Code));

        Assert.Contains("vector lane did not run (endpoint refused)", detail.Result.Text, StringComparison.Ordinal);
        Assert.Equal([ids.Code], detail.Packed.Select(c => c.FactId!.Value));
    }

    [Fact]
    public void Header_NamesTheFilterInRegistryOrder_AndOnlyWhenOneWasPassed()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection);

        string Header(IReadOnlyList<string>? types) =>
            RecallRanker.Pack(connection, Query, 500, RetrievalSettings.DefaultSeedK, ids.CurrentSession, T0, Off, types: types)
                .Text.Split('\n')[0];

        var filtered = Header(Normalized(MemoryScopes.Code, MemoryScopes.User, MemoryScopes.User));
        Assert.Matches(@"coverage: [a-z]+ · types: user,code$", filtered);
        Assert.DoesNotContain("types:", Header(null), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bogus")]
    [InlineData("user,bogus")]
    public void Validation_RefusesEmptyAndUnknown_NamingTheValidTypes(string list)
    {
        var requested = list.Split(',', StringSplitOptions.RemoveEmptyEntries);
        Assert.False(MemoryScopes.TryNormalize(requested, out var types, out var error));
        Assert.Null(types);
        Assert.Contains("valid types are session, user, project, agent, code, tool", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_DedupesKnownNames_AndAcceptsNoFilter()
    {
        Assert.True(MemoryScopes.TryNormalize(["code", "user", "code"], out var types, out _));
        Assert.Equal(["user", "code"], types);
        Assert.True(MemoryScopes.TryNormalize(null, out var none, out _));
        Assert.Null(none);
    }

    private static List<JsonObject> RecallRecords(SandboxHome sandbox, string kind)
    {
        var path = Path.Combine(sandbox.Home.Root, "telemetry.jsonl");
        return !File.Exists(path)
            ? []
            : File.ReadAllLines(path).Select(line => JsonNode.Parse(line)!.AsObject())
                .Where(record => (string?)record["kind"] == kind).ToList();
    }

    [Fact]
    public void Mcp_AnUnknownType_ReturnsTheError_RunsNothing_AndRecordsNothing()
    {
        using var sandbox = new SandboxHome();
        var answer = EngramMcpTools.Recall(sandbox.Home, new McpSessionId("s"), new McpHomeState(true), new LocalRuntime(sandbox.Home),
            new SessionPinStore(), Query, types: ["bogus"]);

        Assert.StartsWith("unknown types: bogus; valid types are", answer, StringComparison.Ordinal);
        Assert.Empty(RecallRecords(sandbox, TelemetryEventKind.Recall));
    }

    [Fact]
    public void Mcp_Telemetry_CarriesTypesOnlyWhenPassed_AndPackedByScopeAlways()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Seed(connection);
        }

        string Recall(string[]? types) => EngramMcpTools.Recall(sandbox.Home, new McpSessionId("current"), new McpHomeState(true),
            new LocalRuntime(sandbox.Home), new SessionPinStore(), Query, types: types);
        Recall(null);
        Recall(["code", "session"]);

        var records = RecallRecords(sandbox, TelemetryEventKind.Recall);
        Assert.Equal(2, records.Count);
        Assert.Null(records[0]["types"]); // the log writes an unset field as null
        Assert.Equal("""["session","code"]""", records[1]["types"]!.ToJsonString());

        var unfiltered = records[0]["packed_by_scope"]!.AsObject();
        Assert.Equal(1, (int)unfiltered["current-session"]!);
        Assert.Equal(1, (int)unfiltered["prior-session"]!);
        Assert.Equal(1, (int)unfiltered["code"]!);
        Assert.False(unfiltered.ContainsKey("tool"));
        Assert.Equal(["code", "current-session", "prior-session"], records[1]["packed_by_scope"]!.AsObject().Select(p => p.Key).Order());
    }

    private static (int Status, JsonNode Body) ModRecall(SandboxHome sandbox, JsonObject body)
    {
        body["mod"] = "lens";
        body["session_id"] = "current";
        body["query"] = Query;
        var result = ModApi.Execute(sandbox.Home, new LocalRuntime(sandbox.Home), "recall", "lens", Encoding.UTF8.GetBytes(body.ToJsonString()));
        return (result.Status, JsonNode.Parse(result.Json)!);
    }

    [Fact]
    public void Mod_WithoutTypes_IsUnchanged_AndItsRecordGetsNeitherField()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Seed(connection);
        }

        var (status, body) = ModRecall(sandbox, []);
        var mcp = EngramMcpTools.Recall(sandbox.Home, new McpSessionId("current"), new McpHomeState(false),
            new LocalRuntime(sandbox.Home), new SessionPinStore(), Query);

        Assert.Equal(200, status);
        Assert.Equal(mcp, (string)body["text"]!);
        var record = Assert.Single(RecallRecords(sandbox, TelemetryEventKind.ModCall));
        Assert.Null(record["types"]);
        Assert.Null(record["packed_by_scope"]);
    }

    [Fact]
    public void Mod_WithTypes_FiltersAndAnUnknownOneIs400()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Seed(connection);
        }

        var (status, body) = ModRecall(sandbox, new JsonObject { ["types"] = new JsonArray("user") });
        Assert.Equal(200, status);
        Assert.Contains("types: user", (string)body["text"]!, StringComparison.Ordinal);
        Assert.Single(body["facts"]!.AsArray());

        var (bad, _) = ModRecall(sandbox, new JsonObject { ["types"] = new JsonArray("bogus") });
        Assert.Equal(400, bad);
    }

    [Fact]
    public void Explain_WithTypesUser_AgreesWithRecall_AndItsLexicalRowIsTheFilteredLane()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection);
        var user = Normalized(MemoryScopes.User);

        var explanation = RetrievalExplainer.Explain(connection, sandbox.Home, Query, 500, 20, "current", T0.AddSeconds(5), _ => null, types: user);
        var recall = Rank(connection, ids.CurrentSession, user);

        Assert.Equal(recall.Candidates.Select(c => c.Handle), explanation.Candidates.Select(c => c.Candidate.Handle));
        var lexical = FactStore.SearchRanked(connection, Query, RetrievalSettings.DefaultSeedK, user);
        Assert.NotEmpty(lexical);
        Assert.All(lexical, hit => Assert.Equal(MemoryScopes.User, ScopeOf(connection, hit.FactId)));
        // The directive (scope user) is in the lexical lane and, as always, never a candidate.
        Assert.Contains(lexical, hit => hit.FactId == ids.Directive);
        var ranked = explanation.Candidates.Where(c => c.Candidate.LexicalRank is not null).ToList();
        Assert.NotEmpty(ranked);
        Assert.All(ranked, c => Assert.Equal(c.Candidate.LexicalRank, c.Lexical?.Rank));
    }

    private static string ScopeOf(SqliteConnection connection, long factId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT scope FROM fact WHERE id = $id;";
        command.Parameters.AddWithValue("$id", factId);
        return (string)command.ExecuteScalar()!;
    }

    [Fact]
    public void ExplainCommand_AnUnknownType_IsABadArgument()
    {
        using var sandbox = new SandboxHome();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = ExplainCommand.Run(sandbox.Home.Root, [Query, "--types", "user,bogus"], stdout, stderr);

        Assert.Equal(1, code);
        Assert.Contains("unknown types: bogus", stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("user,")]
    public void ExplainCommand_AnEmptyTypeName_IsNamedAsSuch(string types)
    {
        using var sandbox = new SandboxHome();
        var stderr = new StringWriter();

        var code = ExplainCommand.Run(sandbox.Home.Root, [Query, "--types", types], new StringWriter(), stderr);

        Assert.Equal(1, code);
        Assert.Contains("error: empty type name; valid types are", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Explain_WithEveryNonToolType_AgreesWithRecallUnderTheSameFilter()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection);
        var allButTool = MemoryScopes.All.Where(type => !MemoryScopes.ExcludedByDefault.Contains(type)).ToList();

        var explanation = RetrievalExplainer.Explain(connection, sandbox.Home, Query, 500, 20, "current", T0.AddSeconds(5), _ => null, types: allButTool);
        var recall = Rank(connection, ids.CurrentSession, allButTool);

        Assert.NotEmpty(recall.Candidates);
        Assert.Equal(recall.Candidates.Select(c => c.Handle), explanation.Candidates.Select(c => c.Candidate.Handle));
        Assert.All(explanation.Candidates.Where(c => c.Candidate.LexicalRank is not null),
            c => Assert.Equal(c.Candidate.LexicalRank, c.Lexical?.Rank));
    }

    /// <summary>
    /// Byte-identity of the default: the only difference from the pre-D85 statement is the
    /// <c>scope &lt;&gt; 'tool'</c> term in <c>scored</c>, and on a store with no tool rows it changes
    /// neither a row nor the plan.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Default_OnAStoreWithoutToolRows_MatchesThePreFilterStatement_RowsAndPlan(bool overlap)
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var ids = Seed(connection, withTool: false);

        var statement = RecallRanker.StatementFor(overlap, false, "fact_vec");
        var before = statement.Replace("\n    AND f.scope <> 'tool'", string.Empty, StringComparison.Ordinal);
        Assert.NotEqual(before, statement);
        // The default lex lane carries no scope term at all, so its D58-D60 cost is untouched.
        var lexStart = statement.IndexOf("lex(fact_id, rank) AS (", StringComparison.Ordinal);
        var lex = statement[lexStart..statement.IndexOf("LIMIT $seedK", lexStart, StringComparison.Ordinal)];
        Assert.DoesNotContain("scope", lex, StringComparison.Ordinal);
        Assert.DoesNotContain("$types", statement, StringComparison.Ordinal);

        foreach (var session in new[] { null, ids.CurrentSession })
        {
            Assert.Equal(Run(connection, before, session), Run(connection, statement, session));
            // Plan detail only: the id columns are bytecode addresses, which any added term shifts.
            Assert.Equal(PlanDetail(connection, before, session), PlanDetail(connection, statement, session));
        }
    }

    private static List<string> PlanDetail(SqliteConnection connection, string sql, long? session) =>
        Run(connection, "EXPLAIN QUERY PLAN\n" + sql, session).Select(row => row.Split('|', 4)[3]).ToList();

    private static List<string> Run(SqliteConnection connection, string sql, long? session)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$terms", "[\"quokka\"]");
        command.Parameters.AddWithValue("$match", "\"quokka\"");
        command.Parameters.AddWithValue("$seedK", 32);
        command.Parameters.AddWithValue("$currentSessionId", (object?)session ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", T0.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$limit", 501);
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(string.Join("|", values.Select(v => v is DBNull ? "NULL" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture))));
        }

        return rows;
    }
}
