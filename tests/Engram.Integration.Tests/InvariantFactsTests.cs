using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2. Drives <see cref="InvariantCommand"/> and <see cref="InvariantFacts"/> against a real
/// SQLite store with an indexed repository, so each row of the invariant contract is held without
/// a published binary.
/// </summary>
public class InvariantFactsTests
{
    private const string Rule = "Always take the lock before reading the counter.";

    private static (int Exit, string Out, string Err) Run(SandboxHome sandbox, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = InvariantCommand.Run(sandbox.Home.Root, args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static (string Repo, string File) IndexedRepo(SandboxHome sandbox)
    {
        var repo = PathCanonicalizer.Canonical(Path.Combine(sandbox.Home.Root, "inv-repo-" + Guid.NewGuid().ToString("N")[..8]));
        Directory.CreateDirectory(repo);
        var file = Path.Combine(repo, "NOTES.md");
        File.WriteAllText(file, "# Deployment notes\n\nShip on Tuesdays.\n\n## Rollback\n\nRevert the tag.\n");
        File.WriteAllText(Path.Combine(repo, "NOTES.md.bak"), "# Old notes\n\nStale.\n");
        Reindex(sandbox, repo, full: false);
        return (repo, file);
    }

    private static void Reindex(SandboxHome sandbox, string repo, bool full)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(repo, Apply: true, Drain: false, Full: full),
            DateTimeOffset.UtcNow);
    }

    private static List<(long Id, string Path, string Body, bool Regenerable, string Scope, string LearnedVia, string? Evidence, string EntityKind)>
        InvariantRows(SandboxHome sandbox, bool liveOnly = true)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT f.id, f.path, f.body, f.regenerable, f.scope, f.learned_via, f.evidence, e.kind "
            + "FROM fact f JOIN entity e ON e.id = f.subject_id "
            + "WHERE f.predicate = 'invariant'"
            + (liveOnly ? " AND f.valid_to IS NULL" : string.Empty)
            + " ORDER BY f.id;";
        var rows = new List<(long, string, string, bool, string, string, string?, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0,
                reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7)));
        }

        return rows;
    }

    private static string EntityPathOf(SandboxHome sandbox, string file)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        return CodeEntityResolver.Resolve(connection, file)!.Value.EntityPath;
    }

    [Fact]
    public void Add_OnAnIndexedFile_WritesOneFactOfTheSpecifiedShape()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var (_, file) = IndexedRepo(sandbox);

        var (exit, stdout, _) = Run(sandbox, "add", file, Rule);

        Assert.Equal(0, exit);
        Assert.Contains("recorded", stdout, StringComparison.Ordinal);
        var row = Assert.Single(InvariantRows(sandbox));
        Assert.Equal(EntityPathOf(sandbox, file) + "#invariant-" + FactStore.Fingerprint(Rule)[..8], row.Path);
        Assert.Equal(Rule, row.Body);
        Assert.False(row.Regenerable);
        Assert.Equal("project", row.Scope);
        Assert.Equal("stated", row.LearnedVia);
        Assert.Equal("stated by the user via engram invariant", row.Evidence);
        Assert.Equal("convention", row.EntityKind);
    }

    [Fact]
    public void Add_TheSameStatementTwice_IsANoOpThatSaysSo()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var (_, file) = IndexedRepo(sandbox);
        Run(sandbox, "add", file, Rule);

        var (exit, stdout, _) = Run(sandbox, "add", file, Rule);

        Assert.Equal(0, exit);
        Assert.Contains("already recorded", stdout, StringComparison.Ordinal);
        Assert.Single(InvariantRows(sandbox, liveOnly: false));
    }

    [Fact]
    public void Add_TwoDifferentStatementsOnOneFile_AreTwoLiveFactsOnTwoSubjects()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var (_, file) = IndexedRepo(sandbox);

        Run(sandbox, "add", file, Rule);
        Run(sandbox, "add", file, "Never log the session token.");

        var rows = InvariantRows(sandbox);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(r => r.Path).Distinct().Count());
    }

    [Fact]
    public void Add_OnAFileOutsideAnyIndexedRepo_FailsAndWritesNothing()
    {
        using var sandbox = new SandboxHome(initialize: false);
        IndexedRepo(sandbox);
        var outside = Path.Combine(sandbox.Home.Root, "outside.txt");
        File.WriteAllText(outside, "x");

        var (exit, _, stderr) = Run(sandbox, "add", outside, Rule);

        Assert.Equal(1, exit);
        Assert.Contains("repo enroll", stderr, StringComparison.Ordinal);
        Assert.Empty(InvariantRows(sandbox, liveOnly: false));
    }

    // The nearest path that must not match: a sibling directory whose name begins with the
    // repository's own name.
    [Fact]
    public void Add_OnAFileInASiblingDirectorySharingTheRepoNamePrefix_Fails()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var (repo, _) = IndexedRepo(sandbox);
        var sibling = repo + "-other";
        Directory.CreateDirectory(sibling);
        var file = Path.Combine(sibling, "NOTES.md");
        File.WriteAllText(file, "x");

        var (exit, _, _) = Run(sandbox, "add", file, Rule);

        Assert.Equal(1, exit);
        Assert.Empty(InvariantRows(sandbox, liveOnly: false));
    }

    [Fact]
    public void Add_OnADirectory_FailsAndWritesNothing()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var (repo, _) = IndexedRepo(sandbox);

        var (exit, _, stderr) = Run(sandbox, "add", repo, Rule);

        Assert.Equal(1, exit);
        Assert.Contains("directory", stderr, StringComparison.Ordinal);
        Assert.Empty(InvariantRows(sandbox, liveOnly: false));
    }

    [Fact]
    public void Add_OnAFileThatDoesNotExist_FailsAndWritesNothing()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var (repo, _) = IndexedRepo(sandbox);

        var (exit, _, _) = Run(sandbox, "add", Path.Combine(repo, "TYPO.md"), Rule);

        Assert.Equal(1, exit);
        Assert.Empty(InvariantRows(sandbox, liveOnly: false));
    }

    // 900 characters estimate to exactly 250 tokens, the cap; one more character is 251.
    [Theory]
    [InlineData(900, 0)]
    [InlineData(901, 1)]
    public void Add_StatementAtAndOverTheTokenCap(int length, int expectedExit)
    {
        using var sandbox = new SandboxHome(initialize: false);
        var (_, file) = IndexedRepo(sandbox);
        var statement = new string('a', length);
        Assert.Equal(expectedExit == 0 ? InvariantFacts.MaxStatementTokens : InvariantFacts.MaxStatementTokens + 1, TokenEstimator.Estimate(statement));

        var (exit, _, _) = Run(sandbox, "add", file, statement);

        Assert.Equal(expectedExit, exit);
        Assert.Equal(expectedExit == 0 ? 1 : 0, InvariantRows(sandbox).Count);
    }

    // Deleting the file makes the indexer close its regenerable facts; the invariant is authored
    // and must be left alone. The control assertion proves the pass really did process the deletion.
    [Fact]
    public void AFileDeletedThenIndexed_KeepsItsInvariantLiveWhileClosingItsCodeFacts()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var (repo, file) = IndexedRepo(sandbox);
        Run(sandbox, "add", file, Rule);
        var entity = EntityPathOf(sandbox, file);
        Assert.True(LiveRegenerableUnder(sandbox, entity) > 0);

        File.Delete(file);
        Reindex(sandbox, repo, full: false);

        Assert.Equal(0, LiveRegenerableUnder(sandbox, entity));
        Assert.Single(InvariantRows(sandbox));
    }

    [Fact]
    public void AFullReindex_LeavesTheInvariantUntouched()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var (repo, file) = IndexedRepo(sandbox);
        Run(sandbox, "add", file, Rule);
        var before = Assert.Single(InvariantRows(sandbox));

        Reindex(sandbox, repo, full: true);

        var after = Assert.Single(InvariantRows(sandbox));
        Assert.Equal(before.Id, after.Id);
    }

    [Fact]
    public void ARecallQueryMatchingTheInvariantText_FindsItLikeAnyFact()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var (_, file) = IndexedRepo(sandbox);
        Run(sandbox, "add", file, Rule);
        var id = Assert.Single(InvariantRows(sandbox)).Id;

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var hits = FactStore.SearchRanked(connection, "lock counter", 10);

        Assert.Contains(hits, hit => hit.FactId == id);
    }

    [Fact]
    public void List_ForOneFile_ShowsOnlyThatFilesInvariants()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var (repo, file) = IndexedRepo(sandbox);
        var sibling = Path.Combine(repo, "NOTES.md.bak");
        Run(sandbox, "add", file, Rule);
        Run(sandbox, "add", sibling, "The backup is read-only.");

        var (exit, stdout, _) = Run(sandbox, "list", file);

        Assert.Equal(0, exit);
        Assert.Contains(Rule, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("The backup is read-only.", stdout, StringComparison.Ordinal);
        var (_, all, _) = Run(sandbox, "list");
        Assert.Contains("The backup is read-only.", all, StringComparison.Ordinal);
        Assert.Contains(Rule, all, StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_WithoutApply_ChangesNothing_AndWithApplyClosesTheFact()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var (_, file) = IndexedRepo(sandbox);
        Run(sandbox, "add", file, Rule);
        var handle = FactCatalog.HandleFor(Assert.Single(InvariantRows(sandbox)).Id);

        var (dryExit, dryOut, _) = Run(sandbox, "remove", handle);
        Assert.Equal(0, dryExit);
        Assert.Contains("Dry run", dryOut, StringComparison.Ordinal);
        Assert.Single(InvariantRows(sandbox));

        var (exit, _, _) = Run(sandbox, "remove", handle, "--apply");
        Assert.Equal(0, exit);
        Assert.Empty(InvariantRows(sandbox));
        Assert.Single(InvariantRows(sandbox, liveOnly: false));
    }

    // The verb takes a fact id, so it must refuse every fact that is not a live invariant — here a
    // directive, the nearest kind of authored fact.
    [Fact]
    public void Remove_OfAFactThatIsNotAnInvariant_IsRefusedAndLeavesItLive()
    {
        using var sandbox = new SandboxHome(initialize: false);
        IndexedRepo(sandbox);
        long directiveId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            directiveId = DirectiveFacts.Add(connection, "always use BEGIN IMMEDIATE for writes", DateTimeOffset.UtcNow);
        }

        var (exit, _, _) = Run(sandbox, "remove", FactCatalog.HandleFor(directiveId), "--apply");

        Assert.Equal(1, exit);
        using var check = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Single(DirectiveFacts.ReadLive(check));
    }

    private static int LiveRegenerableUnder(SandboxHome sandbox, string entityPath)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM fact WHERE valid_to IS NULL AND regenerable = 1 AND (path = $p OR path LIKE $p || '#%');";
        command.Parameters.AddWithValue("$p", entityPath);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
