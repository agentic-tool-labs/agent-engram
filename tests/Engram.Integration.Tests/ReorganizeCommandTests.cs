using System.Net;
using System.Net.Sockets;
using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// <c>engram reorganize</c> (D87), driven in process. One test per row of the P3 invariant table
/// that tier 2 can reach; the published-binary rows are in <c>Engram.EndToEnd.Tests</c>.
/// </summary>
public class ReorganizeCommandTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoStep_PrintsUsage_AndNeverOpensTheStore()
    {
        using var sandbox = new SandboxHome(initialize: false);

        var (exit, _, stderr) = Run(sandbox);

        Assert.Equal(1, exit);
        Assert.Contains("usage: engram reorganize", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.Home.DatabasePath));
    }

    [Fact]
    public void Ledger_DryRun_ListsEveryFact_AndChangesNoFile()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Ledger(connection, "/ah2/repo/t/train/1", "train one opened");
            Ledger(connection, "/ah2/repo/t/train/2", "train two opened");
        }

        SqliteConnection.ClearAllPools();
        var before = FileSnapshot(sandbox.Home.Root);

        var (exit, stdout, _) = Run(sandbox, "--ledger");

        SqliteConnection.ClearAllPools();
        Assert.Equal(0, exit);
        Assert.Contains("ledger: 2 live fact(s) under /ah2 → /tools/ah2", stdout, StringComparison.Ordinal);
        Assert.Contains("/ah2/repo/t/train/1 → /tools/ah2/repo/t/train/1", stdout, StringComparison.Ordinal);
        Assert.Equal(before, FileSnapshot(sandbox.Home.Root));
    }

    [Fact]
    public void Ledger_Apply_WhileAServerIsAlive_RefusesAndWritesNothing()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Ledger(connection, "/ah2/repo/t/train/1", "train one opened");
        }

        // This process, recorded as the server, on a port nothing answers: alive and not answering
        // is Wedged, which ServerIsAlive counts — no real server is started.
        PidFile.Write(sandbox.Home, new PidFileRecord(
            Environment.ProcessId, FreePort(), EngramVersion.Current, DateTimeOffset.UtcNow, ProcessStartToken.ForSelf()));
        SqliteConnection.ClearAllPools();
        var before = FileSnapshot(sandbox.Home.Root);

        var (exit, _, stderr) = Run(sandbox, "--ledger", "--apply");

        SqliteConnection.ClearAllPools();
        Assert.Equal(1, exit);
        Assert.Contains("server is running", stderr, StringComparison.Ordinal);
        Assert.Equal(before, FileSnapshot(sandbox.Home.Root));
    }

    [Fact]
    public void Ledger_ApplyTwice_ReFilesOnce_ThenFindsNothing()
    {
        using var sandbox = new SandboxHome();
        long source;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            source = Ledger(connection, "/ah2/repo/t/train/1", "train one opened");
        }

        var first = Run(sandbox, "--ledger", "--apply");
        var second = Run(sandbox, "--ledger", "--apply");

        Assert.Equal(0, first.Exit);
        Assert.Contains("re-filed 1,", first.Stdout, StringComparison.Ordinal);
        Assert.Contains("snapshot: ", first.Stdout, StringComparison.Ordinal);
        Assert.Equal(0, second.Exit);
        Assert.Contains("ledger: 0 live fact(s)", second.Stdout, StringComparison.Ordinal);
        Assert.Contains("re-filed 0", second.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot: ", second.Stdout, StringComparison.Ordinal);

        using var check = EngramDatabase.Open(sandbox.Home);
        Assert.NotNull(FactStore.ReadById(check, source)!.ValidTo);
        var moved = FactStore.ReadAt(check, "/tools/ah2/repo/t/train/1").Single();
        Assert.Equal(("tool", "observed", "train one opened"), (moved.Scope, moved.LearnedVia, moved.Body));
    }

    [Fact]
    public void Ledger_SelectsOnTheSubtreeBoundary_AndOnlyLiveFacts()
    {
        using var sandbox = new SandboxHome();
        long sibling, closed, hashChild, directive, knowledge;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Ledger(connection, "/ah2/repo/t/train/1", "selected");
            hashChild = Ledger(connection, "/ah2#anchor", "selected via #");
            sibling = Ledger(connection, "/ah2x/repo/t/train/1", "a sibling root, not under /ah2");
            closed = Ledger(connection, "/ah2/repo/t/closed", "closed before the run");
            FactStore.Forget(connection, closed, "test", T0);
            directive = DirectiveFacts.Add(connection, "Mention ah2 when relevant.", T0);
            knowledge = LiveSeededUserFactUnder(connection, "/knowledge");
        }

        var (exit, stdout, _) = Run(sandbox, "--ledger", "--apply");

        Assert.Equal(0, exit);
        Assert.Contains("re-filed 2,", stdout, StringComparison.Ordinal);
        using var check = EngramDatabase.Open(sandbox.Home);
        Assert.NotNull(FactStore.ReadById(check, hashChild)!.ValidTo);
        Assert.Null(FactStore.ReadById(check, sibling)!.ValidTo);
        Assert.Null(FactStore.ReadById(check, directive)!.ValidTo);
        Assert.Null(FactStore.ReadById(check, knowledge)!.ValidTo);

        var stillClosed = FactStore.ReadById(check, closed)!;
        Assert.Equal(("/ah2/repo/t/closed", (long?)null), (stillClosed.SubjectPath, stillClosed.SupersededBy));
        Assert.Empty(FactStore.ReadAt(check, "/tools/ah2/repo/t/closed"));
    }

    [Fact]
    public void Ledger_ToUnderFrom_IsRefused()
    {
        using var sandbox = new SandboxHome();

        var (exit, _, stderr) = Run(sandbox, "--ledger", "--from", "/ah2", "--to", "/ah2/moved");

        Assert.Equal(1, exit);
        Assert.Contains("--to may not lie under --from", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Ledger_AfterApply_ReplayOfAnEarlierJournalReopensNothing()
    {
        using var sandbox = new SandboxHome();
        IReadOnlyList<JournalFact> journal;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Ledger(connection, "/ah2/repo/t/train/1", "train one opened");
            Ledger(connection, "/ah2/repo/t/train/2", "train two opened");
            journal = FactJournal.Read(connection).ToList();
        }

        Assert.Equal(0, Run(sandbox, "--ledger", "--apply").Exit);

        using var check = EngramDatabase.OpenInitialized(sandbox.Home);
        var before = LiveByScope(check);
        var result = FactJournal.Replay(check, journal, apply: true);

        Assert.Equal(0, result.Written);
        Assert.Equal(before, LiveByScope(check));
        Assert.Empty(FactStore.ReadSubtree(check, "/ah2"));
        Assert.Equal(2, FactStore.ReadSubtree(check, "/tools/ah2").Count);
    }

    [Fact]
    public void Ledger_AfterApply_RepairFindsNoTokenOrFtsDrift()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Ledger(connection, "/ah2/repo/t/train/1", "zanzibar train opened");
        }

        Assert.Equal(0, Run(sandbox, "--ledger", "--apply").Exit);

        using var check = EngramDatabase.OpenInitialized(sandbox.Home);
        var report = StoreRepairer.Repair(check, sandbox.Home, apply: false, DateTimeOffset.UtcNow);
        Assert.False(report.TokenIndexNeedsRebuild, $"token missing {report.TokenMissing}, extra {report.TokenExtra}");
        Assert.False(report.FtsNeedsRebuild, $"fts missing {report.FtsMissing}, extra {report.FtsExtra}");
    }

    [Theory]
    [InlineData("I prefer dark roast coffee.")]
    [InlineData("I use `jq` daily")]
    [InlineData("Subagent output should be terse.")]
    [InlineData("Verifying things myself is how I learn.")]
    [InlineData("done is better than perfect")]
    [InlineData("meet me at 10:30 tomorrow")]
    public void Rules_NearMisses_AreNotSuspects(string body) =>
        Assert.Empty(ReorganizeCommand.Rules(body, new HashSet<string>()));

    [Theory]
    [InlineData("Verified the build passes.", "R1")]
    [InlineData("DONE wiring the flag", "R1")]
    [InlineData("see [hierarchy-msg 2026] for it", "R2")]
    [InlineData("the ah2 ledger", "R2")]
    [InlineData("[Subagent: reviewer] said so", "R2")]
    [InlineData("my entry point is Program.cs:12", "R3")]
    [InlineData("look at src/a/b.ts:12 first", "R3")]
    public void Rules_Hits_NameTheRule(string body, string rule) =>
        Assert.Contains(rule, ReorganizeCommand.Rules(body, new HashSet<string>()));

    [Fact]
    public void Rules_LengthBoundary_400IsNotR4_401Is()
    {
        Assert.DoesNotContain("R4", ReorganizeCommand.Rules(new string('a', 400), new HashSet<string>()));
        Assert.Contains("R4", ReorganizeCommand.Rules(new string('a', 401), new HashSet<string>()));
    }

    [Fact]
    public void Captures_DryRun_SplitsTiers_AndChangesNoFile()
    {
        using var sandbox = new SandboxHome();
        long agent, citation, longProse, plain, duplicate;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            agent = Capture(connection, "Verified the fix against the rebuilt binary.");
            citation = Capture(connection, "my entry point is Program.cs:12");
            longProse = Capture(connection, string.Join(' ', Enumerable.Repeat("words", 70)));
            plain = Capture(connection, "I prefer dark roast coffee.");
            SessionFacts.Append(connection, "s1", "Copied from my own note.", null, null, null, T0);
            duplicate = Capture(connection, "Copied from my own note.");
        }

        SqliteConnection.ClearAllPools();
        var before = FileSnapshot(sandbox.Home.Root);
        var (exit, stdout, _) = Run(sandbox, "--captures");
        SqliteConnection.ClearAllPools();

        Assert.Equal(0, exit);
        Assert.Equal(before, FileSnapshot(sandbox.Home.Root));
        var tierA = Section(stdout, "tier A — closable as a set:", "tier B — closable only by handle:");
        var tierB = Section(stdout, "tier B — closable only by handle:", "to act:");
        Assert.Contains($"f{agent} ", tierA, StringComparison.Ordinal);
        Assert.Contains($"f{duplicate} ", tierA, StringComparison.Ordinal);
        Assert.Contains($"f{citation} ", tierB, StringComparison.Ordinal);
        Assert.Contains($"f{longProse} ", tierB, StringComparison.Ordinal);
        Assert.DoesNotContain($"f{plain} ", stdout, StringComparison.Ordinal);
        Assert.Contains("engram reorganize --captures --apply --tier a", stdout, StringComparison.Ordinal);
        Assert.Contains("engram reorganize --captures --apply --only f…,f…", stdout, StringComparison.Ordinal);

        // Longest pasted prose first within a rule: R4 after R3, and the R3 row is listed first.
        Assert.True(tierB.IndexOf($"f{citation} ", StringComparison.Ordinal) < tierB.IndexOf($"f{longProse} ", StringComparison.Ordinal));
    }

    [Fact]
    public void Captures_ApplyTierA_ClosesTierA_AndNothingElse()
    {
        using var sandbox = new SandboxHome();
        long agent, citation, plain;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            agent = Capture(connection, "Verified the fix against the rebuilt binary.");
            citation = Capture(connection, "my entry point is Program.cs:12");
            plain = Capture(connection, "I prefer dark roast coffee.");
        }

        var (exit, stdout, _) = Run(sandbox, "--captures", "--apply", "--tier", "a");

        Assert.Equal(0, exit);
        Assert.Contains($"closed 1: f{agent}", stdout, StringComparison.Ordinal);
        using var check = EngramDatabase.Open(sandbox.Home);
        Assert.NotNull(FactStore.ReadById(check, agent)!.ValidTo);
        Assert.Null(FactStore.ReadById(check, citation)!.ValidTo);
        Assert.Null(FactStore.ReadById(check, plain)!.ValidTo);
        Assert.Equal("misfiled capture: not the user's own words", ReadReason(check, agent));
    }

    [Fact]
    public void Captures_ApplyOnly_ClosesExactlyTheNamedSuspect()
    {
        using var sandbox = new SandboxHome();
        long citation, longProse;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            citation = Capture(connection, "my entry point is Program.cs:12");
            longProse = Capture(connection, new string('a', 401));
        }

        var (exit, _, _) = Run(sandbox, "--captures", "--apply", "--only", $"f{citation}");

        Assert.Equal(0, exit);
        using var check = EngramDatabase.Open(sandbox.Home);
        Assert.NotNull(FactStore.ReadById(check, citation)!.ValidTo);
        Assert.Null(FactStore.ReadById(check, longProse)!.ValidTo);
    }

    [Theory]
    [InlineData("--captures", "--apply")]
    [InlineData("--captures", "--apply", "--tier", "a", "--only", "f1")]
    [InlineData("--captures", "--apply", "--tier", "b")]
    public void Captures_ApplyWithoutExactlyOneSelector_IsRefused_AndWritesNothing(params string[] args)
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Capture(connection, "Verified the fix against the rebuilt binary.");
        }

        SqliteConnection.ClearAllPools();
        var before = FileSnapshot(sandbox.Home.Root);
        var (exit, _, _) = Run(sandbox, args);
        SqliteConnection.ClearAllPools();

        Assert.Equal(1, exit);
        Assert.Equal(before, FileSnapshot(sandbox.Home.Root));
    }

    [Fact]
    public void Captures_ApplyOnly_NamingANonSuspect_IsRefused_AndWritesNothing()
    {
        using var sandbox = new SandboxHome();
        long plain, agent;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            agent = Capture(connection, "Verified the fix against the rebuilt binary.");
            plain = Capture(connection, "I prefer dark roast coffee.");
        }

        SqliteConnection.ClearAllPools();
        var before = FileSnapshot(sandbox.Home.Root);
        var (exit, _, stderr) = Run(sandbox, "--captures", "--apply", "--only", $"f{agent},f{plain}");
        SqliteConnection.ClearAllPools();

        Assert.Equal(1, exit);
        Assert.Contains($"f{plain}", stderr, StringComparison.Ordinal);
        Assert.Equal(before, FileSnapshot(sandbox.Home.Root));
    }

    [Fact]
    public void Captures_NeverSelectsDirectivesOrSeededKnowledge()
    {
        using var sandbox = new SandboxHome();
        long directive, knowledge;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            directive = DirectiveFacts.Add(connection, "Verified answers only: cite src/a.cs:12 every time.", T0);
            knowledge = LiveSeededUserFactUnder(connection, "/knowledge");
        }

        var (exit, stdout, _) = Run(sandbox, "--captures");

        Assert.Equal(0, exit);
        Assert.Contains("; 0 suspect", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain($"f{directive} ", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain($"f{knowledge} ", stdout, StringComparison.Ordinal);
    }

    private static (int Exit, string Stdout, string Stderr) Run(SandboxHome sandbox, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = CliApp.Run(["--home", sandbox.Home.Root, "reorganize", .. args], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static long Ledger(SqliteConnection connection, string path, string body) =>
        FactStore.Remember(connection, new FactWrite(
            path, "concept", "records", body, "user", "stated", Evidence: "ah2 ledger"), T0).FactId;

    private static long Capture(SqliteConnection connection, string body) =>
        FactStore.Remember(connection, new FactWrite(
            $"{UserFacts.AboutYouPath}/{FactStore.Fingerprint(body)}", UserFacts.StatementKind, "stated", body,
            UserFacts.Scope, UserFacts.LearnedVia), T0).FactId;

    /// <summary>A live seeded fact with scope <c>user</c> under <paramref name="root"/> (CannedFacts).</summary>
    private static long LiveSeededUserFactUnder(SqliteConnection connection, string root) =>
        FactStore.ReadSubtree(connection, root).First(f => f.Scope == "user").Id;

    private static string ReadReason(SqliteConnection connection, long factId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT reason FROM supersession WHERE old_fact_id = $id;";
        command.Parameters.AddWithValue("$id", factId);
        return (string)command.ExecuteScalar()!;
    }

    private static SortedDictionary<string, int> LiveByScope(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT scope, count(*) FROM fact WHERE valid_to IS NULL GROUP BY scope;";
        using var reader = command.ExecuteReader();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read())
        {
            counts[reader.GetString(0)] = reader.GetInt32(1);
        }

        return counts;
    }

    private static string Section(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        return text[from..to];
    }

    private static SortedDictionary<string, string> FileSnapshot(string root)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            files[Path.GetRelativePath(root, path)] = $"{info.Length}@{info.LastWriteTimeUtc:O}";
        }

        return files;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
