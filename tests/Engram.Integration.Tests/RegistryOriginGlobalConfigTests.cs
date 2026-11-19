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
