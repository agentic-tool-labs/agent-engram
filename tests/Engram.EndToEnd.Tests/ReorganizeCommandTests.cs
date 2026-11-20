using Microsoft.Data.Sqlite;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// <c>engram reorganize</c> through the published binary (D87): the dry run leaves every file in
/// the home as it was, and <c>--apply</c> is idempotent. Seeded through <c>engram import</c>, the
/// path the other tool's ledger actually arrives by.
/// </summary>
public class ReorganizeCommandTests
{
    [Theory]
    [InlineData("--ledger")]
    [InlineData("--captures")]
    public void DryRun_ChangesNoFileInTheHome(string step)
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        Seed(home);

        var before = Snapshot(home.Root);
        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "reorganize", step);
        var after = Snapshot(home.Root);

        Assert.True(exitCode == 0, stderr);
        Assert.Contains("Dry run only", stdout, StringComparison.Ordinal);
        Assert.Contains(step == "--ledger" ? "ledger: 2 live fact(s)" : "; 1 suspect", stdout, StringComparison.Ordinal);
        Assert.Equal(before, after);
    }

    [Fact]
    public void LedgerApply_Twice_ReFilesOnce()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        Seed(home);

        var first = EngramProcess.Run(home.Root, "reorganize", "--ledger", "--apply");
        var second = EngramProcess.Run(home.Root, "reorganize", "--ledger", "--apply");

        Assert.True(first.ExitCode == 0, first.Stderr);
        Assert.Contains("re-filed 2,", first.Stdout, StringComparison.Ordinal);
        Assert.True(second.ExitCode == 0, second.Stderr);
        Assert.Contains("re-filed 0", second.Stdout, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Path.Combine(home.Root, "backups"), "*pre-reorganize*"));

        using var connection = new SqliteConnection($"Data Source={Path.Combine(home.Root, "engram.db")};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT count(*) FROM fact f JOIN entity e ON e.id = f.subject_id
             WHERE f.valid_to IS NULL AND f.scope = 'tool' AND e.path LIKE '/tools/ah2/%';
            """;
        Assert.Equal(2L, (long)command.ExecuteScalar()!);
    }

    private static void Seed(TestHome home)
    {
        var bundlePath = Path.Combine(Path.GetTempPath(), "engram-e2e-reorganize-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(bundlePath,
        [
            """{"format":"engram-facts","format_version":1,"schema_version":9,"written_at":"2026-11-19T00:00:00Z"}""",
            """{"id":1,"subject":"/ah2/repo/t/train/1","kind":"concept","predicate":"records","body":"train one opened","object":null,"object_kind":null,"scope":"user","learned_via":"stated","regenerable":false,"evidence":"ah2 ledger","valid_from":1763510400,"valid_to":null,"superseded_by":null,"reason":null,"created_at":1763510400,"details":null}""",
            """{"id":2,"subject":"/ah2/repo/t/train/2","kind":"concept","predicate":"records","body":"train two opened","object":null,"object_kind":null,"scope":"user","learned_via":"stated","regenerable":false,"evidence":"ah2 ledger","valid_from":1763510400,"valid_to":null,"superseded_by":null,"reason":null,"created_at":1763510400,"details":null}""",
            """{"id":3,"subject":"/user/about-you/0a0b0c0d","kind":"statement","predicate":"stated","body":"Verified the fix against the rebuilt binary.","object":null,"object_kind":null,"scope":"user","learned_via":"stated","regenerable":false,"evidence":null,"valid_from":1763510400,"valid_to":null,"superseded_by":null,"reason":null,"created_at":1763510400,"details":null}""",
        ]);

        try
        {
            var (seedCode, _, seedErr) = EngramProcess.Run(home.Root, "import", bundlePath, "--apply");
            Assert.True(seedCode == 0, $"seed import exited {seedCode}: {seedErr}");
        }
        finally
        {
            File.Delete(bundlePath);
        }
    }

    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            files[Path.GetRelativePath(root, path)] = $"{info.Length}@{info.LastWriteTimeUtc:O}";
        }

        return files;
    }
}
