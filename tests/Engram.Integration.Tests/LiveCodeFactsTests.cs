using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// <see cref="LiveCodeFacts.UnderFilePredicate"/> is a byte range over <c>fact.path</c>, exact
/// only under BINARY comparison (D95). These pin both what it admits and how SQLite answers it.
/// </summary>
public class LiveCodeFactsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private const string Dir = "/projects/p/code/r/src";

    private static void Seed(SqliteConnection connection, params string[] paths)
    {
        var at = T0;
        foreach (var path in paths)
        {
            FactStore.Remember(
                connection,
                new FactWrite(path, "symbol", "declares", "body of " + path, "code", "observed", Regenerable: true),
                at = at.AddSeconds(1));
        }
    }

    private static List<string> Under(SqliteConnection connection, string filePath)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT fact.path FROM fact WHERE fact.valid_to IS NULL AND {LiveCodeFacts.UnderFilePredicate} ORDER BY fact.path;";
        LiveCodeFacts.BindFile(command, filePath);

        var paths = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    [Fact]
    public void UnderFile_AdmitsTheFileAndItsSymbols_AndNothingThatMerelySortsNearIt()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var file = $"{Dir}/Foo.cs";
        string[] inside = [file, $"{file}#Bar", $"{file}#Bar.Baz", $"{file}#$Dollar", $"{file}#"];
        string[] outside =
        [
            $"{Dir}/Foo.cs2", $"{Dir}/Foo.csx", $"{Dir}/Foo.cs2#Bar",
            $"{Dir}/Foo.cs$", $"{Dir}/Foo.cs$Bar", $"{Dir}/Foo.cs%", $"{Dir}/Foo.cs_", $"{Dir}/Foo.cs%#Bar",
            $"{Dir}/Foo.cs!", $"{Dir}/Foo.cs ", $"{Dir}/Foo.c", $"{Dir}/Foo.csé",
            $"{Dir}/foo.cs", $"{Dir}/FOO.CS#Bar",
        ];
        Seed(connection, [.. inside, .. outside]);

        Assert.Equal(inside.Order(StringComparer.Ordinal), Under(connection, file));
    }

    [Fact]
    public void UnderFile_WildcardAndDollarInTheFileName_AreLiteral()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var file = $"{Dir}/a_%$.cs";
        Seed(connection, file, $"{file}#X", $"{Dir}/ab%$.cs", $"{Dir}/a_x$.cs#X", $"{Dir}/a_%$.cs$#X");

        Assert.Equal([file, $"{file}#X"], Under(connection, file));
    }

    [Fact]
    public void UnderFile_ADirectoryPath_DoesNotClaimTheFilesBeneathIt()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var directory = $"{Dir}/A";
        Seed(connection, directory, $"{directory}/B.cs", $"{directory}/B.cs#C", $"{Dir}/AB.cs");

        Assert.Equal([directory], Under(connection, directory));
    }

    [Fact]
    public void UnderFile_NonAsciiPaths_MatchByteForByte()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        // é is two UTF-8 bytes and 🙂 is a surrogate pair in UTF-16: a length-based form counts
        // either differently from SQLite.
        var file = $"{Dir}/Föö🙂.cs";
        Seed(connection, file, $"{file}#Sym", $"{file}#Ünï", $"{Dir}/Föö🙂.csx", $"{Dir}/Fõõ🙂.cs#Sym", $"{Dir}/Föö🙃.cs");

        Assert.Equal([file, $"{file}#Sym", $"{file}#Ünï"], Under(connection, file));
    }

    [Theory]
    [InlineData("indexer")]
    [InlineData("path-facts")]
    public void BothStatements_AreAnsweredFromThePathIndex_NeverByAScan(string statement)
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Seed(connection, $"{Dir}/Foo.cs", $"{Dir}/Foo.cs#Bar", $"{Dir}/Other.cs");

        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + (statement == "indexer" ? CodeIndexer.ReadLiveUnderSql : ModApi.PathFactsSql);
        LiveCodeFacts.BindFile(command, $"{Dir}/Foo.cs");
        command.Parameters.AddWithValue("$predicate", DBNull.Value);
        command.Parameters.AddWithValue("$limit", 50);

        var lines = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                lines.Add(reader.GetString(3));
            }
        }

        var plan = string.Join(" | ", lines);
        Assert.Contains("MULTI-INDEX OR", plan, StringComparison.Ordinal);
        Assert.Contains("SEARCH fact USING INDEX ix_fact_path (path=?)", plan, StringComparison.Ordinal);
        Assert.Contains("SEARCH fact USING INDEX ix_fact_path (path>? AND path<?)", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("SCAN fact", plan, StringComparison.Ordinal);
    }
}
