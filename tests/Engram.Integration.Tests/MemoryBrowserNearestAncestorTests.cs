using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// What a browse miss points at: the deepest prefix that <see cref="MemoryBrowser.Browse"/> would
/// answer. The probe is asked as index range scans, so the equivalence test is what keeps it the
/// same rule as Browse's own <c>substr</c> boundary.
/// </summary>
public class MemoryBrowserNearestAncestorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 7, 7, 0, 0, TimeSpan.Zero);

    private static SqliteConnection Seed(SandboxHome sandbox, params string[] paths)
    {
        var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        foreach (var path in paths)
        {
            FactStore.EnsureEntity(connection, null, path, "concept", T0.ToUnixTimeSeconds());
        }

        return connection;
    }

    private static SqliteConnection Empty(SandboxHome sandbox)
    {
        var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM entity;";
        command.ExecuteNonQuery();
        return connection;
    }

    [Fact]
    public void NearestAncestor_DeepMiss_ReturnsTheDeepestExistingPrefix()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = Seed(sandbox, "/projects/engram/code/engram/src/Core/a.cs");

        Assert.Equal(
            "/projects/engram/code/engram/src",
            MemoryBrowser.NearestAncestor(connection, "/projects/engram/code/engram/src/Foo.cs"));
    }

    [Fact]
    public void NearestAncestor_SiblingPrefixIsNotAnAncestor()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = Seed(sandbox, "/projects/engram-docs/x");

        Assert.Equal("/projects", MemoryBrowser.NearestAncestor(connection, "/projects/engram/y"));
    }

    [Fact]
    public void NearestAncestor_MissingSymbol_FallsBackToItsFileNotItsDirectory()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = Seed(sandbox, "/projects/p/code/r/src/a.cs", "/projects/p/code/r/src/a.cs#Foo");

        Assert.Equal(
            "/projects/p/code/r/src/a.cs",
            MemoryBrowser.NearestAncestor(connection, "/projects/p/code/r/src/a.cs#Missing"));
    }

    [Fact]
    public void NearestAncestor_NoLeadingSlash_EndsAtRoot()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = Seed(sandbox, "/projects/x");

        Assert.Equal("/", MemoryBrowser.NearestAncestor(connection, "projects/x"));
    }

    [Fact]
    public void NearestAncestor_TrailingSlash_AnswersAsWithout()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = Seed(sandbox, "/projects/x/y");

        Assert.Equal(
            MemoryBrowser.NearestAncestor(connection, "/projects/x/missing"),
            MemoryBrowser.NearestAncestor(connection, "/projects/x/missing/"));
        Assert.Equal("/projects/x", MemoryBrowser.NearestAncestor(connection, "/projects/x/missing/"));
    }

    [Fact]
    public void NearestAncestor_EmptyStore_IsNull()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = Empty(sandbox);

        Assert.Null(MemoryBrowser.NearestAncestor(connection, "/projects/x"));
        Assert.Null(MemoryBrowser.NearestAncestor(connection, "/"));
    }

    [Fact]
    public void NearestAncestor_AgreesWithBrowseMembership()
    {
        using var sandbox = new SandboxHome(initialize: false);
        string[] seeded =
        [
            "/projects/engram/code/engram/src/Core/a.cs",
            "/projects/engram/code/engram/src/Core/a.cs#Foo",
            "/projects/engram/code/engram/docs/guide.md#Intro",
            "/projects/engram-docs/x",
            "/projects/engram#odd",
            "/lonely#child",
            "/people/jim",
            "/knowledge/testing/alpha",
            "/user/pref_one/with%25percent",
        ];
        using var connection = Seed(sandbox, seeded);

        var probes = new HashSet<string>(StringComparer.Ordinal)
        {
            "/", "/projects", "/projects/engram", "/projects/engr", "/projects/engram/y", "/projects/engram-docs",
            "/projects/engram-docs/x/deeper", "/projects/engram/code/engram/src/Core/a.cs#Missing",
            "/projects/engram/code/engram/src/Core/a.cs#Fo", "/nowhere", "projects/x", "/people/jimmy", "/user/pref", "/lonely",
        };
        foreach (var path in seeded)
        {
            probes.Add(path);
            for (var cut = path.LastIndexOfAny(['/', '#']); cut > 0; cut = path.LastIndexOfAny(['/', '#'], cut - 1))
            {
                probes.Add(path[..cut]);
            }
        }

        foreach (var probe in probes)
        {
            var browses = MemoryBrowser.Browse(connection, probe, 1) is not null;
            var nearest = MemoryBrowser.NearestAncestor(connection, probe);

            Assert.True(
                browses == (nearest == (probe.TrimEnd('/').Length == 0 ? "/" : probe.TrimEnd('/'))),
                $"'{probe}': Browse answers {browses} but the nearest ancestor is '{nearest}'");
            Assert.NotNull(nearest);
            Assert.NotNull(MemoryBrowser.Browse(connection, nearest, 1));
        }
    }
}
