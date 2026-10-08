using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2. The structured half of <c>recall</c> and the routines the MCP tools and the mod API now
/// share: whatever the API reports about a digest has to be the values the digest was written from.
/// </summary>
public class ModApiRecallSeamTests
{
    private const string Session = "cc-seam-session";

    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private static (int Status, JsonNode Body) Call(SandboxHome sandbox, string op, JsonObject body)
    {
        body["mod"] ??= "lens";
        var result = ModApi.Execute(
            sandbox.Home, new LocalRuntime(sandbox.Home), op, "lens", Encoding.UTF8.GetBytes(body.ToJsonString()));
        return (result.Status, JsonNode.Parse(result.Json)!);
    }

    private static JsonNode Recall(SandboxHome sandbox, string query)
    {
        var (status, body) = Call(sandbox, "recall", new JsonObject { ["session_id"] = Session, ["query"] = query });
        Assert.Equal(200, status);
        return body;
    }

    private static string Mcp(SandboxHome sandbox, string query) =>
        EngramMcpTools.Recall(
            sandbox.Home, new McpSessionId(Session), new McpHomeState(true), new LocalRuntime(sandbox.Home),
            new SessionPinStore(), query);

    private static long Write(SandboxHome sandbox, string slug, string body)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        return FactStore.Remember(
            connection,
            new FactWrite("/knowledge/testing/" + slug, "note", "states", body, "project", "stated"),
            T0).FactId;
    }

    private static void Execute(SandboxHome sandbox, string sql)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> HandlesIn(string text) =>
        Regex.Matches(text, @"^\[(f\d+)\]", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();

    [Fact]
    public void Facts_AreTheHandlesOfTheMcpTextInTheSameOrder()
    {
        using var sandbox = new SandboxHome();
        for (var i = 0; i < 4; i++)
        {
            Write(sandbox, "kestrel-" + i, "Kestrel loopback binding, note " + i + ".");
        }

        var api = Recall(sandbox, "kestrel loopback binding");
        var apiHandles = api["facts"]!.AsArray().Select(f => (string)f!["handle"]!).ToList();
        var mcpHandles = HandlesIn(Mcp(sandbox, "kestrel loopback binding"));

        Assert.NotEmpty(apiHandles);
        Assert.Equal(mcpHandles, apiHandles);
        Assert.Equal(apiHandles.Count, (int)api["fact_count"]!);
        Assert.Equal(HandlesIn((string)api["text"]!), apiHandles);
    }

    [Fact]
    public void Facts_CarryTheStoredBodyScopeAndLaneRanks()
    {
        using var sandbox = new SandboxHome();
        var id = Write(sandbox, "kestrel-only", "Kestrel binds loopback only.");

        var fact = Assert.Single(Recall(sandbox, "kestrel loopback")["facts"]!.AsArray())!;

        Assert.Equal(FactCatalog.HandleFor(id), (string)fact["handle"]!);
        Assert.Equal(id, (long)fact["id"]!);
        Assert.Equal("Kestrel binds loopback only.", (string)fact["body"]!);
        Assert.Equal("project", (string)fact["scope"]!);
        Assert.Equal(1, (int)fact["versions"]!);
        Assert.Equal(0, (int)fact["withheld_chars"]!);
        Assert.Null(fact["location"]);
        Assert.NotNull(fact["lanes"]!["lexical"]);
        Assert.NotNull(fact["lanes"]!["overlap"]);
        Assert.Null(fact["lanes"]!["vector"]);
    }

    [Fact]
    public void WithheldChars_IsTheNumberBehindTheLinesSuffix_AndZeroForTheRest()
    {
        using var sandbox = new SandboxHome();
        var longBody = string.Join(' ', Enumerable.Repeat("zanzibar vortex lattice rationale", 120));
        var truncated = Write(sandbox, "kestrel-long", longBody);
        var shortOne = Write(sandbox, "kestrel-short-a", "Zanzibar vortex lattice is short.");
        var shortTwo = Write(sandbox, "kestrel-short-b", "Zanzibar vortex lattice is also short.");

        var api = Recall(sandbox, "zanzibar vortex lattice");
        var facts = api["facts"]!.AsArray();
        var text = (string)api["text"]!;

        Assert.Equal(3, facts.Count);
        foreach (var fact in facts)
        {
            var handle = (string)fact!["handle"]!;
            var withheld = (int)fact["withheld_chars"]!;
            var line = text.Split('\n').Single(l => l.StartsWith($"[{handle}]", StringComparison.Ordinal));
            var suffix = Regex.Match(line, @" · \+([0-9.]+k?)\)$");
            if (withheld == 0)
            {
                Assert.False(suffix.Success, line);
            }
            else
            {
                Assert.True(suffix.Success, line);
                Assert.Equal(RecallEngine.FormatCharCount(withheld), suffix.Groups[1].Value);
            }
        }

        Assert.True((int)facts.Single(f => (string)f!["handle"]! == FactCatalog.HandleFor(truncated))!["withheld_chars"]! > 0);
        Assert.Equal(0, (int)facts.Single(f => (string)f!["handle"]! == FactCatalog.HandleFor(shortOne))!["withheld_chars"]!);
        Assert.Equal(0, (int)facts.Single(f => (string)f!["handle"]! == FactCatalog.HandleFor(shortTwo))!["withheld_chars"]!);
    }

    [Fact]
    public void WithheldChars_CountsDetailsOfASessionNote()
    {
        using var sandbox = new SandboxHome();
        var (status, remembered) = Call(sandbox, "remember", new JsonObject
        {
            ["session_id"] = Session,
            ["statement"] = "The quokka cache is warmed at boot.",
            ["evidence"] = "a test",
            ["details"] = new string('d', 2500),
        });
        Assert.Equal(200, status);

        var api = Recall(sandbox, "quokka cache warmed");
        var fact = Assert.Single(api["facts"]!.AsArray(), f => (string)f!["handle"]! == (string)remembered["handle"]!)!;

        Assert.Equal(2500, (int)fact["withheld_chars"]!);
        Assert.Contains("· +2.5k)", (string)api["text"]!, StringComparison.Ordinal);
        Assert.Equal("session", (string)fact["scope"]!);
    }

    [Fact]
    public void NoneCoverage_HasNoFacts_AndGapsEqualsTheHeadersGapsText()
    {
        using var sandbox = new SandboxHome();

        var api = Recall(sandbox, "zzzqqq xxxkkk");
        var text = (string)api["text"]!;

        Assert.Equal("none", (string)api["coverage"]!);
        Assert.Empty(api["facts"]!.AsArray());
        var gaps = (string)api["gaps"]!;
        Assert.Contains($"\ngaps: {gaps}\n", text + "\n", StringComparison.Ordinal);
        Assert.Equal(Mcp(sandbox, "zzzqqq xxxkkk"), text);
    }

    [Fact]
    public void HighCoverage_HasNullGaps_AndNoGapsLineInTheText()
    {
        using var sandbox = new SandboxHome();
        for (var i = 0; i < 3; i++)
        {
            Write(sandbox, "kestrel-" + i, "Kestrel loopback binding, note " + i + ".");
        }

        var api = Recall(sandbox, "kestrel loopback binding");

        Assert.Equal("high", (string)api["coverage"]!);
        Assert.Null(api["gaps"]);
        Assert.DoesNotContain("gaps:", (string)api["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialCoverage_GapsIsTheHeadersText()
    {
        using var sandbox = new SandboxHome();
        Write(sandbox, "kestrel-a", "Kestrel loopback binding, note a.");

        var api = Recall(sandbox, "kestrel loopback binding");

        Assert.Equal("partial", (string)api["coverage"]!);
        var gaps = (string?)api["gaps"];
        Assert.False(string.IsNullOrEmpty(gaps));
        Assert.Contains($"\ngaps: {gaps}\n", (string)api["text"]! + "\n", StringComparison.Ordinal);
    }

    [Fact]
    public void OverlapLaneNotBuilt_NotesIsTheHeadersNoteAndTextStillMatchesMcp()
    {
        using var sandbox = new SandboxHome();
        Write(sandbox, "kestrel-a", "Kestrel binds loopback only.");
        Execute(sandbox, "DELETE FROM schema_meta WHERE key = 'fact_token_version';");

        var api = Recall(sandbox, "kestrel loopback");
        var note = (string)Assert.Single(api["notes"]!.AsArray())!;

        Assert.Equal("overlap lane did not run (token index not built yet)", note);
        Assert.Contains($" · {note}", ((string)api["text"]!).Split('\n')[0], StringComparison.Ordinal);
        Assert.Equal(Mcp(sandbox, "kestrel loopback"), (string)api["text"]!);
    }

    [Fact]
    public void OverlapLaneOneVersionBehind_NotesNamesTheStaleTokenizer()
    {
        using var sandbox = new SandboxHome();
        Write(sandbox, "kestrel-a", "Kestrel binds loopback only.");
        Execute(sandbox, "UPDATE schema_meta SET value = '0' WHERE key = 'fact_token_version';");

        var note = (string)Assert.Single(Recall(sandbox, "kestrel loopback")["notes"]!.AsArray())!;

        Assert.Equal("overlap lane did not run (token index built by an older tokenizer)", note);
    }

    [Fact]
    public void EveryLaneThatCouldRun_HasEmptyNotes()
    {
        using var sandbox = new SandboxHome();
        Write(sandbox, "kestrel-a", "Kestrel binds loopback only.");

        var api = Recall(sandbox, "kestrel loopback");

        Assert.Empty(api["notes"]!.AsArray());
        Assert.DoesNotContain("lane did not run", (string)api["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void Lanes_AFactOnlyTheVectorLaneReached_HasNullLexicalAndOverlap()
    {
        var candidate = new RecallCandidate(
            42, "f42", "[f42] line", 0.1, OverlapRank: null, LexicalRank: null, VectorRank: 3,
            FactOrigin.LongTerm, 10, Packed: true,
            Source: new RecallCandidateSource("a body", "project", 2, 0, null));

        var fact = ModApi.ToRecallFact(candidate);

        Assert.Null(fact.Lanes.Lexical);
        Assert.Null(fact.Lanes.Overlap);
        Assert.Equal(3, fact.Lanes.Vector);
        Assert.Equal("f42", fact.Handle);
        Assert.Equal(2, fact.Versions);
    }

    [Fact]
    public void Lanes_AFactAllThreeReached_ReportsEachRankOneBased()
    {
        var candidate = new RecallCandidate(
            7, "f7", "[f7] line", 0.1, OverlapRank: 2, LexicalRank: 1, VectorRank: 5,
            FactOrigin.LongTerm, 10, Packed: true,
            Source: new RecallCandidateSource("b", "code", 1, 0, "src/A.cs:3"));

        var fact = ModApi.ToRecallFact(candidate);

        Assert.Equal((1, 2, 5), (fact.Lanes.Lexical, fact.Lanes.Overlap, fact.Lanes.Vector));
        Assert.Equal("src/A.cs:3", fact.Location);
    }

    [Fact]
    public void Remember_DetailsOverTheCeiling_IsTheSameMessageTheMcpToolRefuses()
    {
        using var sandbox = new SandboxHome();
        var details = new string('x', 8000);

        var viaMcp = EngramMcpTools.Remember(
            sandbox.Home, new McpSessionId(Session), new McpHomeState(true), new LocalRuntime(sandbox.Home),
            "Has enormous details.", details: details, evidence: "e");
        var (status, body) = Call(sandbox, "remember", new JsonObject
        {
            ["session_id"] = Session,
            ["statement"] = "Has enormous details.",
            ["evidence"] = "e",
            ["details"] = details,
        });

        Assert.Equal(400, status);
        Assert.Equal(viaMcp, (string)body["detail"]!);
        Assert.Contains("2,000-token ceiling", viaMcp, StringComparison.Ordinal);
    }

    [Fact]
    public void Revise_DetailsOverTheCeiling_UsesTheSameMessageAsRemember()
    {
        using var sandbox = new SandboxHome();
        var details = new string('x', 8000);
        var first = EngramMcpTools.Remember(
            sandbox.Home, new McpSessionId(Session), new McpHomeState(true), new LocalRuntime(sandbox.Home), "A belief.");
        var handle = Regex.Match(first, @"\[(f\d+)\]").Groups[1].Value;

        var viaRevise = EngramMcpTools.Revise(
            sandbox.Home, new McpSessionId(Session), new McpHomeState(true), handle, "A better belief.", "reason",
            details: details);

        Assert.Equal(DetailsCeiling.Error(details), viaRevise);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("short", false)]
    public void DetailsCeiling_NoErrorWhenAbsentOrSmall(string? details, bool hasError) =>
        Assert.Equal(hasError, DetailsCeiling.Error(details) is not null);

    [Fact]
    public void DetailsCeiling_IsExactlyTwoThousandTokens()
    {
        var atCeiling = new string('x', (int)(DetailsCeiling.MaxTokens * TokenEstimator.CharactersPerToken));
        var over = atCeiling + new string('x', 8);

        Assert.True(TokenEstimator.Estimate(atCeiling) <= DetailsCeiling.MaxTokens);
        Assert.Null(DetailsCeiling.Error(atCeiling));
        Assert.True(TokenEstimator.Estimate(over) > DetailsCeiling.MaxTokens);
        Assert.NotNull(DetailsCeiling.Error(over));
    }

    [Fact]
    public void ResolverEquivalence_EveryFileEntityTheIndexerWrote_ResolvesToItself()
    {
        using var sandbox = new SandboxHome();
        var repo = PathCanonicalizer.Canonical(Path.Combine(sandbox.Home.Root, "equiv-repo"));
        Directory.CreateDirectory(Path.Combine(repo, "docs", "deep"));
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        File.WriteAllText(Path.Combine(repo, "README.md"), "# Readme\n\nHello.\n\n## Usage\n\nRun it.\n");
        File.WriteAllText(Path.Combine(repo, "docs", "deep", "Guide Notes.md"), "# Guide\n\nText.\n");
        File.WriteAllText(Path.Combine(repo, "src", "Program.cs"), "namespace Demo;\n\npublic class Greeter\n{\n    public void Hi() { }\n}\n");

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        CodeIndexer.Index(
            connection, sandbox.Home, ConfigFile.Empty, IndexingSettings.Default,
            new IndexOptions(repo, Apply: true, Drain: false, Full: false), DateTimeOffset.UtcNow);

        string repoPath;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT repo_path FROM repo_registry LIMIT 1;";
            repoPath = (string)command.ExecuteScalar()!;
        }

        var fileEntities = new SortedSet<string>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT path FROM fact WHERE valid_to IS NULL AND substr(path, 1, $len) = $prefix;";
            command.Parameters.AddWithValue("$prefix", repoPath + "/");
            command.Parameters.AddWithValue("$len", repoPath.Length + 1);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                var hash = path.IndexOf('#', StringComparison.Ordinal);
                fileEntities.Add(hash < 0 ? path : path[..hash]);
            }
        }

        Assert.True(fileEntities.Count >= 3, "the fixture should have produced at least three file entities");
        foreach (var entity in fileEntities)
        {
            var relative = entity[(repoPath.Length + 1)..];
            var disk = Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar));

            var resolved = CodeEntityResolver.Resolve(connection, disk);

            Assert.NotNull(resolved);
            Assert.Equal(entity, resolved!.Value.EntityPath);
            Assert.Equal(repoPath, resolved.Value.RepoPath);
        }
    }

    [Fact]
    public void Resolver_ASiblingDirectorySharingThePrefix_IsNotResolvedToTheRepo()
    {
        using var sandbox = new SandboxHome();
        var repo = PathCanonicalizer.Canonical(Path.Combine(sandbox.Home.Root, "repo"));
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "A.md"), "# A\n\nText.\n");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        CodeIndexer.Index(
            connection, sandbox.Home, ConfigFile.Empty, IndexingSettings.Default,
            new IndexOptions(repo, Apply: true, Drain: false, Full: false), DateTimeOffset.UtcNow);

        Assert.NotNull(CodeEntityResolver.Resolve(connection, Path.Combine(repo, "A.md")));
        Assert.Null(CodeEntityResolver.Resolve(connection, repo + "-sibling" + Path.DirectorySeparatorChar + "A.md"));
        Assert.Null(CodeEntityResolver.Resolve(connection, repo));
    }

    [Fact]
    public void Resolver_ADetachedCheckout_ResolvesToNothing()
    {
        using var sandbox = new SandboxHome();
        var repo = PathCanonicalizer.Canonical(Path.Combine(sandbox.Home.Root, "detached-repo"));
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "A.md"), "# A\n\nText.\n");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        CodeIndexer.Index(
            connection, sandbox.Home, ConfigFile.Empty, IndexingSettings.Default,
            new IndexOptions(repo, Apply: true, Drain: false, Full: false), DateTimeOffset.UtcNow);
        Assert.NotNull(CodeEntityResolver.Resolve(connection, Path.Combine(repo, "A.md")));

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE repo_registry SET detached_at = 1;";
            command.ExecuteNonQuery();
        }

        Assert.Null(CodeEntityResolver.Resolve(connection, Path.Combine(repo, "A.md")));
    }

    [Fact]
    public void Resolver_ANestedRepoInsideAnother_PicksTheLongerRoot()
    {
        using var sandbox = new SandboxHome();
        var outer = PathCanonicalizer.Canonical(Path.Combine(sandbox.Home.Root, "outer"));
        var inner = Path.Combine(outer, "inner");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(outer, "O.md"), "# O\n\nText.\n");
        File.WriteAllText(Path.Combine(inner, "I.md"), "# I\n\nText.\n");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        foreach (var root in new[] { outer, inner })
        {
            CodeIndexer.Index(
                connection, sandbox.Home, ConfigFile.Empty, IndexingSettings.Default,
                new IndexOptions(root, Apply: true, Drain: false, Full: false), DateTimeOffset.UtcNow);
        }

        var resolved = CodeEntityResolver.Resolve(connection, Path.Combine(inner, "I.md"));

        Assert.NotNull(resolved);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT repo_path FROM repo_registry WHERE disk_path = $disk;";
        command.Parameters.AddWithValue("$disk", inner);
        Assert.Equal((string)command.ExecuteScalar()!, resolved!.Value.RepoPath);
        Assert.EndsWith("/I.md", resolved.Value.EntityPath, StringComparison.Ordinal);
    }

    [Fact]
    public void PathFacts_AnAuthoredFactComesBeforeCodeFacts_ThenNewestFirst()
    {
        using var sandbox = new SandboxHome();
        var repo = PathCanonicalizer.Canonical(Path.Combine(sandbox.Home.Root, "order-repo"));
        Directory.CreateDirectory(repo);
        var file = Path.Combine(repo, "NOTES.md");
        File.WriteAllText(file, "# Deployment notes\n\nShip on Tuesdays.\n\n## Rollback\n\nRevert the tag.\n");

        long authored;
        string entityPath;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            CodeIndexer.Index(
                connection, sandbox.Home, ConfigFile.Empty, IndexingSettings.Default,
                new IndexOptions(repo, Apply: true, Drain: false, Full: false), DateTimeOffset.UtcNow);
            entityPath = CodeEntityResolver.Resolve(connection, file)!.Value.EntityPath;

            authored = FactStore.Remember(
                connection,
                new FactWrite(entityPath, "file", "invariant", "Never reorder the rollback steps.", "project", "stated"),
                DateTimeOffset.FromUnixTimeSeconds(1_000)).FactId;
        }

        var (status, body) = Call(sandbox, "path-facts", new JsonObject { ["path"] = file });

        Assert.Equal(200, status);
        var facts = body["facts"]!.AsArray();
        Assert.True(facts.Count >= 3, "expected the authored fact plus at least two code facts");
        Assert.Equal(FactCatalog.HandleFor(authored), (string)facts[0]!["handle"]!);
        Assert.False((bool)facts[0]!["regenerable"]!);
        Assert.All(facts.Skip(1), f => Assert.True((bool)f!["regenerable"]!));
        var times = facts.Skip(1).Select(f => (long)f!["valid_from"]!).ToList();
        Assert.Equal(times.OrderByDescending(t => t).ToList(), times);
        Assert.True((long)facts[0]!["valid_from"]! < times.Min(), "the authored fact is the oldest, so only the regenerable sort key can put it first");
    }
}
