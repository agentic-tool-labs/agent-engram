using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// D94: a git failure is never "no remote" (three-way origin resolution), and a complete full scan
/// retires every other live registration of the same checkout. Real git throughout, except where a
/// cause (a hang, a URL get-url will not return) can only be staged with a stand-in executable.
/// </summary>
public class RegistryRetirementTests
{
    private const string OriginA = "git@example.com:acme/widget-a";
    private const string OriginB = "git@example.com:acme/widget-b";

    internal const string GetUrlGaveNothing = "remote.origin.url is set but 'git remote get-url origin' gave nothing";

    // ---- Phase 1: resolution, one test per branch ------------------------------------------

    [Fact]
    public void Resolve_Remote_IsTheGetUrlValueNormalized()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "remote", OriginA + ".git");

        Assert.True(CodeIndexer.TryResolveIdentity(repo, out var identity, out _));
        Assert.Equal(OriginA, identity);
    }

    [Fact]
    public void Resolve_NotARepository_IsNoRemote_AsBefore()
    {
        using var sandbox = new SandboxHome();
        var plain = PathCanonicalizer.Canonical(Path.Combine(sandbox.Home.Root, "plain"));
        Directory.CreateDirectory(plain);

        Assert.True(CodeIndexer.TryResolveIdentity(plain, out var identity, out _));
        Assert.Equal(plain, identity);
    }

    [Fact]
    public void Resolve_CorruptConfig_IsUnknown()
    {
        using var sandbox = new SandboxHome();
        var repo = CorruptCheckout(sandbox, "corrupt");

        Assert.False(CodeIndexer.TryResolveIdentity(repo, out _, out var failure));
        Assert.Equal("git exited 128", failure);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Resolve_EmptyValue_IsUnknown()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stand-in git is a shell script");
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "empty-value", OriginA);

        // Real git never answers get-url with an empty line (an in-repo origin with an empty url
        // prints the remote's name), so the empty value is staged.
        var git = FakeGit(sandbox, "echo\nexit 0\n");

        Assert.False(CodeIndexer.TryResolveIdentity(repo, git, null, out _, out var failure));
        Assert.Equal(GetUrlGaveNothing, failure);
    }

    [Fact]
    public void Resolve_GitMissing_IsUnknown()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "no-git", OriginA);
        var absent = Path.Combine(sandbox.Home.Root, "no-such-dir", "git");

        Assert.False(CodeIndexer.TryResolveIdentity(repo, absent, null, out _, out var failure));
        Assert.Equal("git did not answer", failure);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Resolve_GitHangs_IsUnknown()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stand-in git is a shell script");
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "hang", OriginA);

        // Closes stdout first so the read returns and the wait is what times out.
        var git = FakeGit(sandbox, "exec >&-\nsleep 5\n");

        Assert.False(CodeIndexer.TryResolveIdentity(repo, git, TimeSpan.FromMilliseconds(200), out _, out var failure));
        Assert.Equal("git did not answer", failure);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Resolve_UrlConfiguredButGetUrlFails_IsUnknown()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stand-in git is a shell script");
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "geturl-fails", OriginA);
        var git = FakeGit(sandbox, "case \"$*\" in\n  *get-url*) exit 2 ;;\n  *) echo " + OriginA + "; exit 0 ;;\nesac\n");

        Assert.False(CodeIndexer.TryResolveIdentity(repo, git, null, out _, out var failure));
        Assert.Equal(GetUrlGaveNothing, failure);
    }

    [Fact]
    public void Resolve_InsteadOf_IdentityIsWhatGetUrlPrints()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "insteadof", "gh:acme/widget");
        RunGit(repo, "config", "url.git@github.com:.insteadOf", "gh:");

        Assert.True(CodeIndexer.TryResolveIdentity(repo, out var identity, out _));
        Assert.Equal(GitOutput(repo, "remote", "get-url", "origin").Trim(), identity);
        Assert.Equal("git@github.com:acme/widget", identity);
    }

    // ---- Phase 1: every caller does nothing on Unknown -------------------------------------

    [Fact]
    public void Index_UnknownOrigin_MintsScansAndRetiresNothing()
    {
        using var sandbox = new SandboxHome();
        var repo = CorruptCheckout(sandbox, "corrupt");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        InsertRow(connection, "/projects/x/code/stale", "stale-identity", repo);

        var report = FullIndex(connection, sandbox, repo, apply: true);

        Assert.Equal([CodeIndexer.UnknownOriginNote(repo, "git exited 128")], report.Notes);
        var only = Assert.Single(Rows(connection));
        Assert.Null(only.DetachedAt);
        Assert.Equal(0, Count(connection, "SELECT COUNT(*) FROM file_state;"));
    }

    [Fact]
    public void IsEnrolled_UnknownOrigin_IsFalse_NotTheRootPathFallback()
    {
        using var sandbox = new SandboxHome();
        var repo = CorruptCheckout(sandbox, "corrupt");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        // Enrolled under the root path with a different last_root, so ByRoot misses and only the
        // identity fallback could answer — and the old fallback would have said yes.
        RepoEnrollment.Enroll(connection, repo, "/somewhere/else", DateTimeOffset.UtcNow);

        Assert.False(RepoEnrollment.IsEnrolled(connection, repo));
    }

    [Fact]
    public void Doctor_UnknownOrigin_WarnsWithoutAFix()
    {
        using var sandbox = new SandboxHome();
        var repo = CorruptCheckout(sandbox, "corrupt");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var diagnosis = Diagnostics.CheckRepo(repo, ConfigFile.Empty, connection);

        Assert.Equal(DiagnosisState.Warn, diagnosis.State);
        Assert.StartsWith("could not resolve this checkout's origin", diagnosis.Detail, StringComparison.Ordinal);
        Assert.Null(diagnosis.Fix);
    }

    // ---- Phase 2: the decision table -------------------------------------------------------

    [Fact]
    public void RemoteRenamed_CompleteScanUnderB_RetiresA()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "renamed", OriginA);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var pathA = FullIndex(connection, sandbox, repo, apply: true).RepoPath;

        RunGit(repo, "remote", "set-url", "origin", OriginB);
        var report = FullIndex(connection, sandbox, repo, apply: true);

        var rows = Rows(connection);
        Assert.NotNull(rows.Single(r => r.RepoPath == pathA).DetachedAt);
        Assert.Null(rows.Single(r => r.RepoPath == report.RepoPath).DetachedAt);
        Assert.Contains($"retired {pathA} ({OriginA}): {repo} is now {report.RepoPath}", report.Notes);
        Assert.Equal(repo, rows.Single(r => r.RepoPath == pathA).DiskPath);
    }

    [Fact]
    public void OriginRemoved_RootIdentityMinted_RetiresA()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "removed", OriginA);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var pathA = FullIndex(connection, sandbox, repo, apply: true).RepoPath;

        RunGit(repo, "remote", "remove", "origin");
        var report = FullIndex(connection, sandbox, repo, apply: true);

        var rows = Rows(connection);
        Assert.Equal(repo, rows.Single(r => r.RepoPath == report.RepoPath).Identity);
        Assert.NotNull(rows.Single(r => r.RepoPath == pathA).DetachedAt);
    }

    [Fact]
    public void ExistingRootPathResidue_RetiredByTheNextScanUnderTheOrigin()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "residue", OriginA);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        InsertRow(connection, "/projects/residue/code/residue-2", repo, repo);

        FullIndex(connection, sandbox, repo, apply: true);

        Assert.NotNull(Rows(connection).Single(r => r.Identity == repo).DetachedAt);
    }

    [Fact]
    public void MovedCheckout_WithOrigin_OneRowFollows_NothingRetired()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "before-move", OriginA);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        FullIndex(connection, sandbox, repo, apply: true);

        var moved = PathCanonicalizer.Canonical(Path.Combine(sandbox.Home.Root, "after-move"));
        Directory.Move(repo, moved);
        FullIndex(connection, sandbox, moved, apply: true);

        var only = Assert.Single(Rows(connection));
        Assert.Equal(moved, only.DiskPath);
        Assert.Null(only.DetachedAt);
    }

    [Fact]
    public void MovedCheckout_WithoutOrigin_OldRowAtAnotherPath_NotRetired()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "np-before", origin: null);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        FullIndex(connection, sandbox, repo, apply: true);

        var moved = PathCanonicalizer.Canonical(Path.Combine(sandbox.Home.Root, "np-after"));
        Directory.Move(repo, moved);
        FullIndex(connection, sandbox, moved, apply: true);

        var rows = Rows(connection);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Null(r.DetachedAt));
    }

    [Fact]
    public void WorktreeAndClone_OfOneRemote_ShareOneRow_NothingRetired()
    {
        using var sandbox = new SandboxHome();
        var main = Checkout(sandbox, "main", OriginA);
        RunGit(main, "add", "-A");
        RunGit(main, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-qm", "fixture");
        var worktree = PathCanonicalizer.Canonical(Path.Combine(sandbox.Home.Root, "wt"));
        RunGit(main, "worktree", "add", "-q", worktree);
        var clone = PathCanonicalizer.Canonical(Path.Combine(sandbox.Home.Root, "clone"));
        RunGit(sandbox.Home.Root, "clone", "-q", main, clone);
        RunGit(clone, "remote", "set-url", "origin", OriginA);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        foreach (var root in new[] { main, worktree, clone, main })
        {
            FullIndex(connection, sandbox, root, apply: true);
        }

        var only = Assert.Single(Rows(connection));
        Assert.Null(only.DetachedAt);
    }

    [Fact]
    public void TruncatedScan_RetiresNothing()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "truncated", OriginB);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        FullIndex(connection, sandbox, repo, apply: true);
        InsertRow(connection, "/projects/x/code/old", OriginA, repo);

        var report = FullIndex(connection, sandbox, repo, apply: true, budget: new ScanBudget(TimeSpan.Zero, 1_000));

        Assert.Contains(report.Notes, n => n.Contains("skipped deletions", StringComparison.Ordinal));
        Assert.All(Rows(connection), r => Assert.Null(r.DetachedAt));
    }

    [Fact]
    public void EmptyScan_RetiresNothing()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "emptied", OriginB);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        FullIndex(connection, sandbox, repo, apply: true);
        InsertRow(connection, "/projects/x/code/old", OriginA, repo);
        foreach (var file in Directory.GetFiles(repo))
        {
            File.Delete(file);
        }

        var report = FullIndex(connection, sandbox, repo, apply: true);

        Assert.Contains(report.Notes, n => n.StartsWith("scan found 0 files", StringComparison.Ordinal));
        Assert.All(Rows(connection), r => Assert.Null(r.DetachedAt));
    }

    [Fact]
    public void Drain_RetiresNothing()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "drained", OriginB);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        FullIndex(connection, sandbox, repo, apply: true);
        InsertRow(connection, "/projects/x/code/old", OriginA, repo);

        var report = CodeIndexer.Index(
            connection, sandbox.Home, ConfigFile.Empty, IndexingSettings.Default,
            new IndexOptions(repo, Apply: true, Drain: true, Full: false, AllowFullScanDue: false, Queue: SpoolQueue.Empty),
            DateTimeOffset.UtcNow);

        Assert.False(report.FullScan);
        Assert.All(Rows(connection), r => Assert.Null(r.DetachedAt));
    }

    [Fact]
    public void DryRun_WritesNothing_AndSaysWhatItWouldRetire()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "dry", OriginB);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var pathB = FullIndex(connection, sandbox, repo, apply: true).RepoPath;
        InsertRow(connection, "/projects/x/code/old", OriginA, repo);

        var report = FullIndex(connection, sandbox, repo, apply: false);

        Assert.Contains($"would retire /projects/x/code/old (superseded by {pathB})", report.Notes);
        Assert.All(Rows(connection), r => Assert.Null(r.DetachedAt));
    }

    [Fact]
    public void SameCheckoutUnderAnotherSpelling_IsRetired()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinked directories need elevation on Windows");
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "spelled", OriginB);
        var alias = Path.Combine(sandbox.Home.Root, "alias-of-spelled");
        Directory.CreateSymbolicLink(alias, repo);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        InsertRow(connection, "/projects/x/code/old", OriginA, alias);

        FullIndex(connection, sandbox, repo, apply: true);

        Assert.NotNull(Rows(connection).Single(r => r.RepoPath == "/projects/x/code/old").DetachedAt);
    }

    [Fact]
    public void ARowAtAPathSharingOnlyAPrefix_IsNotRetired()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "repo", OriginB);
        var sibling = PathCanonicalizer.Canonical(Path.Combine(sandbox.Home.Root, "repo2"));
        Directory.CreateDirectory(sibling);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        InsertRow(connection, "/projects/x/code/sibling", OriginA, sibling);

        FullIndex(connection, sandbox, repo, apply: true);

        Assert.Null(Rows(connection).Single(r => r.RepoPath == "/projects/x/code/sibling").DetachedAt);
    }

    [Fact]
    public void RemotePointedBack_AtTheSameCheckout_UnRetiresA_AndTheScanRetiresB()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "back", OriginA);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var pathA = FullIndex(connection, sandbox, repo, apply: true).RepoPath;
        RunGit(repo, "remote", "set-url", "origin", OriginB);
        var pathB = FullIndex(connection, sandbox, repo, apply: true).RepoPath;
        Assert.NotNull(Rows(connection).Single(r => r.RepoPath == pathA).DetachedAt);

        RunGit(repo, "remote", "set-url", "origin", OriginA);
        FullIndex(connection, sandbox, repo, apply: true);

        var rows = Rows(connection);
        Assert.Null(rows.Single(r => r.RepoPath == pathA).DetachedAt);
        Assert.NotNull(rows.Single(r => r.RepoPath == pathB).DetachedAt);
    }

    [Fact]
    public void ARetiredRowThatIsNotTheResolvedOne_StaysRetired()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "stays", OriginA);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var pathA = FullIndex(connection, sandbox, repo, apply: true).RepoPath;
        RunGit(repo, "remote", "set-url", "origin", OriginB);
        FullIndex(connection, sandbox, repo, apply: true);
        var retiredAt = Rows(connection).Single(r => r.RepoPath == pathA).DetachedAt;
        Assert.NotNull(retiredAt);

        FullIndex(connection, sandbox, repo, apply: true);

        Assert.Equal(retiredAt, Rows(connection).Single(r => r.RepoPath == pathA).DetachedAt);
    }

    [Fact]
    public void RemotePointedBack_DryRun_ClearsNothing()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "dry-back", OriginA);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var pathA = FullIndex(connection, sandbox, repo, apply: true).RepoPath;
        RunGit(repo, "remote", "set-url", "origin", OriginB);
        FullIndex(connection, sandbox, repo, apply: true);
        var before = Rows(connection);

        RunGit(repo, "remote", "set-url", "origin", OriginA);
        FullIndex(connection, sandbox, repo, apply: false);

        Assert.Equal(before, Rows(connection));
        Assert.NotNull(before.Single(r => r.RepoPath == pathA).DetachedAt);
    }

    [Fact]
    public void Retirement_KeysOnTheResolvedIdentity_NeverTheEnrolledOne()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "split", OriginA);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var pathA = FullIndex(connection, sandbox, repo, apply: true).RepoPath;
        RunGit(repo, "remote", "set-url", "origin", OriginB);

        // Freshen passes the enrollment row's identity (A) while the scan resolves B.
        CodeIndexer.Index(
            connection, sandbox.Home, ConfigFile.Empty, IndexingSettings.Default,
            new IndexOptions(repo, Apply: true, Drain: false, Full: true, EnrolledIdentity: OriginA),
            DateTimeOffset.UtcNow);

        Assert.NotNull(Rows(connection).Single(r => r.RepoPath == pathA).DetachedAt);
    }

    [Fact]
    public void Resolver_AfterRetirement_ResolvesToTheLivePathOnly()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "resolver", OriginA);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        FullIndex(connection, sandbox, repo, apply: true);
        RunGit(repo, "remote", "set-url", "origin", OriginB);
        var pathB = FullIndex(connection, sandbox, repo, apply: true).RepoPath;

        var resolved = CodeEntityResolver.Resolve(connection, Path.Combine(repo, "README.md"));

        Assert.NotNull(resolved);
        Assert.Equal(pathB, resolved.Value.RepoPath);
    }

    [Fact]
    public void Retirement_DeletesNothing_AndLeavesEnrollmentAlone()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "keeps", OriginA);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        RepoEnrollment.Enroll(connection, OriginA, repo, DateTimeOffset.UtcNow);
        FullIndex(connection, sandbox, repo, apply: true);
        var before = Snapshot(connection);
        var enrollmentBefore = RepoEnrollment.ListAll(connection);

        RunGit(repo, "remote", "set-url", "origin", OriginB);
        FullIndex(connection, sandbox, repo, apply: true);

        var after = Snapshot(connection);
        Assert.True(before.LiveUnderA > 0);
        Assert.Equal(before.LiveUnderA, after.LiveUnderA);
        Assert.Equal(before.FileStatesUnderA, after.FileStatesUnderA);
        Assert.True(after.Entities >= before.Entities);
        Assert.Equal(enrollmentBefore, RepoEnrollment.ListAll(connection).Where(r => r.Identity == OriginA).ToList());
    }

    // ---- Phase 2: doctor -------------------------------------------------------------------

    [Fact]
    public void Doctor_RetiredPathsHoldingFiles_Warn_AndNoneMeansNoRow()
    {
        using var sandbox = new SandboxHome();
        var repo = Checkout(sandbox, "doctor", OriginA);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        FullIndex(connection, sandbox, repo, apply: true);

        Assert.Null(Diagnostics.CheckRetiredPaths(connection));

        RunGit(repo, "remote", "set-url", "origin", OriginB);
        FullIndex(connection, sandbox, repo, apply: true);
        var files = Count(connection,
            "SELECT COUNT(*) FROM file_state f JOIN repo_registry r ON r.repo_path = f.repo_path WHERE r.detached_at IS NOT NULL;");

        var diagnosis = Diagnostics.CheckRetiredPaths(connection);

        Assert.NotNull(diagnosis);
        Assert.Equal(DiagnosisState.Warn, diagnosis.State);
        Assert.Equal(
            $"1 superseded registry path(s) still hold indexed files ({files} file states); "
                + "they are excluded from code lookup and will be removable in a later version",
            diagnosis.Detail);
        Assert.Null(diagnosis.Fix);
        Assert.True(files > 0);
    }

    [Fact]
    public void Doctor_RetiredCheck_ToleratesAStoreWithoutTheTables()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        Assert.Null(Diagnostics.CheckRetiredPaths(connection));
    }

    // ---- helpers ---------------------------------------------------------------------------

    private sealed record Row(string RepoPath, string Identity, string? DiskPath, long? DetachedAt);

    private sealed record Counts(long LiveUnderA, long FileStatesUnderA, long Entities);

    private static Counts Snapshot(SqliteConnection connection) => new(
        Count(connection, "SELECT COUNT(*) FROM fact WHERE valid_to IS NULL AND path LIKE '/projects/keeps/code/widget-a%';"),
        Count(connection, "SELECT COUNT(*) FROM file_state WHERE repo_path = '/projects/keeps/code/widget-a';"),
        Count(connection, "SELECT COUNT(*) FROM entity;"));

    internal static string Checkout(SandboxHome sandbox, string name, string? origin)
    {
        var repo = Path.Combine(sandbox.Home.Root, name);
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "README.md"), "# Widget\n\nText.\n");
        File.WriteAllText(Path.Combine(repo, "Program.cs"), "public sealed class Widget { }\n");
        RunGit(repo, "init", "-q");
        if (origin is not null)
        {
            RunGit(repo, "remote", "add", "origin", origin);
        }

        return PathCanonicalizer.Canonical(repo);
    }

    private static string CorruptCheckout(SandboxHome sandbox, string name)
    {
        var repo = Checkout(sandbox, name, OriginA);
        File.AppendAllText(Path.Combine(repo, ".git", "config"), "[[[broken\n");
        return repo;
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static string FakeGit(SandboxHome sandbox, string body)
    {
        var path = Path.Combine(sandbox.Home.Root, $"fake-git-{Guid.NewGuid():N}");
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static IndexReport FullIndex(
        SqliteConnection connection, SandboxHome sandbox, string repo, bool apply, ScanBudget? budget = null) =>
        CodeIndexer.Index(
            connection, sandbox.Home, ConfigFile.Empty, IndexingSettings.Default,
            new IndexOptions(repo, apply, Drain: false, Full: true, Budget: budget),
            DateTimeOffset.UtcNow);

    private static void InsertRow(SqliteConnection connection, string repoPath, string identity, string diskPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO repo_registry (repo_path, identity, disk_path, created_at) VALUES ($p, $i, $d, 1);";
        command.Parameters.AddWithValue("$p", repoPath);
        command.Parameters.AddWithValue("$i", identity);
        command.Parameters.AddWithValue("$d", diskPath);
        command.ExecuteNonQuery();
    }

    private static List<Row> Rows(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT repo_path, identity, disk_path, detached_at FROM repo_registry;";
        using var reader = command.ExecuteReader();
        var rows = new List<Row>();
        while (reader.Read())
        {
            rows.Add(new Row(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3)));
        }

        return rows;
    }

    private static long Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    internal static IEnumerable<string> IndexEvents(SandboxHome sandbox) =>
        File.Exists(Telemetry.ResolvePath(sandbox.Home))
            ? File.ReadAllLines(Telemetry.ResolvePath(sandbox.Home)).Where(l => l.Contains("\"kind\":\"index\"", StringComparison.Ordinal))
            : [];

    internal static void RunGit(string directory, params string[] args) =>
        Assert.True(TryGit(directory, args, out _), $"git {string.Join(' ', args)} failed in {directory}");

    internal static string GitOutput(string directory, params string[] args)
    {
        Assert.True(TryGit(directory, args, out var output), $"git {string.Join(' ', args)} failed in {directory}");
        return output;
    }

    private static bool TryGit(string directory, string[] args, out string output)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git")
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

        using var process = System.Diagnostics.Process.Start(info)!;
        output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(20_000);
        return process.HasExited && process.ExitCode == 0;
    }
}
