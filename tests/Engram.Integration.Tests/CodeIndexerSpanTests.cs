using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Spans are computed from the live file on demand. A line number that reached the store would turn
/// every edit above a symbol into a new fact version, so this is the guard that none does.
/// </summary>
public class CodeIndexerSpanTests
{
    private const string ProgramCs = """
        /// <summary>Turns cranks into torque.</summary>
        using System.Text;

        public sealed class Widget { }
        public enum Gear { Low }
        """;

    private const string ReadmeMd = """
        Fixture repo for the code indexer.

        # Guide

        ## Usage

        Run the fixture through the indexer.
        """;

    private static IndexReport Index(SqliteConnection connection, SandboxHome sandbox, string repo) =>
        CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(repo, Apply: true, Drain: false, Full: false),
            DateTimeOffset.UtcNow);

    private static (long Count, long MaxId, List<string> Live) Snapshot(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*), coalesce(max(id), 0) FROM fact";
        using var reader = command.ExecuteReader();
        reader.Read();
        var (count, maxId) = (reader.GetInt64(0), reader.GetInt64(1));
        reader.Close();

        var live = FactStore.ReadLive(connection)
            .Where(f => f.SubjectPath.Contains("/code/", StringComparison.Ordinal))
            .Select(f => $"{f.SubjectPath}|{f.Predicate}|{f.Body}|{f.Evidence}")
            .Order(StringComparer.Ordinal)
            .ToList();
        return (count, maxId, live);
    }

    [Fact]
    public void ShiftingEveryLine_WritesNoNewFactVersion_AndChangesNoBodyOrEvidence()
    {
        using var sandbox = new SandboxHome();
        var repo = Path.Combine(sandbox.Home.Root, "fixture-repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "Program.cs"), ProgramCs);
        File.WriteAllText(Path.Combine(repo, "README.md"), ReadmeMd);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Index(connection, sandbox, repo);
        var before = Snapshot(connection);
        Assert.NotEmpty(before.Live);

        foreach (var name in new[] { "Program.cs", "README.md" })
        {
            var path = Path.Combine(repo, name);
            File.WriteAllText(path, "\n\n\n\n\n" + File.ReadAllText(path));
        }

        var again = Index(connection, sandbox, repo);

        Assert.True(again.Analyzed > 0, "the shifted files must actually be re-read, or this proves nothing");
        var after = Snapshot(connection);
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before.MaxId, after.MaxId);
        Assert.Equal(before.Live, after.Live);
        Assert.Equal(6, CodeAnalyzer.AnalyzerVersion);
    }
}
