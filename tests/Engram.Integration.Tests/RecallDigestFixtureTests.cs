using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// The Memory Lens reads the recall digest the model already received instead of asking again, so
/// its parser in <c>plugin/mods/lens</c> is a second consumer of a text format this repository
/// owns. These tests render a fixed set of digests and require them to equal the files the
/// TypeScript parser tests read — a formatter change reds this file first and forces a fixture
/// update that the parser's own tests then run against.
/// </summary>
public class RecallDigestFixtureTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private const string HighPath = "high.txt";
    private const string PartialPath = "partial.txt";
    private const string NoneBothLanesPath = "none-both-lanes.txt";

    [Fact]
    public void High_CarriesEveryMarker_AndMatchesItsFixture()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var pinned = Write(connection, "kestrel-pinned", "Kestrel listener binds loopback only, and a pinned fact says so.");
        Write(connection, "kestrel-version", "Kestrel listener first bound every interface.");
        Write(connection, "kestrel-version", "Kestrel listener binds loopback only after the revision.");
        var judgedA = Write(connection, "kestrel-judged-a", "Kestrel listener port comes from the server settings.");
        var judgedB = Write(connection, "kestrel-judged-b", "Kestrel listener port is read at startup.");
        Judge(connection, judgedA, judgedB);
        Write(connection, "kestrel-long", LongBody("Kestrel listener tuning notes"));
        FactStore.Remember(
            connection,
            new FactWrite(
                "/projects/p/code/r/src/kestrel.cs#Bind", "symbol", "declared-as",
                "Bind(port) — kestrel listener binder.", "code", "observed", Regenerable: true),
            T0);

        var text = Pack(connection, "kestrel listener", VectorLaneQuery.Stopped(VectorLaneState.Off, "off"), pinned).Text;

        Assert.StartsWith("RECALL \"kestrel listener\" · ", text, StringComparison.Ordinal);
        Assert.Contains("coverage: high", text, StringComparison.Ordinal);
        Assert.Contains(" · v2", text, StringComparison.Ordinal);
        Assert.Contains(" · judged", text, StringComparison.Ordinal);
        Assert.Contains(" · pinned)", text, StringComparison.Ordinal);
        Assert.Contains(" · +", text, StringComparison.Ordinal);
        Assert.Contains("(code · r:src/kestrel.cs · 0d", text, StringComparison.Ordinal);

        AssertMatchesFixture(HighPath, text);
    }

    [Fact]
    public void Partial_CarriesTheGapsLine_AndMatchesItsFixture()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Write(connection, "kestrel-a", "Kestrel listener binds loopback only.");

        var text = Pack(connection, "kestrel listener", VectorLaneQuery.Stopped(VectorLaneState.Off, "off")).Text;

        Assert.Contains("coverage: partial", text, StringComparison.Ordinal);
        Assert.Contains("\ngaps: only partial matches", text, StringComparison.Ordinal);

        AssertMatchesFixture(PartialPath, text);
    }

    [Fact]
    public void None_NamesBothUnavailableLanes_AndMatchesItsFixture()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Write(connection, "kestrel-a", "Kestrel listener binds loopback only.");
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM schema_meta WHERE key = 'fact_token_version';";
            command.ExecuteNonQuery();
        }

        var stopped = VectorLaneQuery.Stopped(VectorLaneState.Unavailable, "sqlite-vec is not installed");
        var text = Pack(connection, "zzznomatch", stopped).Text;

        Assert.Contains("coverage: none", text, StringComparison.Ordinal);
        Assert.Contains("overlap lane did not run (token index not built yet)", text, StringComparison.Ordinal);
        Assert.Contains("vector lane did not run (sqlite-vec is not installed)", text, StringComparison.Ordinal);

        AssertMatchesFixture(NoneBothLanesPath, text);
    }

    private static void AssertMatchesFixture(string name, string actual)
    {
        var path = Path.Combine(FixtureDirectory(), name);
        Assert.True(File.Exists(path), $"fixture {path} is missing");

        // Compared exactly, with a trailing newline stripped on the file side only: editors add one
        // and the digest never ends with one.
        Assert.Equal(File.ReadAllText(path).TrimEnd('\n'), actual);
    }

    private static string FixtureDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "plugin", "mods", "lens", "fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate plugin/mods/lens/fixtures from the test output directory.");
    }

    private static string LongBody(string lead) =>
        lead + " " + string.Concat(Enumerable.Repeat("kestrel listener detail ", 80)).TrimEnd();

    private static RecallPackResult Pack(
        SqliteConnection connection, string query, VectorLaneQuery vectorQuery, long? pinnedFactId = null) =>
        RecallRanker.Pack(
            connection, query, RetrievalSettings.DefaultBudgetTokens, RetrievalSettings.DefaultSeedK,
            currentSessionId: null, T0, vectorQuery,
            pinnedFactId is { } id ? new HashSet<long> { id } : null);

    private static long Write(
        SqliteConnection connection, string slug, string body, string scope = "project", string predicate = "states") =>
        FactStore.Remember(
            connection,
            new FactWrite("/knowledge/testing/" + slug, "note", predicate, body, scope, "stated"),
            T0).FactId;

    private static void Judge(SqliteConnection connection, long factId, long relatedId)
    {
        using var transaction = EngramDatabase.BeginWrite(connection);
        FactRelations.Judge(connection, transaction, factId, relatedId, "not_conflict", null, T0.ToUnixTimeSeconds());
        transaction.Commit();
    }
}
