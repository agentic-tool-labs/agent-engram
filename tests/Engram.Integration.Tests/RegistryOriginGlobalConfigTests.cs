using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// The D94 cases that only arise from git's <em>global</em> configuration, so each test points
/// <c>GIT_CONFIG_GLOBAL</c> at its own file. That variable is process-wide and the commands under
/// test run in-process, hence the serial collection: a parallel class would resolve its own
/// fixtures against this file.
/// </summary>
/// <remarks>
/// A section in the repository's own config does not reproduce these: there git treats origin as
/// configured and <c>remote get-url</c> prints the remote's name, <c>origin</c>, exit 0.
/// </remarks>
[Collection(GitGlobalConfigCollection.Name)]
public sealed class RegistryOriginGlobalConfigTests
{
    [Fact]
    public void GlobalOriginSectionWithoutUrl_IsNoRemote()
    {
        using var sandbox = new SandboxHome();
        var repo = RegistryRetirementTests.Checkout(sandbox, "tank-shape", origin: null);
        using var global = GlobalConfig(sandbox, "[remote \"origin\"]\n\tprune = true\n");

        // The tank shape: `git remote` lists origin in every repository.
        Assert.Contains("origin", RegistryRetirementTests.GitOutput(repo, "remote"), StringComparison.Ordinal);

        Assert.True(CodeIndexer.TryResolveIdentity(repo, out var identity, out _));
        Assert.Equal(repo, identity);
    }

    [Fact]
    public void RepoEnroll_UnknownOrigin_ExitsNonZeroAndWritesNothing()
    {
        using var sandbox = new SandboxHome();
        var repo = UnknownCheckout(sandbox, "enroll-unknown", out var global);
        using var _ = global;
        var stderr = new StringWriter();

        var exit = RepoCommand.Run(sandbox.Home.Root, ["enroll", repo], new StringWriter(), stderr);

        Assert.Equal(1, exit);
        Assert.Contains(CodeIndexer.UnknownOriginNote(repo, RegistryRetirementTests.GetUrlGaveNothing), stderr.ToString(), StringComparison.Ordinal);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Empty(RepoEnrollment.ListAll(connection));
    }

    [Fact]
    public void IndexCommand_UnknownOrigin_ExitsNonZero_AndRecordsNoIndexEvent()
    {
        using var sandbox = new SandboxHome();
        var repo = UnknownCheckout(sandbox, "index-unknown", out var global);
        using var _ = global;
        var stdout = new StringWriter();

        var exit = IndexCommand.Run(sandbox.Home.Root, ["--full", "--apply", repo], stdout, new StringWriter());

        Assert.Equal(1, exit);
        Assert.Contains(CodeIndexer.UnknownOriginNote(repo, RegistryRetirementTests.GetUrlGaveNothing), stdout.ToString(), StringComparison.Ordinal);
        Assert.Empty(RegistryRetirementTests.IndexEvents(sandbox));
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Equal(0L, (long)Scalar(connection, "SELECT COUNT(*) FROM repo_registry;"));
    }

    [Fact]
    public void IndexCommand_Auto_UnknownOrigin_IsASilentRefusal()
    {
        using var sandbox = new SandboxHome();
        var repo = UnknownCheckout(sandbox, "auto-unknown", out var global);
        using var _ = global;
        var stdout = new StringWriter();

        var exit = IndexCommand.Run(sandbox.Home.Root, ["--auto", "--apply", repo], stdout, new StringWriter());

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Empty(RegistryRetirementTests.IndexEvents(sandbox));
    }

    [Fact]
    public void RepoIndexAll_UnknownOrigin_SkipsTheRoot_ExitsNonZero_AndRecordsNoIndexEvent()
    {
        using var sandbox = new SandboxHome();
        var repo = EnrolledUnknownCheckout(sandbox, "all-unknown", out var global);
        using var _ = global;
        var stdout = new StringWriter();

        var exit = RepoCommand.Run(sandbox.Home.Root, ["index", "--all", "--apply"], stdout, new StringWriter());

        Assert.Equal(1, exit);
        Assert.Contains(CodeIndexer.UnknownOriginNote(repo, RegistryRetirementTests.GetUrlGaveNothing), stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("1 skipped (origin unknown)", stdout.ToString(), StringComparison.Ordinal);
        AssertNothingIndexed(sandbox);
    }

    [Fact]
    public void IndexFreshen_UnknownOrigin_IsSilent_AndIndexesNothing()
    {
        using var sandbox = new SandboxHome();
        EnrolledUnknownCheckout(sandbox, "freshen-unknown", out var global);
        using var _ = global;
        var stdout = new StringWriter();

        var exit = IndexCommand.Run(sandbox.Home.Root, ["--freshen", "--apply"], stdout, new StringWriter());

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        AssertNothingIndexed(sandbox);
    }

    [Fact]
    public void ServerFreshnessTick_UnknownOrigin_IndexesNothing()
    {
        using var sandbox = new SandboxHome();
        var repo = EnrolledUnknownCheckout(sandbox, "tick-unknown", out var global);
        using var _ = global;
        var log = new List<string>();

        new IndexFreshness(sandbox.Home, log.Add).TickOnce();

        Assert.Contains(CodeIndexer.UnknownOriginNote(repo, RegistryRetirementTests.GetUrlGaveNothing), log);
        AssertNothingIndexed(sandbox);
    }

    [Fact]
    public void DrainAll_UnknownSecondaryRoot_IsSkipped_WithNoEventForIt()
    {
        using var sandbox = new SandboxHome();
        var secondary = EnrolledUnknownCheckout(sandbox, "secondary-unknown", out var global);
        using var _ = global;

        // Any local origin works: the global url is appended to it, and get-url answers.
        var primary = RegistryRetirementTests.Checkout(sandbox, "primary", "git@example.com:acme/primary");

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = IndexCommand.Run(sandbox.Home.Root, ["--drain-all", "--apply", primary], stdout, stderr);

        Assert.True(exit == 0, $"{stdout}\n{stderr}");
        // Every index event is the primary's: the secondary started nothing, under any name.
        var primaryIdentity = CodeIndexer.ResolveIdentity(primary);
        Assert.NotEmpty(RegistryRetirementTests.IndexEvents(sandbox));
        Assert.All(
            RegistryRetirementTests.IndexEvents(sandbox),
            l => Assert.Contains($"\"repo\":\"{primaryIdentity}\"", l, StringComparison.Ordinal));
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Equal(1L, (long)Scalar(connection, "SELECT COUNT(*) FROM repo_registry;"));
        Assert.Equal(0L, (long)Scalar(connection, $"SELECT COUNT(*) FROM repo_registry WHERE disk_path = '{secondary}';"));
        Assert.Null(RepoEnrollment.Get(connection, EnrolledIdentity)!.LastFullScanAt);
    }

    [Fact]
    public void McpIndexRepo_UnknownOrigin_RecordsNothing()
    {
        using var sandbox = new SandboxHome();
        var repo = UnknownCheckout(sandbox, "mcp-unknown", out var global);
        using var _ = global;

        var response = EngramMcpTools.IndexRepo(sandbox.Home, new McpSessionId("session-unknown"), repo, "enroll");

        Assert.StartsWith(CodeIndexer.UnknownOriginNote(repo, RegistryRetirementTests.GetUrlGaveNothing), response, StringComparison.Ordinal);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Empty(RepoEnrollment.ListAll(connection));
    }

    [Fact]
    public void ServerFreshnessTick_AnUnknownFirstPick_DoesNotConsumeTheTick()
    {
        using var sandbox = new SandboxHome();
        var unknown = EnrolledUnknownCheckout(sandbox, "tick-first-unknown", out var global);
        using var _ = global;
        var next = EnrollResolvable(sandbox, "tick-second");

        new IndexFreshness(sandbox.Home).TickOnce();

        AssertOnlyIndexed(sandbox, next, unknown);
    }

    [Fact]
    public void IndexFreshen_AnUnknownFirstPick_DoesNotConsumeThePass()
    {
        using var sandbox = new SandboxHome();
        var unknown = EnrolledUnknownCheckout(sandbox, "freshen-first-unknown", out var global);
        using var _ = global;
        var next = EnrollResolvable(sandbox, "freshen-second");

        var exit = IndexCommand.Run(sandbox.Home.Root, ["--freshen", "--apply"], new StringWriter(), new StringWriter());

        Assert.Equal(0, exit);
        AssertOnlyIndexed(sandbox, next, unknown);
    }

    /// <summary>
    /// A due root that resolves under the global file too (a local origin, which get-url answers),
    /// enrolled after the Unknown one so the most-neglected-first order picks the Unknown one first.
    /// </summary>
    private static string EnrollResolvable(SandboxHome sandbox, string name)
    {
        var repo = RegistryRetirementTests.Checkout(sandbox, name, $"git@example.com:acme/{name}");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        RepoEnrollment.Enroll(connection, CodeIndexer.ResolveIdentity(repo), repo, DateTimeOffset.UtcNow.AddDays(-1));
        return repo;
    }

    private static void AssertOnlyIndexed(SandboxHome sandbox, string indexed, string skipped)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Equal(1L, (long)Scalar(connection, $"SELECT COUNT(*) FROM repo_registry WHERE disk_path = '{indexed}';"));
        Assert.Equal(0L, (long)Scalar(connection, $"SELECT COUNT(*) FROM repo_registry WHERE disk_path = '{skipped}';"));
        Assert.Null(RepoEnrollment.Get(connection, EnrolledIdentity)!.LastFullScanAt);
    }

    private const string EnrolledIdentity = "git@example.com:acme/enrolled-earlier";

    /// <summary>Enrolled under an identity it once resolved to, never scanned, so it is due.</summary>
    private static string EnrolledUnknownCheckout(SandboxHome sandbox, string name, out IDisposable global)
    {
        var repo = UnknownCheckout(sandbox, name, out global);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        RepoEnrollment.Enroll(connection, EnrolledIdentity, repo, DateTimeOffset.UtcNow.AddDays(-2));
        return repo;
    }

    private static void AssertNothingIndexed(SandboxHome sandbox)
    {
        Assert.Empty(RegistryRetirementTests.IndexEvents(sandbox));
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Equal(0L, (long)Scalar(connection, "SELECT COUNT(*) FROM repo_registry;"));
        Assert.Null(RepoEnrollment.Get(connection, EnrolledIdentity)!.LastFullScanAt);
    }

    /// <summary>
    /// A real-git Unknown that leaves the checkout readable: the URL lives only in the global
    /// file, so config answers it (exit 0) while get-url says there is no such remote (exit 2).
    /// A corrupt config would also be Unknown, but rev-parse fails on it too, and the verbs would
    /// refuse earlier as "not inside a git checkout" — proving nothing about this rule.
    /// </summary>
    private static string UnknownCheckout(SandboxHome sandbox, string name, out IDisposable global)
    {
        var repo = RegistryRetirementTests.Checkout(sandbox, name, origin: null);
        global = GlobalConfig(sandbox, "[remote \"origin\"]\n\turl = git@example.com:acme/global-only\n");
        return repo;
    }

    private static IDisposable GlobalConfig(SandboxHome sandbox, string body)
    {
        var path = Path.Combine(sandbox.Home.Root, $"global-{Guid.NewGuid():N}.gitconfig");
        File.WriteAllText(path, body);
        var previous = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", path);
        return new Restore(() => Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", previous));
    }

    private static object Scalar(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GitGlobalConfigCollection
{
    public const string Name = "git-global-config";
}
