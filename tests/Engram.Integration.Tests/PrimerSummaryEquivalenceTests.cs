using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// <see cref="PrimerSummary.Read"/> stops the primer materialising the corpus, and the whole
/// value of that depends on it changing nothing a store would say. So the assertion is
/// byte-for-byte equality of the finished primer against one built from
/// <see cref="FactCatalog.ReadLongTerm(SqliteConnection, DateTimeOffset)"/>, which stays as the
/// reference implementation.
/// </summary>
/// <remarks>
/// String equality rather than a comparison of the two summaries, because the summaries are
/// legitimately different objects — one carries every live fact as example candidates, the other
/// carries a handful — and what has to agree is the text the model receives. Both precedence
/// values are covered because the precedence line is prepended before the budget is spent, so a
/// coverage line that changed length could be dropped under one and kept under the other.
/// </remarks>
public class PrimerSummaryEquivalenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = T0.AddDays(3);

    private static readonly MemoryPrecedence[] Precedences =
        [MemoryPrecedence.Off, MemorySettings.DefaultPrecedence];

    [Fact]
    public void AnEmptyStore() => AssertPrimersMatch(seeded: false, _ => { });

    [Fact]
    public void TheSeededStoreAsInitLeavesIt() => AssertPrimersMatch(seeded: true, _ => { });

    [Fact]
    public void OneFactOnly() => AssertPrimersMatch(seeded: false, connection =>
        Write(connection, "/knowledge/alpha/only", "The only thing believed.", "user", 1));

    /// <summary>
    /// One scope is the case where <c>TopFacts</c> has to fill from the front of the catalog
    /// after taking its single scope-first, so the candidate set has to reach the second fact.
    /// </summary>
    [Fact]
    public void FactsInASingleScope() => AssertPrimersMatch(seeded: false, connection =>
    {
        Write(connection, "/knowledge/alpha/a", "First body.", "user", 1);
        Write(connection, "/knowledge/alpha/b", "Second body.", "user", 2);
        Write(connection, "/knowledge/alpha/c", "Third body.", "user", 3);
    });

    /// <summary>
    /// All four scopes, with the lowest-id fact overall deliberately NOT the lowest-id fact of
    /// the first preferred scope — which is what separates the two arms of <c>TopFacts</c>. A
    /// candidate set built only from the front of the catalog would pick the project fact where
    /// the reference picks the user one.
    /// </summary>
    [Fact]
    public void FactsInAllFourScopesWhereTheFirstPreferredScopeIsNotFirstInTheCatalog() =>
        AssertPrimersMatch(seeded: false, connection =>
        {
            Write(connection, "/knowledge/alpha/a", "Project body.", "project", 1);
            Write(connection, "/knowledge/alpha/b", "User body.", "user", 2);
            Write(connection, "/knowledge/alpha/c", "Code body.", "code", 3);
            Write(connection, "/knowledge/alpha/d", "Session-scoped body.", "session", 4);
        });

    /// <summary>
    /// More distinct topics than <c>MaxClusters</c>, including a tie at two that only the ordinal
    /// tiebreak can settle. Written in reverse-ish order so insertion order, dictionary order and
    /// the rendered order are all different.
    /// </summary>
    [Fact]
    public void MoreTopicsThanFitWithATieTheOrdinalTiebreakHasToSettle() =>
        AssertPrimersMatch(seeded: false, connection =>
        {
            Write(connection, "/knowledge/golf/a", "Golf body.", "user", 1);
            Write(connection, "/knowledge/foxtrot/a", "Foxtrot body.", "user", 2);
            Write(connection, "/knowledge/echo/a", "Echo one.", "user", 3);
            Write(connection, "/knowledge/echo/b", "Echo two.", "user", 4);
            Write(connection, "/knowledge/delta/a", "Delta one.", "user", 5);
            Write(connection, "/knowledge/delta/b", "Delta two.", "user", 6);
            Write(connection, "/knowledge/charlie/a", "Charlie body.", "user", 7);
            Write(connection, "/knowledge/bravo/a", "Bravo body.", "user", 8);
            Write(connection, "/knowledge/alpha/a", "Alpha body.", "user", 9);
        });

    /// <summary>
    /// Session facts must not be counted, must not appear as a topic, and must not be an example.
    /// </summary>
    [Fact]
    public void SessionFactsAlongsideLongTermOnes() => AssertPrimersMatch(seeded: false, connection =>
    {
        Write(connection, "/knowledge/alpha/a", "A durable belief.", "project", 1);
        Write(connection, SessionFacts.Root + "/abc123/note-one", "A session note.", "session", 2);
        Write(connection, SessionFacts.Root + "/abc123/note-two", "Another session note.", "session", 3);
    });

    // Fewer than two segments, so TopicOf returns "memory" rather than a slug.
    [Fact]
    public void ASubjectPathWithFewerThanTwoSegments() => AssertPrimersMatch(seeded: false, connection =>
        Write(connection, "/orphan", "An orphaned belief.", "user", 1));

    /// <summary>
    /// A seeded store — so most topics resolve through a topic entity — plus one topic that has
    /// no node to resolve against, where <c>TopicOf</c> falls back to the raw slug. Both spellings
    /// have to survive the same way on both paths.
    /// </summary>
    [Fact]
    public void ATopicPathWithNoTopicEntityToResolve() => AssertPrimersMatch(seeded: true, connection =>
        Write(connection, "/knowledge/zzz-unseeded-topic/x", "A belief under no topic node.", "user", 1));

    /// <summary>
    /// A superseded thread, so a candidate carries <c>Versions</c> greater than one. The primer
    /// never prints that number, so the string equality below cannot see it — the explicit
    /// assertion is what stops the correlated count being silently wrong, and a count written as
    /// a join rather than a subquery would also duplicate the candidate row.
    /// </summary>
    [Fact]
    public void AFactWhoseThreadWasSuperseded()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var write = new FactWrite(
            "/user/about-you/colour", "preference", "prefers", "Orange.", "user", "stated");
        FactStore.Remember(connection, write, T0);
        FactStore.Remember(connection, write with { Body = "Green." }, T0.AddSeconds(9));

        var summary = PrimerSummary.Read(connection, Now);

        var candidate = Assert.Single(summary.ExampleCandidates);
        Assert.Equal("Green.", candidate.Body);
        Assert.Equal(2, candidate.Versions);

        AssertPrimersMatch(connection);
    }

    /// <summary>
    /// Every shape the histogram's two partial-index arms and the per-scope probe have to agree
    /// on at once: live edge and non-edge facts, one subject holding both (the arms' outer SUM),
    /// one subject with several live predicates, closed facts — the lowest id of the first
    /// preferred scope among them, so a probe that did not skip closed rows would pick it —
    /// session notes, a session whose only note is closed, a directive, and three scopes, one of
    /// which ('session') has no qualifying fact at all. Each arm the SQL has is exercised here;
    /// dropping either histogram arm reddens this test.
    /// </summary>
    [Fact]
    public void EdgesClosedFactsSessionsAndDirectivesTogether() =>
        AssertPrimersMatch(seeded: false, connection =>
        {
            var closedFirst = Write(connection, "/knowledge/alpha/closed", "Retracted first.", "user", 1);
            FactStore.Forget(connection, closedFirst, "test", T0.AddSeconds(2));
            Write(connection, "/knowledge/alpha/a", "Project body.", "project", 3);
            Write(connection, "/knowledge/alpha/b", "User body.", "user", 4);
            Edge(connection, "/knowledge/alpha/b", "/knowledge/beta/target", "relates", 5);
            Edge(connection, "/knowledge/beta/x", "/knowledge/alpha/a", "uses", 6);
            Edge(connection, "/knowledge/beta/x", "/knowledge/alpha/b", "uses", 7);
            FactStore.Remember(connection,
                new FactWrite("/knowledge/beta/x", "note", "prefers", "Several predicates.", "user", "stated"),
                T0.AddSeconds(8));
            FactStore.Remember(connection,
                new FactWrite("/knowledge/beta/x", "note", "decided", "Another predicate.", "user", "stated"),
                T0.AddSeconds(9));
            var closedLater = Write(connection, "/knowledge/gamma/closed", "Closed later.", "project", 10);
            FactStore.Forget(connection, closedLater, "test", T0.AddSeconds(11));

            SessionFacts.Append(connection, "live-session", "A live session note.", null, null, null, T0.AddSeconds(12));
            var closedNote = SessionFacts.Append(
                connection, "closed-session", "A note later retracted.", null, null, null, T0.AddSeconds(13));
            FactStore.Forget(connection, closedNote, "test", T0.AddSeconds(14));

            DirectiveFacts.Add(connection, "Always answer in plain English.", T0.AddSeconds(15));
        });

    /// <summary>
    /// Tool-scope records (D87) are neither counted nor sampled, including a subject whose only
    /// live facts are tool records and the lowest-id fact of the whole store being one — the cases
    /// the histogram's negative arm, its HAVING, and both candidate arms each have to handle.
    /// </summary>
    [Fact]
    public void ToolScopeRecordsAreNeitherCountedNorSampled()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "/tools/ah2/repo/t/train/1", "Ledger first in the catalog.", FactStore.ToolScope, 1);
        Write(connection, "/tools/ah2/repo/t/train/2", "Another ledger row.", FactStore.ToolScope, 2);
        Write(connection, "/knowledge/mixed/a", "A tool record shares this subject.", FactStore.ToolScope, 3);
        FactStore.Remember(connection,
            new FactWrite("/knowledge/mixed/a", "note", "decided", "A user belief on it.", "user", "stated"),
            T0.AddSeconds(4));
        Write(connection, "/knowledge/alpha/b", "Project body.", "project", 5);

        AssertPrimersMatch(connection);

        var summary = PrimerSummary.Read(connection, Now);
        Assert.Equal(2, summary.FactCount);
        Assert.Equal(2, summary.TopicCounts.Values.Sum());
        Assert.DoesNotContain(summary.ExampleCandidates, fact => fact.Scope == FactStore.ToolScope);
        Assert.DoesNotContain("Ledger", PrimerBuilder.Build(summary, MemorySettings.DefaultPrecedence), StringComparison.Ordinal);
    }

    private static void AssertPrimersMatch(bool seeded, Action<SqliteConnection> arrange)
    {
        using var sandbox = new SandboxHome(initialize: false);
        if (seeded)
        {
            EngramInitializer.Initialize(sandbox.Home);
        }

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        arrange(connection);

        AssertPrimersMatch(connection);
    }

    private static void AssertPrimersMatch(SqliteConnection connection)
    {
        // ReadLongTerm keeps directives as ordinary facts; the primer delivers them in their own
        // block instead (D-1), so the reference takes them out of the catalog and hands them over
        // as Directives — a no-op for every fixture that writes none. Tool-scope records stay in
        // the catalog (recall reads it) but not in the primer (D87).
        var directivePrefix = DirectiveFacts.Root + "/";
        var reference = PrimerSummary.From(FactCatalog.ReadLongTerm(connection, Now)
                .Where(fact => fact.SubjectPath?.StartsWith(directivePrefix, StringComparison.Ordinal) != true)
                .Where(fact => fact.Scope != FactStore.ToolScope)
                .ToList())
            with { Directives = DirectiveFacts.ReadLive(connection).Select(d => d.Body).ToList() };
        var summary = PrimerSummary.Read(connection, Now);

        foreach (var precedence in Precedences)
        {
            Assert.Equal(
                PrimerBuilder.Build(reference, precedence),
                PrimerBuilder.Build(summary, precedence));
            Assert.Equal(
                PrimerBuilder.BuildForSubagent(reference, precedence),
                PrimerBuilder.BuildForSubagent(summary, precedence));
        }

        // Two empty strings are equal, so every corpus that holds something is also held to
        // producing a coverage line — otherwise a summary that read nothing would pass by
        // agreeing with a reference the test never checked was non-trivial.
        if (reference.FactCount > 0)
        {
            Assert.Contains(
                "Memory holds",
                PrimerBuilder.Build(summary, MemorySettings.DefaultPrecedence));
        }
    }

    private static long Write(
        SqliteConnection connection, string path, string body, string scope, int second) =>
        FactStore.Remember(
            connection,
            new FactWrite(path, "note", "notes", body, scope, "stated"),
            T0.AddSeconds(second)).FactId;

    private static void Edge(
        SqliteConnection connection, string subject, string target, string predicate, int second) =>
        FactStore.Remember(
            connection,
            new FactWrite(subject, "note", predicate, $"{subject} {predicate} {target}.", "project", "stated",
                ObjectPath: target, ObjectKind: "note"),
            T0.AddSeconds(second));
}
