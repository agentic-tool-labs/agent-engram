using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// The spec §9 trio: browse is a table of contents, expand is scrutiny of one handle,
/// revise is explicit belief revision through the store's own collision rule.
/// </summary>
public class McpBrowseExpandReviseTests
{
    private static readonly McpHomeState Initialized = new(true);
    private static readonly DateTimeOffset T0 = new(2026, 8, 7, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Browse_ShowsCountsHereAndUnder_AndFoldsPhantomIntermediates()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Write(connection, "/projects/browse-test", "decided", "the project exists");
            Write(connection, "/projects/browse-test", "uses", "sqlite underneath");
            Write(connection, "/projects/browse-test/code/api", "declared-as", "the api repo");
            Write(connection, "/projects/browse-test/code/api", "uses", "http");
            Write(connection, "/projects/browse-test/code/api", "imports", "nothing yet");
            Write(connection, "/projects/browse-test/decisions", "decided", "trunk releases");
        }

        var result = EngramMcpTools.Browse(
            sandbox.Home, new McpSessionId("browse-session"), Initialized, "/projects/browse-test");

        Assert.Contains("2 facts here, 4 under it", result);

        // /projects/browse-test/code was never written as an entity — only its child was —
        // yet it must appear as a folded segment carrying its subtree's count.
        Assert.Contains("code — 3 facts", result);
        Assert.Contains("decisions — 1 fact", result);
        Assert.Contains("[f", result);
        Assert.Contains("the project exists", result);
    }

    [Fact]
    public void Browse_AtDepthTwo_ReachesGrandchildren()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Write(connection, "/projects/deep-test/code/api", "uses", "http");
        }

        var shallow = EngramMcpTools.Browse(
            sandbox.Home, new McpSessionId("s"), Initialized, "/projects/deep-test");
        var deep = EngramMcpTools.Browse(
            sandbox.Home, new McpSessionId("s"), Initialized, "/projects/deep-test", depth: 2);

        Assert.DoesNotContain("api", shallow);
        Assert.Contains("api — 1 fact", deep);
    }

    [Fact]
    public void Browse_WhereNothingIs_SaysSoInsteadOfInventingStructure()
    {
        using var sandbox = new SandboxHome();

        var result = EngramMcpTools.Browse(
            sandbox.Home, new McpSessionId("s"), Initialized, "/nowhere/at/all");

        Assert.Contains("Nothing in memory under /nowhere/at/all", result);
    }

    [Fact]
    public void Revise_ClosesTheOldBelief_AndRecordsTheReason()
    {
        using var sandbox = new SandboxHome();
        long oldId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            oldId = Write(connection, "/projects/revise-test", "deploys-with", "jenkins on fridays");
        }

        var session = new McpSessionId("revise-session");
        var result = EngramMcpTools.Revise(
            sandbox.Home,
            session,
            Initialized,
            $"f{oldId}",
            "github actions on merge",
            "the user said jenkins was decommissioned");

        Assert.Contains("revised", result);

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            var old = FactStore.ReadById(connection, oldId)!;
            Assert.NotNull(old.ValidTo);
            Assert.NotNull(old.SupersededBy);

            var replacement = FactStore.ReadById(connection, old.SupersededBy!.Value)!;
            Assert.Equal("github actions on merge", replacement.Body);
            Assert.Equal("deploys-with", replacement.Predicate);
            Assert.Null(replacement.ValidTo);

            var reasons = MemoryBrowser.Reasons(connection, [oldId]);
            Assert.Equal("the user said jenkins was decommissioned", reasons[oldId]);
        }
    }

    [Fact]
    public void Revise_OnAClosedFact_RefusesToRewriteHistory()
    {
        using var sandbox = new SandboxHome();
        long oldId;
        long factsBefore;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            oldId = Write(connection, "/projects/revise-test", "uses", "the old thing");
            FactStore.Forget(connection, oldId, "already retracted", T0);
            factsBefore = Count(connection);
        }

        var result = EngramMcpTools.Revise(
            sandbox.Home, new McpSessionId("s"), Initialized, $"f{oldId}", "the new thing", "too late");

        Assert.Contains("already closed", result);
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Assert.Equal(factsBefore, Count(connection));
        }
    }

    [Fact]
    public void Revise_WithoutAReason_DeclinesToWrite()
    {
        using var sandbox = new SandboxHome();
        long oldId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            oldId = Write(connection, "/projects/revise-test", "uses", "something");
        }

        var result = EngramMcpTools.Revise(
            sandbox.Home, new McpSessionId("s"), Initialized, $"f{oldId}", "corrected", "  ");

        Assert.Contains("needs both", result);
    }

    [Fact]
    public void Expand_History_ShowsTheChainAndWhyItMoved()
    {
        using var sandbox = new SandboxHome();
        long oldId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            oldId = Write(connection, "/projects/expand-test", "deploys-with", "jenkins nightly");
        }

        EngramMcpTools.Revise(
            sandbox.Home,
            new McpSessionId("s"),
            Initialized,
            $"f{oldId}",
            "actions on merge",
            "pipeline was replaced");

        var history = EngramMcpTools.Expand(
            sandbox.Home, new McpSessionId("s"), Initialized, $"f{oldId}", "history");

        Assert.Contains("2 versions", history);
        Assert.Contains("jenkins nightly", history);
        Assert.Contains("actions on merge", history);
        Assert.Contains("pipeline was replaced", history);
    }

    [Fact]
    public void Expand_Evidence_TellsRegenerableFromRecorded()
    {
        using var sandbox = new SandboxHome();
        long factId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            factId = FactStore.Remember(
                connection,
                new FactWrite(
                    "/projects/expand-test/code/api/src/Auth.cs", "file", "about",
                    "token validation lives here", "code", "observed",
                    Evidence: "src/Auth.cs @ ab12cd34", Regenerable: true),
                T0).FactId;
        }

        var result = EngramMcpTools.Expand(
            sandbox.Home, new McpSessionId("s"), Initialized, $"f{factId}", "evidence");

        Assert.Contains("src/Auth.cs @ ab12cd34", result);
        Assert.Contains("regenerable", result);
        Assert.Contains("observed", result);
    }

    [Fact]
    public void Expand_Related_ListsTheNeighbourhood()
    {
        using var sandbox = new SandboxHome();
        long factId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            factId = Write(connection, "/projects/expand-test", "uses", "sqlite");
            Write(connection, "/projects/expand-test", "decided", "no orm");
            Write(connection, "/projects/expand-test/code/api", "uses", "http");
        }

        var result = EngramMcpTools.Expand(
            sandbox.Home, new McpSessionId("s"), Initialized, $"f{factId}", "related");

        Assert.Contains("no orm", result);
        Assert.Contains("http", result);
        Assert.DoesNotContain("sqlite", result);
    }

    [Fact]
    public void Expand_RejectsWhatItDoesNotKnow()
    {
        using var sandbox = new SandboxHome();
        long factId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            factId = Write(connection, "/projects/expand-test", "uses", "sqlite");
        }

        Assert.Contains(
            "Unknown view",
            EngramMcpTools.Expand(sandbox.Home, new McpSessionId("s"), Initialized, $"f{factId}", "vibes"));
        Assert.Contains(
            "not a fact handle",
            EngramMcpTools.Expand(sandbox.Home, new McpSessionId("s"), Initialized, "banana", "history"));
        Assert.Contains(
            "No fact with id",
            EngramMcpTools.Expand(sandbox.Home, new McpSessionId("s"), Initialized, "f999999", "history"));
    }

    private const string RepoPath = "/projects/p/code/r";
    private const string GistBody = "Foo() — builds the widget.";

    // The indexer names the entity before remembering (a symbol's name is not its path leaf), so
    // the fixture does too.
    private static long WriteIndexed(SqliteConnection connection, string path, string kind, string body)
    {
        var hash = path.IndexOf('#');
        FactStore.EnsureEntity(
            connection, null, path, kind, T0.ToUnixTimeSeconds(), hash >= 0 ? path[(hash + 1)..] : null);

        return FactStore.Remember(
            connection,
            new FactWrite(path, kind, "declared-as", body, "code", "observed", "src/a.cs @ 87d3f8ba", Regenerable: true),
            T0).FactId;
    }

    private static string Expand(SandboxHome sandbox, long factId, string view, int budget = 800, int offset = 0) =>
        EngramMcpTools.Expand(sandbox.Home, new McpSessionId("s"), Initialized, $"f{factId}", view, budget, offset);

    private static string RegisterRepo(SqliteConnection connection, string? diskPath, params string[] indexedFiles)
    {
        Execute(
            connection,
            "INSERT INTO repo_registry (repo_path, identity, disk_path, created_at) VALUES ($repo, $id, $disk, 0);",
            ("$repo", RepoPath), ("$id", "id-" + RepoPath), ("$disk", diskPath));
        foreach (var file in indexedFiles)
        {
            Execute(
                connection,
                "INSERT INTO file_state (repo_path, path, blob_sha, indexed_at) VALUES ($repo, $path, 'x', 4102444800);",
                ("$repo", RepoPath), ("$path", file));
        }

        return RepoPath;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private static string DiskCheckout()
    {
        var dir = Path.Combine(Path.GetTempPath(), "engram-expand-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "a.cs"), "class A {}");
        return dir;
    }

    [Fact]
    public void Expand_IndexedCodeFact_Details_SaysGistOnlyAndWhereToRead()
    {
        using var sandbox = new SandboxHome();
        var disk = DiskCheckout();
        try
        {
            long id;
            using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
            {
                id = WriteIndexed(connection, RepoPath + "/src/a.cs#Foo", "symbol", GistBody);
                RegisterRepo(connection, disk, "src/a.cs");
            }

            var result = Expand(sandbox, id, "details");

            Assert.StartsWith(GistBody + "\n\n", result, StringComparison.Ordinal);
            Assert.Contains("r:src/a.cs", result, StringComparison.Ordinal);
            Assert.Contains(Path.Combine(disk, "src", "a.cs"), result, StringComparison.Ordinal);
            Assert.Contains("only this indexed gist", result, StringComparison.Ordinal);
            Assert.Contains("engram_navigate \"Foo\"", result, StringComparison.Ordinal);
            Assert.Contains("repo \"r\"", result, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(disk, recursive: true);
        }
    }

    [Fact]
    public void Expand_IndexedCodeFact_Source_NamesTheIndexedFile()
    {
        using var sandbox = new SandboxHome();
        long id;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            id = WriteIndexed(connection, RepoPath + "/src/a.cs#Foo", "symbol", GistBody);
        }

        var result = Expand(sandbox, id, "source");

        Assert.Contains("indexed from r:src/a.cs", result, StringComparison.Ordinal);
        Assert.DoesNotContain("outside any tracked session", result, StringComparison.Ordinal);
        Assert.Contains("learned via 'observed'", result, StringComparison.Ordinal);
        Assert.Contains("It is currently believed.", result, StringComparison.Ordinal);
        Assert.Contains("only this indexed gist", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Expand_IndexedCodeFact_Evidence_KeepsEvidenceAndAddsNote()
    {
        using var sandbox = new SandboxHome();
        long id;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            id = WriteIndexed(connection, RepoPath + "/src/a.cs#Foo", "symbol", GistBody);
        }

        var result = Expand(sandbox, id, "evidence");

        Assert.Contains("Evidence: src/a.cs @ 87d3f8ba", result, StringComparison.Ordinal);
        Assert.Contains("It is regenerable", result, StringComparison.Ordinal);
        Assert.Contains("Indexed code: r:src/a.cs", result, StringComparison.Ordinal);
        Assert.Contains("engram_navigate \"Foo\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Expand_CodeFile_HintsImports_Section_HasNoHint()
    {
        using var sandbox = new SandboxHome();
        long file;
        long section;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            file = WriteIndexed(connection, RepoPath + "/src/a.cs", "file", "a.cs — the widget file.");
            section = WriteIndexed(connection, RepoPath + "/docs/guide.md#Intro", "section", "Intro — what this is.");
        }

        var fileResult = Expand(sandbox, file, "details");
        var sectionResult = Expand(sandbox, section, "details");

        Assert.Contains("engram_navigate \"src/a.cs\" imports, repo \"r\"", fileResult, StringComparison.Ordinal);
        Assert.Contains("Indexed code: r:docs/guide.md", sectionResult, StringComparison.Ordinal);
        Assert.DoesNotContain("engram_navigate", sectionResult, StringComparison.Ordinal);
    }

    [Fact]
    public void Expand_IndexedCodeFact_DetachedRepo_OmitsDiskPathOnly()
    {
        using var sandbox = new SandboxHome();
        long detached;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            detached = WriteIndexed(connection, RepoPath + "/src/a.cs#Foo", "symbol", GistBody);
            RegisterRepo(connection, diskPath: null, "src/a.cs");
        }

        var result = Expand(sandbox, detached, "details");

        Assert.DoesNotContain("Read it at", result, StringComparison.Ordinal);
        Assert.Contains("Indexed code: r:src/a.cs", result, StringComparison.Ordinal);
        Assert.Contains("only this indexed gist", result, StringComparison.Ordinal);

        using var unregistered = new SandboxHome();
        long other;
        using (var connection = EngramDatabase.OpenInitialized(unregistered.Home))
        {
            other = WriteIndexed(connection, RepoPath + "/src/a.cs#Foo", "symbol", GistBody);
        }

        var bare = Expand(unregistered, other, "details");
        Assert.DoesNotContain("Read it at", bare, StringComparison.Ordinal);
        Assert.Contains("only this indexed gist", bare, StringComparison.Ordinal);
    }

    [Fact]
    public void Expand_IndexedCodeFact_StaleFile_SaysSoBesideThePath()
    {
        using var sandbox = new SandboxHome();
        var disk = DiskCheckout();
        try
        {
            long id;
            using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
            {
                id = WriteIndexed(connection, RepoPath + "/src/a.cs#Foo", "symbol", GistBody);
                RegisterRepo(connection, disk);
                Execute(
                    connection,
                    "INSERT INTO file_state (repo_path, path, blob_sha, indexed_at) VALUES ($repo, 'src/a.cs', 'x', 1);",
                    ("$repo", RepoPath));
            }

            Assert.Contains(
                Path.Combine(disk, "src", "a.cs") + " (stale).", Expand(sandbox, id, "details"), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(disk, recursive: true);
        }
    }

    [Fact]
    public void Expand_RevisedCodeScopeFact_GetsNoGistNote()
    {
        using var sandbox = new SandboxHome();
        long id;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            id = FactStore.Remember(
                connection,
                new FactWrite(RepoPath + "/src/a.cs#Foo", "symbol", "declared-as", "Foo is load-bearing.", "code", "stated"),
                T0).FactId;
        }

        Assert.Equal("Foo is load-bearing.", Expand(sandbox, id, "details"));
        Assert.DoesNotContain("Indexed code", Expand(sandbox, id, "evidence"), StringComparison.Ordinal);
        Assert.DoesNotContain("Indexed code", Expand(sandbox, id, "source"), StringComparison.Ordinal);
        Assert.Contains("outside any tracked session", Expand(sandbox, id, "source"), StringComparison.Ordinal);
        Assert.DoesNotContain("indexed from", Expand(sandbox, id, "source"), StringComparison.Ordinal);
    }

    [Fact]
    public void Expand_IndexedCodeFact_Details_PagesAcrossTheNote()
    {
        using var sandbox = new SandboxHome();
        long id;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            id = WriteIndexed(connection, RepoPath + "/src/a.cs#Foo", "symbol", GistBody);
        }

        var whole = Expand(sandbox, id, "details");
        Assert.DoesNotContain("showing chars", whole, StringComparison.Ordinal);

        var joined = new System.Text.StringBuilder();
        var offset = 0;
        var pages = 0;
        for (var guard = 0; guard < 500; guard++)
        {
            var page = Expand(sandbox, id, "details", budget: 8, offset: offset);
            const string footerMarker = "\n\nshowing chars ";
            var cut = page.LastIndexOf(footerMarker, StringComparison.Ordinal);
            var body = cut < 0 ? page : page[..cut];
            pages++;

            // The note is inside the paged text, so no page may exceed the budget however far in it is.
            Assert.True(body.Length <= 8 * TokenEstimator.CharactersPerToken, $"page {pages} is {body.Length} chars");

            joined.Append(body);
            if (cut < 0)
            {
                break;
            }

            offset = int.Parse(page[(page.LastIndexOf("offset: ", StringComparison.Ordinal) + "offset: ".Length)..]);
        }

        Assert.True(pages > 3, $"only {pages} pages — the budget was meant to split the note");
        Assert.Equal(whole, joined.ToString());
    }

    private static long Write(SqliteConnection connection, string path, string predicate, string body) =>
        FactStore.Remember(
            connection,
            new FactWrite(path, "concept", predicate, body, "project", "stated"),
            T0).FactId;

    private static long Count(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM fact;";
        return (long)command.ExecuteScalar()!;
    }
}
