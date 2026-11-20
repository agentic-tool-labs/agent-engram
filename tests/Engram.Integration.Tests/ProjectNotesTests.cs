using System.Diagnostics;
using System.Runtime.CompilerServices;
using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// P4 (D98): <c>engram_remember scope:"project"</c> files a note under the project the call's own
/// <c>project_dir</c> resolves to, and degrades to a session note, with a notice naming why, for
/// every way that resolution can fail.
/// </summary>
public sealed class ProjectNotesTests : IDisposable
{
    private static readonly McpHomeState Initialized = new(true);

    private readonly List<string> scratch = [];

    public void Dispose()
    {
        foreach (var directory in scratch)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void NoScope_WritesTheSameSessionNoteAndResponseAsBefore()
    {
        using var sandbox = new SandboxHome();
        var repo = IndexedCheckout(sandbox);

        var response = Remember(sandbox, "s1", "The build pipeline retries flaky uploads three times.", projectDir: repo);

        var fact = OnlyFact(sandbox);
        Assert.StartsWith("/sessions/", fact.Path, StringComparison.Ordinal);
        Assert.Equal("session", fact.Scope);
        Assert.Equal("noted", fact.Predicate);
        Assert.Equal(
            $"[{fact.Handle}] remembered: (unspecified subject) — \"The build pipeline retries flaky uploads three times.\"",
            response);
        Assert.Null(LastRemember(sandbox).Scope);
        Assert.Null(LastRemember(sandbox).ProjectUnresolved);
    }

    [Fact]
    public void ScopeProject_InAnIndexedButUnenrolledCheckout_FilesUnderTheProject()
    {
        using var sandbox = new SandboxHome();
        var repo = IndexedCheckout(sandbox);
        var project = ProjectOf(sandbox, repo);

        var response = Remember(sandbox, "s1", "We chose WAL mode for the store.", "project", repo, agent: "helper");

        var fact = OnlyFact(sandbox);
        Assert.Equal(ProjectNotes.PathFor(project, "We chose WAL mode for the store."), fact.Path);
        Assert.Matches("^/projects/[^/]+/notes/[0-9a-f]{16}$", fact.Path);
        Assert.Equal("project", fact.Scope);
        Assert.Equal("observed", fact.LearnedVia);
        Assert.Equal("note", fact.Kind);
        Assert.Equal("decided", fact.Predicate);
        Assert.Equal(SessionRowId(sandbox, "s1"), fact.SessionId);
        Assert.EndsWith(" [via helper] [project " + project + "]", response, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', response);
        Assert.Equal("project", LastRemember(sandbox).Scope);
        Assert.Null(LastRemember(sandbox).ProjectUnresolved);
        Assert.Empty(RepoEnrollment.ListAll(Open(sandbox)));
    }

    [Fact]
    public void ScopeProject_NeverWritesSessionRepoPath()
    {
        using var sandbox = new SandboxHome();
        var repo = IndexedCheckout(sandbox);

        Remember(sandbox, "s1", "The hook budget is ten milliseconds.", "project", repo);
        Remember(sandbox, "s1", "A plain session note.");

        using var connection = Open(sandbox);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM session WHERE repo_path IS NOT NULL;";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void ScopeProject_FromALinkedWorktree_ResolvesToTheSameProject()
    {
        using var sandbox = new SandboxHome();
        var repo = IndexedCheckout(sandbox, commit: true);
        var project = ProjectOf(sandbox, repo);
        var worktree = NewDirectory();
        Directory.Delete(worktree);
        Assert.True(Git(repo, "worktree", "add", "-q", worktree), "git worktree add failed");

        var response = Remember(sandbox, "s1", "The schema file is the authority.", "project", Path.Combine(worktree, "src"));

        Assert.Contains("[project " + project + "]", response, StringComparison.Ordinal);
        Assert.Equal(ProjectNotes.PathFor(project, "The schema file is the authority."), OnlyFact(sandbox).Path);
    }

    [Fact]
    public void ScopeProject_WithoutProjectDir_KeepsASessionNote()
    {
        using var sandbox = new SandboxHome();

        var response = Remember(sandbox, "s1", "Something durable.", "project", projectDir: null);

        AssertDegraded(sandbox, response, "no-dir",
            "no project_dir given — kept as a session note; pass your working directory to file it under the project");
    }

    [Theory]
    [InlineData("relative/dir")]
    [InlineData("/definitely/not/a/directory/engram-p4")]
    public void ScopeProject_WithARelativeOrMissingDir_KeepsASessionNote(string projectDir)
    {
        using var sandbox = new SandboxHome();

        var response = Remember(sandbox, "s1", "Something durable.", "project", projectDir);

        AssertDegraded(sandbox, response, "bad-dir", "project_dir is not an existing absolute directory — kept as a session note");
    }

    [Fact]
    public void ScopeProject_OutsideAnyCheckout_KeepsASessionNote()
    {
        using var sandbox = new SandboxHome();
        var plain = NewDirectory();

        var response = Remember(sandbox, "s1", "Something durable.", "project", plain);

        AssertDegraded(sandbox, response, "not-checkout", $"{plain} is not in a git checkout — kept as a session note");
    }

    [Fact]
    public void ScopeProject_InACheckoutNeverIndexed_KeepsASessionNote()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout();

        var response = Remember(sandbox, "s1", "Something durable.", "project", repo);

        AssertDegraded(sandbox, response, "not-indexed", $"{CodeIndexer.ResolveRoot(repo)} is not indexed by Engram — kept as a session note");
    }

    [Fact]
    public void ScopeProject_WhenTheOnlyRegistryRowIsRetired_KeepsASessionNote()
    {
        using var sandbox = new SandboxHome();
        var repo = IndexedCheckout(sandbox);
        using (var connection = Open(sandbox))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE repo_registry SET detached_at = 1, disk_path = NULL;";
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var response = Remember(sandbox, "s1", "Something durable.", "project", repo);

        AssertDegraded(sandbox, response, "not-indexed", $"{CodeIndexer.ResolveRoot(repo)} is not indexed by Engram — kept as a session note");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("session")]
    public void AnyScopeButProject_IgnoresProjectDir_AndRunsNoGit(string? scope)
    {
        using var sandbox = new SandboxHome();
        var repo = IndexedCheckout(sandbox);

        var gitCalls = CountGit(() => Remember(sandbox, "s1", "Kept in the session.", scope, repo));

        Assert.Equal(0, gitCalls);
        Assert.StartsWith("/sessions/", OnlyFact(sandbox).Path, StringComparison.Ordinal);
    }

    [Fact]
    public void ScopeProject_RunsGit_SoTheZeroCountAboveCanFail()
    {
        using var sandbox = new SandboxHome();
        var repo = IndexedCheckout(sandbox);

        Assert.True(CountGit(() => Remember(sandbox, "s1", "Kept under the project.", "project", repo)) > 0);
    }

    [Fact]
    public void ScopeProject_RepeatingALiveStatement_ReturnsTheExistingHandle()
    {
        using var sandbox = new SandboxHome();
        var repo = IndexedCheckout(sandbox);

        var first = HandleOf(Remember(sandbox, "s1", "Never call the server from the hook.", "project", repo));
        var second = HandleOf(Remember(sandbox, "s2", "never call the server, from the hook", "project", repo));

        Assert.Equal(first, second);
        Assert.Single(Facts(sandbox));
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("user")]
    [InlineData("Project")]
    public void AnUnknownScope_IsAnErrorListingTheValidOnes_AndWritesNothing(string scope)
    {
        using var sandbox = new SandboxHome();
        var before = FactCount(sandbox);

        var response = Remember(sandbox, "s1", "Something.", scope, projectDir: null);

        Assert.Equal($"Unknown scope '{scope}'; valid scopes: session, project. Nothing was saved.", response);
        Assert.Equal(before, FactCount(sandbox));
    }

    [Fact]
    public void TwoStatementsWhoseShortFingerprintsCollide_GetDistinctProjectPaths()
    {
        const string a = "project note collision probe 36851";
        const string b = "project note collision probe 77915";
        Assert.Equal(FactStore.Fingerprint(a), FactStore.Fingerprint(b));

        using var sandbox = new SandboxHome();
        var repo = IndexedCheckout(sandbox);

        var first = HandleOf(Remember(sandbox, "s1", a, "project", repo));
        var second = HandleOf(Remember(sandbox, "s1", b, "project", repo));

        Assert.NotEqual(first, second);
        var paths = Facts(sandbox).Select(fact => fact.Path).ToList();
        Assert.Equal(2, paths.Distinct().Count());
        Assert.All(paths, path => Assert.StartsWith("/projects/", path, StringComparison.Ordinal));
    }

    [Fact]
    public void ExistingSessionNotes_AreUntouchedByAProjectWrite()
    {
        using var sandbox = new SandboxHome();
        var repo = IndexedCheckout(sandbox);
        Remember(sandbox, "s1", "First working note.");
        Remember(sandbox, "s2", "Second working note.", agent: "helper");
        var before = Facts(sandbox);

        Remember(sandbox, "s1", "First working note.", "project", repo);

        var after = Facts(sandbox);
        Assert.Equal(before, after.Where(fact => fact.Path.StartsWith("/sessions/", StringComparison.Ordinal)).ToList());
        Assert.Single(after, fact => fact.Scope == "project");
    }

    [Fact]
    public void AProjectNote_IsLongTermToRecall_AndReachedByTheProjectType()
    {
        using var sandbox = new SandboxHome();
        var repo = IndexedCheckout(sandbox);
        var handle = HandleOf(Remember(sandbox, "s1", "Zanzibar migrations always snapshot first.", "project", repo));

        var typed = EngramMcpTools.Recall(
            sandbox.Home, new McpSessionId("s1"), Initialized, new LocalRuntime(sandbox.Home), new SessionPinStore(),
            "zanzibar migrations snapshot", types: ["project"]);
        var plain = EngramMcpTools.Recall(
            sandbox.Home, new McpSessionId("s9"), Initialized, new LocalRuntime(sandbox.Home), new SessionPinStore(),
            "zanzibar migrations snapshot");

        Assert.Contains($"[{handle}]", typed, StringComparison.Ordinal);
        Assert.Contains($"[{handle}]", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("(session)", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("session · p", plain, StringComparison.Ordinal);
    }

    private static void AssertDegraded(SandboxHome sandbox, string response, string code, string notice)
    {
        var fact = OnlyFact(sandbox);
        Assert.StartsWith("/sessions/", fact.Path, StringComparison.Ordinal);
        Assert.Equal("session", fact.Scope);
        Assert.EndsWith("\n" + notice, response, StringComparison.Ordinal);
        Assert.Equal(1, response.Count(character => character == '\n'));
        Assert.DoesNotContain("[project ", response, StringComparison.Ordinal);
        Assert.Null(LastRemember(sandbox).Scope);
        Assert.Equal(code, LastRemember(sandbox).ProjectUnresolved);
    }

    private static string Remember(
        SandboxHome sandbox, string session, string statement, string? scope = null, string? projectDir = null, string? agent = null) =>
        EngramMcpTools.Remember(
            sandbox.Home, new McpSessionId(session), Initialized, new LocalRuntime(sandbox.Home), statement,
            agent: agent, scope: scope, project_dir: projectDir);

    private static int CountGit(Action action)
    {
        var counter = new StrongBox<int>();
        GitFileLister.InvocationCounter.Value = counter;
        try
        {
            action();
        }
        finally
        {
            GitFileLister.InvocationCounter.Value = null;
        }

        return counter.Value;
    }

    private string IndexedCheckout(SandboxHome sandbox, bool commit = false)
    {
        var repo = Checkout(commit);
        using var connection = Open(sandbox);
        CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(repo, Apply: true, Drain: false, Full: false),
            DateTimeOffset.UtcNow);
        Assert.NotNull(CodeIndexer.LiveRepoPath(connection, CodeIndexer.ResolveIdentity(CodeIndexer.ResolveRoot(repo))));
        return repo;
    }

    private string Checkout(bool commit = false)
    {
        var repo = NewDirectory();
        Assert.True(Git(repo, "init", "-q"), "git init failed");
        Assert.True(Git(repo, "remote", "add", "origin", "https://example.invalid/acme/widget-" + Path.GetFileName(repo) + ".git"));
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        File.WriteAllText(Path.Combine(repo, "src", "Widget.cs"), "namespace Acme; public class Widget { }\n");
        if (commit)
        {
            Assert.True(Git(repo, "add", "."));
            Assert.True(Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-q", "-m", "init"));
        }

        return repo;
    }

    private string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "engram-p4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        scratch.Add(directory);
        return directory;
    }

    private static string ProjectOf(SandboxHome sandbox, string repo)
    {
        using var connection = Open(sandbox);
        var repoPath = CodeIndexer.LiveRepoPath(connection, CodeIndexer.ResolveIdentity(CodeIndexer.ResolveRoot(repo)))!;
        return repoPath.Split('/', StringSplitOptions.RemoveEmptyEntries)[1];
    }

    private sealed record NoteRow(
        long Id, string Handle, string Path, string Kind, string Predicate, string Scope, string LearnedVia, long? SessionId, long? ValidTo);

    private static List<NoteRow> Facts(SandboxHome sandbox)
    {
        using var connection = Open(sandbox);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.id, e.path, e.kind, f.predicate, f.scope, f.learned_via, f.session_id, f.valid_to
            FROM fact f JOIN entity e ON e.id = f.subject_id
            WHERE e.path LIKE '/sessions/%' OR e.path LIKE '/projects/%/notes/%'
            ORDER BY f.id;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<NoteRow>();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            rows.Add(new NoteRow(
                id, FactCatalog.HandleFor(id), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7)));
        }

        return rows;
    }

    private static NoteRow OnlyFact(SandboxHome sandbox) => Assert.Single(Facts(sandbox));

    private static long FactCount(SandboxHome sandbox)
    {
        using var connection = Open(sandbox);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM fact;";
        return (long)command.ExecuteScalar()!;
    }

    private static long SessionRowId(SandboxHome sandbox, string external)
    {
        using var connection = Open(sandbox);
        return SessionStore.FindSession(connection, external)!.Value;
    }

    private static TelemetryRecord LastRemember(SandboxHome sandbox) =>
        File.ReadAllLines(Telemetry.ResolvePath(sandbox.Home))
            .Select(Telemetry.TryParse)
            .Last(record => record?.Kind == TelemetryEventKind.Remember)!;

    private static SqliteConnection Open(SandboxHome sandbox) => EngramDatabase.Open(sandbox.Home);

    private static string HandleOf(string response) => response[1..response.IndexOf(']', StringComparison.Ordinal)];

    private static bool Git(string directory, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in args)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        process.WaitForExit(10_000);
        return process.HasExited && process.ExitCode == 0;
    }
}
