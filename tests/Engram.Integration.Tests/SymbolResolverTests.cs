using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

public class SymbolResolverTests
{
    [Fact]
    public void Resolve_PrefersExactMatch_OverCaseInsensitiveAndSubstring()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedSymbol(connection, "/projects/p/code/r/a.cs#Widget", "Widget");
        SeedSymbol(connection, "/projects/p/code/r/b.cs#widget", "widget");
        SeedSymbol(connection, "/projects/p/code/r/c.cs#WidgetFactory", "WidgetFactory");

        var matches = SymbolResolver.Resolve(connection, "Widget", 20);

        var match = Assert.Single(matches);
        Assert.Equal("/projects/p/code/r/a.cs#Widget", match.Path);
        Assert.Equal(SymbolMatchTier.Exact, match.Tier);
    }

    [Fact]
    public void Resolve_FallsBackToCaseInsensitive_WhenNoExactMatch()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedSymbol(connection, "/projects/p/code/r/a.cs#Widget", "Widget");

        var matches = SymbolResolver.Resolve(connection, "widget", 20);

        var match = Assert.Single(matches);
        Assert.Equal(SymbolMatchTier.CaseInsensitive, match.Tier);
    }

    [Fact]
    public void Resolve_FallsBackToSubstring_WhenNoExactOrCaseInsensitiveMatch()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedSymbol(connection, "/projects/p/code/r/a.cs#WidgetFactory", "WidgetFactory");

        var matches = SymbolResolver.Resolve(connection, "Widget", 20);

        var match = Assert.Single(matches);
        Assert.Equal(SymbolMatchTier.Substring, match.Tier);
    }

    [Fact]
    public void Resolve_IgnoresNonSymbolEntities()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedEntity(connection, "/projects/p/code/r/Widget.cs", "file", "Widget.cs");

        var matches = SymbolResolver.Resolve(connection, "Widget", 20);

        Assert.Empty(matches);
    }

    [Fact]
    public void Resolve_ReturnsEmpty_ForBlankName()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Empty(SymbolResolver.Resolve(connection, "  ", 20));
    }

    [Theory]
    [InlineData("e.name = $name", "ix_symbol_name", false)]
    [InlineData("e.name = $name", "ix_symbol_name", true)]
    [InlineData("e.name = $name COLLATE NOCASE", "ix_symbol_name_nocase", false)]
    [InlineData("e.name = $name COLLATE NOCASE", "ix_symbol_name_nocase", true)]
    public void EqualityLookups_SearchTheNameIndex(string predicate, string index, bool repoScoped)
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedSymbol(connection, "/projects/p/code/repo-a/a.cs#Widget", "Widget");
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + SymbolResolver.StatementFor(predicate, repoScoped);
        command.Parameters.AddWithValue("$name", "Widget");
        command.Parameters.AddWithValue("$limit", 20);
        if (repoScoped) command.Parameters.AddWithValue("$repo", "repo-a");
        var plan = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) plan.Add(reader.GetString(3));
        Assert.Contains(plan, line => line.Contains("SEARCH e USING COVERING INDEX " + index + " (name=?)", StringComparison.Ordinal));
    }

    [Fact]
    public void NameIndexes_PreserveFallbackScopeOrderingAndLimits()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var names = new[] { "Widget", "Widget", "widget", "WidgetFactory", "literal%", "東京", "東京" };
        for (var i = 0; i < names.Length; i++)
        {
            SeedSymbol(connection, $"/projects/p/code/repo-{(i % 2 == 0 ? "a" : "b")}/{i}.cs#{names[i]}", names[i]);
        }

        var ddl = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE name IN ('ix_symbol_name', 'ix_symbol_name_nocase') ORDER BY name;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) ddl.Add(reader.GetString(0));
        }
        Assert.Equal(2, ddl.Count);
        using var change = connection.CreateCommand();
        change.CommandText = "DROP INDEX ix_symbol_name; DROP INDEX ix_symbol_name_nocase;";
        change.ExecuteNonQuery();

        var cases = (from name in new[] { "Widget", "WIDGET", "Wid", "literal%", "東京", "missing" }
                     from repo in new string?[] { null, "repo-a", "repo-b" }
                     from limit in new[] { 1, 20 }
                     select (name, repo, limit)).ToArray();
        var before = cases.Select(c => SymbolResolver.Resolve(connection, c.name, c.limit, c.repo)).ToArray();
        change.CommandText = string.Join(';', ddl);
        change.ExecuteNonQuery();
        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            Assert.Equal(before[i], SymbolResolver.Resolve(connection, c.name, c.limit, c.repo));
        }

        // Both name indexes follow ordinary entity name and path changes.
        change.CommandText = "UPDATE entity SET name = 'Renamed', path = path || '-renamed' WHERE name = '東京';";
        change.ExecuteNonQuery();
        Assert.Empty(SymbolResolver.Resolve(connection, "東京", 20));
        Assert.Equal(2, SymbolResolver.Resolve(connection, "Renamed", 20).Count);
    }

    private static void SeedSymbol(SqliteConnection connection, string path, string name) =>
        SeedEntity(connection, path, "symbol", name);

    private static void SeedEntity(SqliteConnection connection, string path, string kind, string name)
    {
        using var transaction = EngramDatabase.BeginWrite(connection);
        FactStore.EnsureEntity(connection, transaction, path, kind, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), name);
        transaction.Commit();
    }
}
