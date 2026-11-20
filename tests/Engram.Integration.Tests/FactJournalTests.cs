using System.Text.Json.Nodes;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// The journal against real stores: what it writes, and whether what it writes can rebuild a store
/// the snapshots could not have restored.
/// </summary>
public sealed class FactJournalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static long Write(SqliteConnection connection, string path, string body, DateTimeOffset at, string? details = null) =>
        FactStore.Remember(connection, new FactWrite(path, "note", "states", body, "project", "stated", Details: details), at).FactId;

    private static IReadOnlyList<JournalFact> Reread(EngramHome home)
    {
        var facts = FactJournal.Parse(File.ReadLines(FactJournal.PathIn(home)), out var skipped);
        Assert.Equal(0, skipped);
        return facts;
    }

    private static IReadOnlyList<string> Bodies(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM fact ORDER BY id;";
        using var reader = command.ExecuteReader();

        var bodies = new List<string>();
        while (reader.Read())
        {
            bodies.Add(reader.GetString(0));
        }

        return bodies;
    }

    [Fact]
    public void Write_PutsEveryFactOnItsOwnLine()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Write(connection, "/project/a", "the first thing", T0);
        Write(connection, "/project/b", "the second thing", T0);

        var written = FactJournal.Write(connection, sandbox.Home, T0, BackupScope.Full);

        Assert.Equal(2, written);
        var lines = File.ReadAllLines(FactJournal.PathIn(sandbox.Home));
        Assert.Equal(3, lines.Length);
        Assert.Contains("engram-facts", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheHeader_NamesTheSchemaThatWroteIt()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "/project/a", "something", T0);

        FactJournal.Write(connection, sandbox.Home, T0, BackupScope.Full);

        var header = JsonNode.Parse(File.ReadLines(FactJournal.PathIn(sandbox.Home)).First())!.AsObject();
        Assert.Equal(EngramDatabase.SchemaVersion, header["schema_version"]!.GetValue<int>());
        Assert.Equal(FactJournal.FormatVersion, header["format_version"]!.GetValue<int>());
    }

    [Fact]
    public void Write_CarriesTheFieldsAReplayNeeds()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        FactStore.Remember(
            connection,
            new FactWrite("/project/a", "note", "depends-on", "the body", "project", "observed", "src/x.cs:12"),
            T0);

        FactJournal.Write(connection, sandbox.Home, T0, BackupScope.Full);

        var fact = Assert.Single(Reread(sandbox.Home));
        Assert.Equal("/project/a", fact.Subject);
        Assert.Equal("note", fact.SubjectKind);
        Assert.Equal("depends-on", fact.Predicate);
        Assert.Equal("the body", fact.Body);
        Assert.Equal("project", fact.Scope);
        Assert.Equal("observed", fact.LearnedVia);
        Assert.Equal("src/x.cs:12", fact.Evidence);
        Assert.Equal(T0.ToUnixTimeSeconds(), fact.ValidFrom);
        Assert.Null(fact.ValidTo);
    }

    [Fact]
    public void Write_IncludesFactsThatAreNoLongerBelieved()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Write(connection, "/project/a", "the old belief", T0);
        Write(connection, "/project/a", "the new belief", T0.AddHours(1));

        FactJournal.Write(connection, sandbox.Home, T0.AddHours(2), BackupScope.Full);

        var facts = Reread(sandbox.Home);
        Assert.Equal(2, facts.Count);
        var closed = Assert.Single(facts, f => f.ValidTo is not null);
        Assert.Equal("the old belief", closed.Body);
        Assert.NotNull(closed.SupersededBy);
        Assert.NotNull(closed.SupersessionReason);
    }

    [Fact]
    public void Write_LeavesNoPartialFileBehind()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "/project/a", "something", T0);

        FactJournal.Write(connection, sandbox.Home, T0, BackupScope.Full);

        Assert.Empty(Directory.GetFiles(sandbox.Home.BackupDir, "*.partial"));
    }

    [Fact]
    public void Write_ReplacesTheJournalRatherThanGrowingIt()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Write(connection, "/project/a", "one", T0);
        FactJournal.Write(connection, sandbox.Home, T0, BackupScope.Full);

        Write(connection, "/project/b", "two", T0.AddMinutes(1));
        FactJournal.Write(connection, sandbox.Home, T0.AddMinutes(1), BackupScope.Full);

        Assert.Equal(2, Reread(sandbox.Home).Count);
    }

    [Fact]
    public void Replay_RebuildsAStoreThatWasEmpty()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "the first thing", T0);
            Write(connection, "/project/b", "the second thing", T0.AddMinutes(1));
            FactJournal.Write(connection, source.Home, T0.AddMinutes(2), BackupScope.Full);
        }

        var facts = Reread(source.Home);

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var result = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(2, result.Written);
        Assert.Equal(0, result.AlreadyPresent);
        Assert.Equal(["the first thing", "the second thing"], Bodies(rebuilt));
    }

    [Fact]
    public void Replay_LeavesTheReplayedFactsFindable()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/parsers", "the tokenizer keeps its own line counter", T0);
            FactJournal.Write(connection, source.Home, T0, BackupScope.Full);
        }

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        FactJournal.Replay(rebuilt, Reread(source.Home), apply: true);

        // The lexical index is maintained by triggers on `fact`, so a replayed fact is only
        // findable if it went in through a real insert rather than around one.
        using var command = rebuilt.CreateCommand();
        command.CommandText = "SELECT body FROM fact_fts WHERE fact_fts MATCH 'tokenizer';";
        Assert.Equal("the tokenizer keeps its own line counter", (string)command.ExecuteScalar()!);
    }

    [Fact]
    public void Replay_KeepsTheChainFromTheOldBeliefToTheNewOne()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "the old belief", T0);
            Write(connection, "/project/a", "the new belief", T0.AddHours(1));
            FactJournal.Write(connection, source.Home, T0.AddHours(2), BackupScope.Full);
        }

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        FactJournal.Replay(rebuilt, Reread(source.Home), apply: true);

        using var command = rebuilt.CreateCommand();
        command.CommandText =
            """
            SELECT old.body, new.body, s.reason
            FROM supersession s
            JOIN fact old ON old.id = s.old_fact_id
            JOIN fact new ON new.id = s.new_fact_id;
            """;
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal("the old belief", reader.GetString(0));
        Assert.Equal("the new belief", reader.GetString(1));
        Assert.False(reader.IsDBNull(2));
        Assert.False(reader.Read());
    }

    [Fact]
    public void Replay_LeavesOnlyTheNewestBeliefLive()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "the old belief", T0);
            Write(connection, "/project/a", "the new belief", T0.AddHours(1));
            FactJournal.Write(connection, source.Home, T0.AddHours(2), BackupScope.Full);
        }

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        FactJournal.Replay(rebuilt, Reread(source.Home), apply: true);

        using var command = rebuilt.CreateCommand();
        command.CommandText = "SELECT body FROM fact WHERE valid_to IS NULL;";
        Assert.Equal("the new belief", (string)command.ExecuteScalar()!);
    }

    [Fact]
    public void Replay_Twice_WritesNothingTheSecondTime()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "the old belief", T0);
            Write(connection, "/project/a", "the new belief", T0.AddHours(1));
            FactJournal.Write(connection, source.Home, T0.AddHours(2), BackupScope.Full);
        }

        var facts = Reread(source.Home);

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        FactJournal.Replay(rebuilt, facts, apply: true);
        var second = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(0, second.Written);
        Assert.Equal(2, second.AlreadyPresent);
        Assert.Equal(2, Bodies(rebuilt).Count);
    }

    [Fact]
    public void Replay_WithoutApply_ChangesNothing()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "something", T0);
            FactJournal.Write(connection, source.Home, T0, BackupScope.Full);
        }

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var plan = FactJournal.Replay(rebuilt, Reread(source.Home), apply: false);

        Assert.Equal(1, plan.Written);
        Assert.Empty(Bodies(rebuilt));
    }

    [Fact]
    public void Replay_LeavesFactsTheStoreAlreadyHadAlone()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "shared", T0);
            Write(connection, "/project/b", "only in the journal", T0.AddMinutes(1));
            FactJournal.Write(connection, source.Home, T0.AddMinutes(2), BackupScope.Full);
        }

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        Write(rebuilt, "/project/a", "shared", T0);

        var result = FactJournal.Replay(rebuilt, Reread(source.Home), apply: true);

        Assert.Equal(1, result.Written);
        Assert.Equal(1, result.AlreadyPresent);
        Assert.Equal(["shared", "only in the journal"], Bodies(rebuilt));
    }

    [Fact]
    public void Replay_CarriesTheOriginalTimestampsRatherThanStampingNow()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "something", T0);
            FactJournal.Write(connection, source.Home, T0, BackupScope.Full);
        }

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        FactJournal.Replay(rebuilt, Reread(source.Home), apply: true);

        using var command = rebuilt.CreateCommand();
        command.CommandText = "SELECT valid_from FROM fact;";
        Assert.Equal(T0.ToUnixTimeSeconds(), (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void Write_AndReplay_CarriesDetailsIntact()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "the short version", T0, details: "the long version, verbatim.");
            FactJournal.Write(connection, source.Home, T0, BackupScope.Full);
        }

        var facts = Reread(source.Home);
        Assert.Equal("the long version, verbatim.", Assert.Single(facts).Details);

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        FactJournal.Replay(rebuilt, facts, apply: true);

        var replayed = Assert.Single(FactStore.ReadLive(rebuilt));
        Assert.Equal("the long version, verbatim.", replayed.Details);
    }

    [Fact]
    public void Parse_AJournalLineWrittenBeforeDetailsExisted_ParsesWithDetailsNull()
    {
        var facts = FactJournal.Parse(
            [
                """{"id":7,"subject":"/project/a","kind":"note","predicate":"states","body":"written before details existed","scope":"project","learned_via":"stated","valid_from":1767225600,"created_at":1767225600}""",
            ],
            out var skipped);

        Assert.Equal(0, skipped);
        Assert.Null(Assert.Single(facts).Details);
    }

    /// <summary>
    /// Replay identity is subject + predicate + body + <c>valid_from</c> — details rides along on
    /// insert but is never compared, so a body match with differing details is still
    /// <c>AlreadyPresent</c> rather than a conflict, and the target's own details survive untouched.
    /// </summary>
    [Fact]
    public void Replay_WhenBodyMatchesButDetailsDiffer_CountsAlreadyPresentAndLeavesTheTargetUntouched()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "shared body", T0, details: "the journal's details");
            FactJournal.Write(connection, source.Home, T0, BackupScope.Full);
        }

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var targetId = Write(rebuilt, "/project/a", "shared body", T0, details: "the target's own details");

        var result = FactJournal.Replay(rebuilt, Reread(source.Home), apply: true);

        Assert.Equal(0, result.Written);
        Assert.Equal(1, result.AlreadyPresent);

        var targetFact = FactStore.ReadById(rebuilt, targetId);
        Assert.NotNull(targetFact);
        Assert.Equal("the target's own details", targetFact.Details);
    }

    [Fact]
    public void Parse_SkipsAMangledLineRatherThanLosingTheRest()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "/project/a", "the first thing", T0);
        Write(connection, "/project/b", "the second thing", T0.AddMinutes(1));
        FactJournal.Write(connection, sandbox.Home, T0.AddMinutes(2), BackupScope.Full);

        var path = FactJournal.PathIn(sandbox.Home);
        var lines = File.ReadAllLines(path);
        lines[1] = lines[1][..(lines[1].Length / 2)];
        File.WriteAllLines(path, lines);

        var facts = FactJournal.Parse(File.ReadLines(path), out var skipped);

        Assert.Equal(1, skipped);
        Assert.Equal("the second thing", Assert.Single(facts).Body);
    }

    [Fact]
    public void Parse_IgnoresBlankLines()
    {
        var facts = FactJournal.Parse(["", "   ", ""], out var skipped);

        Assert.Empty(facts);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void Replay_IntoAStoreAtANewerSchema_StillLands()
    {
        // The point of the whole tier: a `.db` snapshot from an older schema is refused, but the
        // text is addressed by path and predicate, so it does not care what version wrote it.
        using var sandbox = new SandboxHome(initialize: false);
        Directory.CreateDirectory(sandbox.Home.BackupDir);
        File.WriteAllLines(
            FactJournal.PathIn(sandbox.Home),
            [
                """{"format":"engram-facts","format_version":1,"schema_version":1,"written_at":"2026-01-01T00:00:00Z"}""",
                """{"id":7,"subject":"/project/a","kind":"note","predicate":"states","body":"written by an older engram","scope":"project","learned_via":"stated","valid_from":1767225600,"created_at":1767225600}""",
            ]);

        var facts = FactJournal.Parse(File.ReadLines(FactJournal.PathIn(sandbox.Home)), out var skipped);
        Assert.Equal(0, skipped);

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        FactJournal.Replay(connection, facts, apply: true);

        Assert.Equal(["written by an older engram"], Bodies(connection));
    }

    [Fact]
    public void Replay_OfAFactWhoseSupersederIsMissing_KeepsTheFactAndReportsTheGap()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var facts = new List<JournalFact>
        {
            new(7, "/project/a", "note", "states", "closed, but by what?", null, null, "project", "stated",
                false, null, T0.ToUnixTimeSeconds(), T0.AddHours(1).ToUnixTimeSeconds(), 99, "revised", T0.ToUnixTimeSeconds()),
        };

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var result = FactJournal.Replay(connection, facts, apply: true);

        Assert.Equal(1, result.Written);
        Assert.Equal(1, result.Unresolved);
        Assert.Equal(["closed, but by what?"], Bodies(connection));
    }

    // ---- a target that believes something else ----

    /// <summary>
    /// The reported defect. <c>ux_fact_live</c> permits one live fact per subject and predicate, so
    /// a journalled belief that disagrees with the target's cannot be inserted — and the insert used
    /// to raise SQLITE_CONSTRAINT and abort the whole replay, recovering nothing.
    /// </summary>
    [Fact]
    public void Replay_WhenTheTargetBelievesSomethingElse_SkipsThatFactRatherThanFailing()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "what the journal remembers", T0);
            Write(connection, "/project/b", "only in the journal", T0.AddMinutes(1));
            FactJournal.Write(connection, source.Home, T0.AddMinutes(2), BackupScope.Full);
        }

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        Write(rebuilt, "/project/a", "what the store believes now", T0.AddHours(1));

        var result = FactJournal.Replay(rebuilt, Reread(source.Home), apply: true);

        Assert.Equal(1, result.Conflicted);
        Assert.Equal(1, result.Written);
        Assert.Equal(0, result.AlreadyPresent);

        // The target's belief is untouched — replay may never close one to make room (D8) — and
        // everything that did not collide was still recovered.
        Assert.Equal(["what the store believes now", "only in the journal"], Bodies(rebuilt));
    }

    /// <summary>
    /// The shape the bug was found in: a journal replayed into a home that has been through
    /// <c>init</c>, so the seeded corpus is already there under the same subjects and predicates.
    /// Before the fix this recovered nothing at all.
    /// </summary>
    [Fact]
    public void Replay_IntoAnInitialisedHome_RecoversWhatDoesNotCollide()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/mine", "a fact only this store has", T0);
            FactJournal.Write(connection, source.Home, T0, BackupScope.Full);
        }

        // Initialised, so the store arrives holding the canned corpus rather than nothing.
        using var target = new SandboxHome();
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var before = Bodies(rebuilt).Count;
        Assert.True(before > 0, "an initialised home was expected to arrive seeded");

        var journal = Reread(source.Home);
        foreach (var seeded in FactJournal.Read(rebuilt).Take(3))
        {
            // Same subject and predicate, different body: what a second install's seed looks like
            // to the first install's journal.
            journal = [.. journal, seeded with { Body = seeded.Body + " (as the journal had it)" }];
        }

        var result = FactJournal.Replay(rebuilt, journal, apply: true);

        Assert.Equal(3, result.Conflicted);
        Assert.Equal(1, result.Written);
        Assert.Equal(before + 1, Bodies(rebuilt).Count);
    }

    /// <summary>
    /// A dry run that under-reports conflicts would promise a recovery the apply cannot deliver.
    /// </summary>
    [Fact]
    public void Replay_ADryRun_ReportsTheSameConflictsTheApplyFinds()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "what the journal remembers", T0);
            Write(connection, "/project/b", "only in the journal", T0.AddMinutes(1));
            FactJournal.Write(connection, source.Home, T0.AddMinutes(2), BackupScope.Full);
        }

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        Write(rebuilt, "/project/a", "what the store believes now", T0.AddHours(1));

        var facts = Reread(source.Home);
        var dry = FactJournal.Replay(rebuilt, facts, apply: false);
        var applied = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(applied.Conflicted, dry.Conflicted);
        Assert.Equal(applied.Written, dry.Written);
    }

    /// <summary>
    /// Only live facts can collide — the uniqueness index is partial on <c>valid_to IS NULL</c> —
    /// so a closed fact lands beside whatever the target believes now and adds to the record of how
    /// the belief got there rather than competing with it.
    /// </summary>
    [Fact]
    public void Replay_AClosedJournalFact_LandsBesideTheTargetsLiveBelief()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "what was believed first", T0);
            Write(connection, "/project/a", "what replaced it", T0.AddMinutes(1));
            FactJournal.Write(connection, source.Home, T0.AddMinutes(2), BackupScope.Full);
        }

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        Write(rebuilt, "/project/a", "what this store believes", T0.AddHours(1));

        var result = FactJournal.Replay(rebuilt, Reread(source.Home), apply: true);

        // The closed one is written, the live one collides.
        Assert.Equal(1, result.Written);
        Assert.Equal(1, result.Conflicted);
        Assert.Contains("what was believed first", Bodies(rebuilt));
    }

    /// <summary>
    /// Two live facts for one subject and predicate can only reach a journal through a merged
    /// bundle, and the second must be reported rather than taking the replay down at the index.
    /// </summary>
    /// <remarks>
    /// The dry run is the half that has to be asserted, and the first version of this test asserted
    /// only the apply — which passed with the in-journal check deleted, because an apply sees its
    /// own inserts through the transaction and resolves the collision without it. A dry run inserts
    /// nothing, so it is the only caller that needs the pairs tracked, and the only one whose
    /// numbers change when the tracking goes.
    /// </remarks>
    [Fact]
    public void Replay_AJournalHoldingTwoLiveFactsForOneSubject_KeepsTheFirstAndReportsTheSecond()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "the one that arrived first", T0);
            FactJournal.Write(connection, source.Home, T0, BackupScope.Full);
        }

        var facts = Reread(source.Home);
        IReadOnlyList<JournalFact> merged =
            [.. facts, .. facts.Select(f => f with { Id = f.Id + 1000, Body = "a second opinion" })];

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        var dry = FactJournal.Replay(rebuilt, merged, apply: false);
        Assert.Equal(1, dry.Written);
        Assert.Equal(1, dry.Conflicted);

        var applied = FactJournal.Replay(rebuilt, merged, apply: true);
        Assert.Equal(1, applied.Written);
        Assert.Equal(1, applied.Conflicted);
        Assert.Equal(["the one that arrived first"], Bodies(rebuilt));
    }

    // ---- D68: a supersession may only be written into a row this replay inserted ----

    private static long SeedRawFact(
        SqliteConnection connection,
        string path,
        string predicate,
        string body,
        DateTimeOffset validFrom,
        DateTimeOffset? validTo = null,
        long? supersededBy = null)
    {
        using var entity = connection.CreateCommand();
        entity.CommandText =
            """
            INSERT INTO entity (path, kind, name, created_at) VALUES ($path, 'note', $name, $createdAt)
            ON CONFLICT(path) DO UPDATE SET path = excluded.path
            RETURNING id;
            """;
        entity.Parameters.AddWithValue("$path", path);
        entity.Parameters.AddWithValue("$name", path);
        entity.Parameters.AddWithValue("$createdAt", validFrom.ToUnixTimeSeconds());
        var subjectId = (long)entity.ExecuteScalar()!;

        using var fact = connection.CreateCommand();
        fact.CommandText =
            """
            INSERT INTO fact (subject_id, predicate, body, path, scope, learned_via, regenerable,
                              valid_from, valid_to, superseded_by, created_at)
            VALUES ($subject, $predicate, $body, $path, 'project', 'stated', 0,
                    $validFrom, $validTo, $supersededBy, $createdAt)
            RETURNING id;
            """;
        fact.Parameters.AddWithValue("$subject", subjectId);
        fact.Parameters.AddWithValue("$predicate", predicate);
        fact.Parameters.AddWithValue("$body", body);
        fact.Parameters.AddWithValue("$path", path);
        fact.Parameters.AddWithValue("$validFrom", validFrom.ToUnixTimeSeconds());
        fact.Parameters.AddWithValue("$validTo", (object?)validTo?.ToUnixTimeSeconds() ?? DBNull.Value);
        fact.Parameters.AddWithValue("$supersededBy", (object?)supersededBy ?? DBNull.Value);
        fact.Parameters.AddWithValue("$createdAt", validFrom.ToUnixTimeSeconds());
        var factId = (long)fact.ExecuteScalar()!;

        if (validTo is null)
        {
            FactTokenIndex.Add(connection, null!, factId);
        }

        return factId;
    }

    private static void SeedSupersession(
        SqliteConnection connection, long oldFactId, long newFactId, DateTimeOffset at, string reason = "test fixture")
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO supersession (old_fact_id, new_fact_id, reason, created_at)
            VALUES ($old, $new, $reason, $createdAt);
            """;
        command.Parameters.AddWithValue("$old", oldFactId);
        command.Parameters.AddWithValue("$new", newFactId);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$createdAt", at.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    private static long? SupersededByOf(SqliteConnection connection, long factId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT superseded_by FROM fact WHERE id = $id;";
        command.Parameters.AddWithValue("$id", factId);
        return command.ExecuteScalar() as long?;
    }

    private static long? ValidToOf(SqliteConnection connection, long factId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT valid_to FROM fact WHERE id = $id;";
        command.Parameters.AddWithValue("$id", factId);
        return command.ExecuteScalar() as long?;
    }

    private static int SupersessionRowCount(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM supersession;";
        return (int)(long)command.ExecuteScalar()!;
    }

    private static int FactRowCount(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM fact;";
        return (int)(long)command.ExecuteScalar()!;
    }

    private static int FactRowCountFor(SqliteConnection connection, string path, string body)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM fact f JOIN entity e ON e.id = f.subject_id
            WHERE e.path = $path AND f.body = $body;
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$body", body);
        return (int)(long)command.ExecuteScalar()!;
    }

    private static bool HasTokenRow(SqliteConnection connection, long factId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM fact_token WHERE fact_id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", factId);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Test 1 of D68 §7: a planted duplicate pair, one closed-and-superseded, one live, sharing
    /// (subject, predicate, body, valid_from). The address resolves to the live row (the only
    /// basis D68 §4.2 permits), and the target already disagrees with what the journal claims
    /// about it, so the outcome is a decline — nothing about either row's chain moves.
    /// </summary>
    [Fact]
    public void Replay_ADuplicatePairWithOneLiveOneClosed_LeavesTheChainAndTheLiveRowUnchanged()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        var supersederId = SeedRawFact(rebuilt, "/project/other", "states", "the original superseder", T0);
        var closedId = SeedRawFact(
            rebuilt, "/project/dup", "states", "a duplicated belief", T0,
            validTo: T0.AddHours(1), supersededBy: supersederId);
        SeedSupersession(rebuilt, closedId, supersederId, T0.AddHours(1));
        var liveId = SeedRawFact(rebuilt, "/project/dup", "states", "a duplicated belief", T0);

        var factRowsBefore = FactRowCount(rebuilt);
        var supersessionRowsBefore = SupersessionRowCount(rebuilt);

        var facts = new List<JournalFact>
        {
            new(1, "/project/dup", "note", "states", "a duplicated belief", null, null, "project", "stated",
                false, null, T0.ToUnixTimeSeconds(), T0.AddHours(1).ToUnixTimeSeconds(), 2, "revised",
                T0.ToUnixTimeSeconds()),
            new(2, "/project/fresh", "note", "states", "a fact only the journal has", null, null, "project",
                "stated", false, null, T0.AddMinutes(5).ToUnixTimeSeconds(), null, null, null,
                T0.AddMinutes(5).ToUnixTimeSeconds()),
        };

        var result = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(1, result.Conflicted);
        Assert.Equal(supersederId, SupersededByOf(rebuilt, closedId));
        Assert.Null(SupersededByOf(rebuilt, liveId));
        Assert.Null(ValidToOf(rebuilt, liveId));
        Assert.Equal(supersessionRowsBefore, SupersessionRowCount(rebuilt));
        Assert.True(HasTokenRow(rebuilt, liveId));

        // Only the fresh fact was written; the ambiguous pair was left exactly as seeded.
        Assert.Equal(factRowsBefore + 1, FactRowCount(rebuilt));
    }

    /// <summary>
    /// Test 2 of D68 §7: replaying into a store that already fully agrees with the journal — its
    /// fact rows and its supersession row already match — is a genuine fixed point. Two separate
    /// <see cref="FactJournal.Replay"/> invocations (not one transaction reused: <c>claimed</c> is
    /// dry-run-only, per the existing "two live facts" test) must return byte-for-byte identical
    /// results, and change no row.
    /// </summary>
    [Fact]
    public void Replay_TwiceAgainstAnAlreadyLinkedStore_ReturnsIdenticalCountsBothTimes()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        var newId = SeedRawFact(rebuilt, "/project/a", "states", "the new belief", T0.AddHours(1));
        var oldId = SeedRawFact(
            rebuilt, "/project/a", "states", "the old belief", T0,
            validTo: T0.AddHours(1), supersededBy: newId);
        SeedSupersession(rebuilt, oldId, newId, T0.AddHours(1));

        var facts = new List<JournalFact>
        {
            new(1, "/project/a", "note", "states", "the old belief", null, null, "project", "stated",
                false, null, T0.ToUnixTimeSeconds(), T0.AddHours(1).ToUnixTimeSeconds(), 2, "revised",
                T0.ToUnixTimeSeconds()),
            new(2, "/project/a", "note", "states", "the new belief", null, null, "project", "stated",
                false, null, T0.AddHours(1).ToUnixTimeSeconds(), null, null, null,
                T0.AddHours(1).ToUnixTimeSeconds()),
        };

        var factRowsBefore = FactRowCount(rebuilt);
        var supersessionRowsBefore = SupersessionRowCount(rebuilt);

        var first = FactJournal.Replay(rebuilt, facts, apply: true);
        var second = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(first, second);
        Assert.Equal(0, second.Written);
        Assert.Equal(factRowsBefore, FactRowCount(rebuilt));
        Assert.Equal(supersessionRowsBefore, SupersessionRowCount(rebuilt));
        Assert.Equal(newId, SupersededByOf(rebuilt, oldId));
    }

    /// <summary>
    /// Test 3 of D68 §7 — the falsification for the rejected row-state guard (§3.1). A fact closed
    /// with <c>superseded_by</c> NULL and no <c>supersession</c> row is exactly the shape
    /// <c>Forget</c> leaves, and is structurally identical to a row this replay just inserted — only
    /// provenance tells them apart. Proved to fail under the rejected guard: temporarily replacing
    /// the <c>inserted.Contains(fact.Id)</c> provenance check in <c>FactJournal.Replay</c> with an
    /// unconditional <c>Link(...)</c> call (relying solely on <c>Link</c>'s SQL assertion predicate,
    /// which this forgotten row also satisfies) reddens this test — confirming the provenance check,
    /// not the SQL predicate, is what this guards. Restored after confirming.
    /// </summary>
    /// <remarks>
    /// Also test 5 of D68 §7 (post-architect-ruling): the same declined link counts both
    /// <c>AlreadyPresent</c> (the body matched in pass 1) and <c>Conflicted</c> (the edge did not,
    /// in pass 2) — the two counters describe different statements about the same record and are
    /// not mutually exclusive.
    /// </remarks>
    [Fact]
    public void Replay_OfASupersessionTargetingAForgottenRow_DeclinesRatherThanFabricatingOne()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        var forgottenId = SeedRawFact(
            rebuilt, "/project/gone", "states", "a forgotten belief", T0, validTo: T0.AddHours(1));

        var facts = new List<JournalFact>
        {
            new(1, "/project/gone", "note", "states", "a forgotten belief", null, null, "project", "stated",
                false, null, T0.ToUnixTimeSeconds(), T0.AddHours(1).ToUnixTimeSeconds(), 2, "revised",
                T0.ToUnixTimeSeconds()),
            new(2, "/project/replacement", "note", "states", "what allegedly replaced it", null, null,
                "project", "stated", false, null, T0.AddMinutes(30).ToUnixTimeSeconds(), null, null, null,
                T0.AddMinutes(30).ToUnixTimeSeconds()),
        };

        var result = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(1, result.AlreadyPresent);
        Assert.Equal(1, result.Conflicted);
        Assert.Null(SupersededByOf(rebuilt, forgottenId));
        Assert.Equal(0, SupersessionRowCount(rebuilt));
    }

    /// <summary>
    /// Test 4 of D68 §7 — the falsification for the ambiguous-address case (§4.2). Two closed rows
    /// share the tuple with no live member, so no basis exists to prefer either as the address a
    /// supersession would point at. Proved to fail: temporarily forcing <c>Existing()</c>'s
    /// <c>AddressUsable</c> to always be true (the pre-fix <c>LIMIT 1</c>, no-<c>ORDER BY</c>
    /// behaviour) flips the outcome from <c>Unresolved</c> to <c>Conflicted</c>, reddening this
    /// test. Restored after confirming. Replayed twice to prove §4.1's other half: ambiguity never
    /// suppresses presence, so no third copy is ever inserted.
    /// </summary>
    [Fact]
    public void Replay_OfASupersessionTargetingAnAllClosedAmbiguousPair_ReportsUnresolvedAndInsertsNoThirdCopy()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        var firstClosed = SeedRawFact(
            rebuilt, "/project/dup2", "states", "an ambiguous closed belief", T0, validTo: T0.AddHours(1));
        var secondClosed = SeedRawFact(
            rebuilt, "/project/dup2", "states", "an ambiguous closed belief", T0, validTo: T0.AddHours(2));

        var facts = new List<JournalFact>
        {
            new(1, "/project/dup2", "note", "states", "an ambiguous closed belief", null, null, "project",
                "stated", false, null, T0.ToUnixTimeSeconds(), T0.AddHours(1).ToUnixTimeSeconds(), 2, "revised",
                T0.ToUnixTimeSeconds()),
            new(2, "/project/other2", "note", "states", "an unrelated replacement", null, null, "project",
                "stated", false, null, T0.AddMinutes(10).ToUnixTimeSeconds(), null, null, null,
                T0.AddMinutes(10).ToUnixTimeSeconds()),
        };

        var first = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(1, first.Unresolved);
        Assert.Null(SupersededByOf(rebuilt, firstClosed));
        Assert.Null(SupersededByOf(rebuilt, secondClosed));
        Assert.Equal(2, FactRowCountFor(rebuilt, "/project/dup2", "an ambiguous closed belief"));

        var second = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(1, second.Unresolved);
        Assert.Equal(0, second.Written);
        Assert.Equal(2, FactRowCountFor(rebuilt, "/project/dup2", "an ambiguous closed belief"));
    }

    // ---- D99: a forgotten fact keeps its retraction, and only in a row this replay inserted ----

    private const string Gone = "/project/forgotten";
    private const string GoneBody = "a belief later forgotten";

    private static JournalFact Record(
        long id, string body, DateTimeOffset validFrom, DateTimeOffset? validTo, long? supersededBy, string? reason,
        string path = Gone) =>
        new(id, path, "note", "states", body, null, null, "project", "stated", false, null,
            validFrom.ToUnixTimeSeconds(), validTo?.ToUnixTimeSeconds(), supersededBy, reason,
            validFrom.ToUnixTimeSeconds());

    private static JournalFact Retraction(long id = 1, string body = GoneBody) =>
        Record(id, body, T0, T0.AddHours(1), supersededBy: null, reason: "no longer true");

    private static void SeedRetractionRow(SqliteConnection connection, long oldFactId, DateTimeOffset at, string reason)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO supersession (old_fact_id, new_fact_id, reason, created_at)
            VALUES ($old, NULL, $reason, $createdAt);
            """;
        command.Parameters.AddWithValue("$old", oldFactId);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$createdAt", at.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    /// <summary>Every column of every fact and supersession row, so "untouched" means untouched (I1).</summary>
    private static string Snapshot(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 'f', id, subject_id, predicate, body, object_id, path, scope, learned_via, regenerable,
                   evidence, session_id, valid_from, valid_to, superseded_by, created_at, details, analyzer_tier
              FROM fact
            UNION ALL
            SELECT 's', old_fact_id, new_fact_id, reason, evidence, session_id, created_at,
                   NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
              FROM supersession
            ORDER BY 1, 2;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var cells = new string[reader.FieldCount];
            for (var i = 0; i < cells.Length; i++)
            {
                cells[i] = reader.IsDBNull(i) ? "∅" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;
            }

            rows.Add(string.Join('|', cells));
        }

        return string.Join('\n', rows);
    }

    private sealed record SupersessionRow(long? NewFactId, string Reason, string? Evidence, long? SessionId, long CreatedAt);

    private static IReadOnlyList<SupersessionRow> SupersessionRowsFor(SqliteConnection connection, long oldFactId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT new_fact_id, reason, evidence, session_id, created_at FROM supersession WHERE old_fact_id = $id;";
        command.Parameters.AddWithValue("$id", oldFactId);
        using var reader = command.ExecuteReader();
        var rows = new List<SupersessionRow>();
        while (reader.Read())
        {
            rows.Add(new SupersessionRow(
                reader.IsDBNull(0) ? null : reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.GetInt64(4)));
        }

        return rows;
    }

    private static long FactIdFor(SqliteConnection connection, string path, string body, DateTimeOffset validFrom)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.id FROM fact f JOIN entity e ON e.id = f.subject_id
            WHERE e.path = $path AND f.body = $body AND f.valid_from = $validFrom;
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$validFrom", validFrom.ToUnixTimeSeconds());
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// N1, from a real <c>Forget</c> through a real journal: the retraction comes back with the
    /// forget's reason and time, and nothing else. Fails on the code before D99 — pass 2 only acted
    /// on records with a superseder, so the fact came back closed with no account of why.
    /// </summary>
    [Fact]
    public void Replay_OfAForgottenFact_RecreatesItsRetraction()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            var id = Write(connection, Gone, GoneBody, T0);
            Assert.True(FactStore.Forget(connection, id, "no longer true", T0.AddHours(1)));
            FactJournal.Write(connection, source.Home, T0.AddHours(2), BackupScope.Full);
        }

        var facts = Reread(source.Home);
        var record = Assert.Single(facts);
        Assert.Null(record.SupersededBy);
        Assert.Equal("no longer true", record.SupersessionReason);

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var result = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(new ReplayResult(1, 0, 0, 0), result);
        var factId = FactIdFor(rebuilt, Gone, GoneBody, T0);
        Assert.Equal(T0.AddHours(1).ToUnixTimeSeconds(), ValidToOf(rebuilt, factId));
        Assert.Null(SupersededByOf(rebuilt, factId));
        Assert.False(HasTokenRow(rebuilt, factId));
        var row = Assert.Single(SupersessionRowsFor(rebuilt, factId));
        Assert.Equal(new SupersessionRow(null, "no longer true", null, null, T0.AddHours(1).ToUnixTimeSeconds()), row);
        Assert.Equal(1, SupersessionRowCount(rebuilt));
    }

    /// <summary>N2 / I4: a second replay is a fixed point — one retraction row, nothing counted twice.</summary>
    [Fact]
    public void Replay_OfARetractionTwice_LeavesExactlyOneRetractionRow()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var facts = new List<JournalFact> { Retraction() };

        FactJournal.Replay(rebuilt, facts, apply: true);
        var before = Snapshot(rebuilt);
        var again = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(new ReplayResult(0, 1, 0, 0), again);
        Assert.Equal(1, SupersessionRowCount(rebuilt));
        Assert.Equal(before, Snapshot(rebuilt));
    }

    /// <summary>N3: the target still believes it. Never closed (D32); counted as not recovered.</summary>
    [Fact]
    public void Replay_OfARetractionTheTargetStillBelieves_LeavesItLiveAndCountsAConflict()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var liveId = SeedRawFact(rebuilt, Gone, "states", GoneBody, T0);
        var before = Snapshot(rebuilt);

        var result = FactJournal.Replay(rebuilt, [Retraction()], apply: true);

        Assert.Equal(new ReplayResult(0, 1, 0, 1), result);
        Assert.Null(ValidToOf(rebuilt, liveId));
        Assert.True(HasTokenRow(rebuilt, liveId));
        Assert.Equal(before, Snapshot(rebuilt));
    }

    /// <summary>N4: already retracted, for its own reason. Not overwritten, nothing more counted.</summary>
    [Fact]
    public void Replay_OfARetractionTheTargetAlreadyRecorded_KeepsTheTargetsReason()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var closedId = SeedRawFact(rebuilt, Gone, "states", GoneBody, T0, validTo: T0.AddHours(1));
        SeedRetractionRow(rebuilt, closedId, T0.AddHours(1), "the target's own reason");
        var before = Snapshot(rebuilt);

        var result = FactJournal.Replay(rebuilt, [Retraction()], apply: true);

        Assert.Equal(new ReplayResult(0, 1, 0, 0), result);
        Assert.Equal("the target's own reason", Assert.Single(SupersessionRowsFor(rebuilt, closedId)).Reason);
        Assert.Equal(before, Snapshot(rebuilt));
    }

    /// <summary>
    /// N5 / U1: closed with no account — the shape an older binary's replay left. No backfill: this
    /// row is structurally identical to one the target closed without explanation, and only
    /// provenance could tell them apart (D68). Visible as a conflict rather than silent. Proved to
    /// fail by swapping the provenance test for the row-state predicate (backfill appears).
    /// </summary>
    [Fact]
    public void Replay_OfARetractionIntoAClosedRowWithNoAccount_DoesNotBackfillAndCountsAConflict()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        SeedRawFact(rebuilt, Gone, "states", GoneBody, T0, validTo: T0.AddHours(1));
        var before = Snapshot(rebuilt);

        var result = FactJournal.Replay(rebuilt, [Retraction()], apply: true);

        Assert.Equal(new ReplayResult(0, 1, 0, 1), result);
        Assert.Equal(0, SupersessionRowCount(rebuilt));
        Assert.Equal(before, Snapshot(rebuilt));
    }

    /// <summary>N6: the target says a replacement closed it. Its account stands (D68).</summary>
    [Fact]
    public void Replay_OfARetractionIntoARowTheTargetSaysWasReplaced_LeavesTheEdgeAndCountsAConflict()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var replacementId = SeedRawFact(rebuilt, Gone, "states", "what replaced it", T0.AddHours(1));
        var closedId = SeedRawFact(
            rebuilt, Gone, "states", GoneBody, T0, validTo: T0.AddHours(1), supersededBy: replacementId);
        SeedSupersession(rebuilt, closedId, replacementId, T0.AddHours(1));
        var before = Snapshot(rebuilt);

        var result = FactJournal.Replay(rebuilt, [Retraction()], apply: true);

        Assert.Equal(new ReplayResult(0, 1, 0, 1), result);
        Assert.Equal(replacementId, Assert.Single(SupersessionRowsFor(rebuilt, closedId)).NewFactId);
        Assert.Equal(before, Snapshot(rebuilt));
    }

    /// <summary>N7: several closed duplicates and none live — no row to address, so nothing to say.</summary>
    [Fact]
    public void Replay_OfARetractionMatchingAnAllClosedAmbiguousPair_WritesAndConflictsNothing()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        SeedRawFact(rebuilt, Gone, "states", GoneBody, T0, validTo: T0.AddHours(1));
        SeedRawFact(rebuilt, Gone, "states", GoneBody, T0, validTo: T0.AddHours(2));
        var before = Snapshot(rebuilt);

        var result = FactJournal.Replay(rebuilt, [Retraction()], apply: true);

        Assert.Equal(new ReplayResult(0, 1, 0, 0), result);
        Assert.Equal(before, Snapshot(rebuilt));
    }

    /// <summary>
    /// N8 / I2: a superseder that is not in the journal is a lost pointer, not a retraction — a NULL
    /// row there would turn a replacement into a forget.
    /// </summary>
    [Fact]
    public void Replay_OfAFactWhoseSupersederIsMissing_WritesNoRetraction()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var facts = new List<JournalFact>
        {
            Record(1, GoneBody, T0, T0.AddHours(1), supersededBy: 99, reason: "superseded by a newer statement"),
        };

        var result = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(new ReplayResult(1, 0, 1, 0), result);
        Assert.Equal(0, SupersessionRowCount(rebuilt));
    }

    /// <summary>N9 / I2: a resolvable superseder still gets Link's edge, and only that.</summary>
    [Fact]
    public void Replay_OfAResolvableSupersession_WritesTheEdgeNotARetraction()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var facts = new List<JournalFact>
        {
            Record(1, GoneBody, T0, T0.AddHours(1), supersededBy: 2, reason: "revised"),
            Record(2, "what replaced it", T0.AddHours(1), null, null, null),
        };

        var result = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(new ReplayResult(2, 0, 0, 0), result);
        var oldId = FactIdFor(rebuilt, Gone, GoneBody, T0);
        var newId = FactIdFor(rebuilt, Gone, "what replaced it", T0.AddHours(1));
        Assert.Equal(newId, Assert.Single(SupersessionRowsFor(rebuilt, oldId)).NewFactId);
        Assert.Equal(1, SupersessionRowCount(rebuilt));
    }

    /// <summary>
    /// N10 / I3: closed with no reason means the source had no supersession row either. Replay
    /// must not invent an account the source never had.
    /// </summary>
    [Fact]
    public void Replay_OfAClosedFactWithNoReason_InventsNoRetraction()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var facts = new List<JournalFact> { Record(1, GoneBody, T0, T0.AddHours(1), null, null) };

        var result = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(new ReplayResult(1, 0, 0, 0), result);
        Assert.NotNull(ValidToOf(rebuilt, FactIdFor(rebuilt, Gone, GoneBody, T0)));
        Assert.Equal(0, SupersessionRowCount(rebuilt));
    }

    private static List<JournalFact> ForgottenThenRestated() =>
    [
        Retraction(1),
        Record(2, GoneBody, T0.AddHours(2), null, null, null),
    ];

    /// <summary>N12: forgotten, then said again later. The closed one keeps its retraction; the restatement is live.</summary>
    [Fact]
    public void Replay_OfAForgottenThenRestatedFact_KeepsBoth()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        var result = FactJournal.Replay(rebuilt, ForgottenThenRestated(), apply: true);

        Assert.Equal(new ReplayResult(2, 0, 0, 0), result);
        var oldId = FactIdFor(rebuilt, Gone, GoneBody, T0);
        var restatedId = FactIdFor(rebuilt, Gone, GoneBody, T0.AddHours(2));
        Assert.Null(Assert.Single(SupersessionRowsFor(rebuilt, oldId)).NewFactId);
        Assert.Null(ValidToOf(rebuilt, restatedId));
        Assert.Empty(SupersessionRowsFor(rebuilt, restatedId));
    }

    /// <summary>N13: the same journal against an older store that still believes the first statement.</summary>
    [Fact]
    public void Replay_OfAForgottenThenRestatedFact_IntoAStoreStillBelievingTheFirst_TouchesNothing()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var liveId = SeedRawFact(rebuilt, Gone, "states", GoneBody, T0);
        var before = Snapshot(rebuilt);

        var result = FactJournal.Replay(rebuilt, ForgottenThenRestated(), apply: true);

        Assert.Equal(new ReplayResult(0, 1, 0, 2), result);
        Assert.Null(ValidToOf(rebuilt, liveId));
        Assert.Equal(before, Snapshot(rebuilt));
    }

    /// <summary>
    /// N14 / I5: the dry run keeps the pre-D99 counting exactly — retraction state is classified on
    /// apply only, like D68's edges — and writes nothing, whatever the target holds.
    /// </summary>
    [Fact]
    public void Replay_ADryRunOfRetractions_CountsWhatItAlwaysDidAndWritesNothing()
    {
        var facts = new List<JournalFact>
        {
            Retraction(1),
            Retraction(2, "a second forgotten belief"),
            Retraction(3, "a third forgotten belief"),
        };

        using var emptyHome = new SandboxHome(initialize: false);
        using var empty = EngramDatabase.OpenInitialized(emptyHome.Home);
        Assert.Equal(new ReplayResult(3, 0, 0, 0), FactJournal.Replay(empty, facts, apply: false));
        Assert.Equal(0, FactRowCount(empty));
        Assert.Equal(0, SupersessionRowCount(empty));

        // Live (N3), closed with no account (N5), and one absent: the apply would count two conflicts.
        using var heldHome = new SandboxHome(initialize: false);
        using var held = EngramDatabase.OpenInitialized(heldHome.Home);
        SeedRawFact(held, Gone, "states", GoneBody, T0);
        SeedRawFact(held, Gone, "states", "a second forgotten belief", T0, validTo: T0.AddHours(1));
        var before = Snapshot(held);

        Assert.Equal(new ReplayResult(1, 2, 0, 0), FactJournal.Replay(held, facts, apply: false));
        Assert.Equal(before, Snapshot(held));
    }

    // §6.6, arm 1: a journal holding two live edges from one subject on one predicate replays with
    // both live and zero conflicts, and a second replay counts both AlreadyPresent with nothing
    // written.
    [Fact]
    public void Replay_TwoLiveEdgesFromOneSubjectOnOnePredicate_BothArriveLive_ZeroConflicts()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            FactStore.Remember(
                connection,
                new FactWrite("/projects/x/code/a.cs", "file", "imports", "imports react", "code", "observed",
                    Regenerable: true, ObjectPath: CodePaths.ForSymbolName("react"), ObjectKind: "symbol-name"),
                T0);
            FactStore.Remember(
                connection,
                new FactWrite("/projects/x/code/a.cs", "file", "imports", "imports lodash", "code", "observed",
                    Regenerable: true, ObjectPath: CodePaths.ForSymbolName("lodash"), ObjectKind: "symbol-name"),
                T0);
            FactJournal.Write(connection, source.Home, T0.AddMinutes(1), BackupScope.Full);
        }

        var facts = Reread(source.Home);

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var result = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(2, result.Written);
        Assert.Equal(0, result.Conflicted);
        var live = FactStore.ReadLive(rebuilt)
            .Where(f => f.SubjectPath == "/projects/x/code/a.cs" && f.Predicate == "imports")
            .ToList();
        Assert.Equal(2, live.Count);

        var again = FactJournal.Replay(rebuilt, facts, apply: true);
        Assert.Equal(0, again.Written);
        Assert.Equal(2, again.AlreadyPresent);
        Assert.Equal(0, again.Conflicted);
    }

    // §6.6, arm 2: the dry run's counts must match what apply reports for the same journal.
    [Fact]
    public void Replay_DryRunCounts_MatchTheApplysCounts_ForTwoLiveEdges()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            FactStore.Remember(
                connection,
                new FactWrite("/projects/x/code/a.cs", "file", "imports", "imports react", "code", "observed",
                    Regenerable: true, ObjectPath: CodePaths.ForSymbolName("react"), ObjectKind: "symbol-name"),
                T0);
            FactStore.Remember(
                connection,
                new FactWrite("/projects/x/code/a.cs", "file", "imports", "imports lodash", "code", "observed",
                    Regenerable: true, ObjectPath: CodePaths.ForSymbolName("lodash"), ObjectKind: "symbol-name"),
                T0);
            FactJournal.Write(connection, source.Home, T0.AddMinutes(1), BackupScope.Full);
        }

        var facts = Reread(source.Home);

        using var dryTarget = new SandboxHome(initialize: false);
        using var dryStore = EngramDatabase.OpenInitialized(dryTarget.Home);
        var dryRun = FactJournal.Replay(dryStore, facts, apply: false);

        using var applyTarget = new SandboxHome(initialize: false);
        using var applyStore = EngramDatabase.OpenInitialized(applyTarget.Home);
        var apply = FactJournal.Replay(applyStore, facts, apply: true);

        Assert.Equal(apply.Written, dryRun.Written);
        Assert.Equal(apply.AlreadyPresent, dryRun.AlreadyPresent);
        Assert.Equal(apply.Conflicted, dryRun.Conflicted);
    }

    // §6.6, arm 3 (the ruling's new arm, §6.5's regression guard): a null-object journal written
    // as if pre-v13 replays into a v13 store at the same counts it always got. Every JournalFact
    // here carries Object: null, so the replay never leaves the objectless branch of the widened
    // Existing/WouldDisplaceALiveBelief logic.
    [Fact]
    public void Replay_APreV13NullObjectJournal_MatchesTheV12Counts()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        var facts = new List<JournalFact>
        {
            new(1, "/project/a", "note", "states", "the first thing", null, null, "project", "stated",
                false, null, T0.ToUnixTimeSeconds(), null, null, null, T0.ToUnixTimeSeconds()),
            new(2, "/project/b", "note", "states", "the second thing", null, null, "project", "stated",
                false, null, T0.AddMinutes(1).ToUnixTimeSeconds(), null, null, null,
                T0.AddMinutes(1).ToUnixTimeSeconds()),
        };

        var result = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(2, result.Written);
        Assert.Equal(0, result.AlreadyPresent);
        Assert.Equal(0, result.Conflicted);
        Assert.Equal(["the first thing", "the second thing"], Bodies(rebuilt));

        var again = FactJournal.Replay(rebuilt, facts, apply: true);
        Assert.Equal(0, again.Written);
        Assert.Equal(2, again.AlreadyPresent);
        Assert.Equal(0, again.Conflicted);
    }

    // §6.6, arm 4 (edit 4's guard, §6.4): replaying an edge into an empty store gives the object
    // entity a real display name derived from its path, rather than leaving it nameless forever —
    // EnsureEntity short-circuits on an existing path, so a later index run cannot fix this after
    // the fact.
    [Fact]
    public void Replay_OfAnEdgeIntoAnEmptyStore_NamesTheObjectEntity()
    {
        // A slash in the name is percent-encoded into the path (CodePaths.ForSymbolName), so the
        // raw path's last segment ("@angular%2Fcore") diverges from the real name — a fallback to
        // LastSegment(path) instead of SymbolNameOf(path) would pass with "react" but not this.
        var objectPath = CodePaths.ForSymbolName("@angular/core");

        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            FactStore.Remember(
                connection,
                new FactWrite("/projects/x/code/a.cs", "file", "imports", "imports @angular/core", "code", "observed",
                    Regenerable: true, ObjectPath: objectPath, ObjectKind: "symbol-name"),
                T0);
            FactJournal.Write(connection, source.Home, T0.AddMinutes(1), BackupScope.Full);
        }

        var facts = Reread(source.Home);

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        FactJournal.Replay(rebuilt, facts, apply: true);

        using var command = rebuilt.CreateCommand();
        command.CommandText = "SELECT name FROM entity WHERE path = $path;";
        command.Parameters.AddWithValue("$path", objectPath);
        Assert.Equal("@angular/core", (string)command.ExecuteScalar()!);
    }

    // ---- adr-5: records pair with target rows one-to-one, liveness first (F-a table) ----

    private const string Restated = "/project/restated";
    private const string RestatedBody = "a belief forgotten and restated in one second";
    private const string EdgeObject = "/project/edge-target";

    /// <summary>A: stated and forgotten within T0 — closed, valid_to = valid_from, a reason.</summary>
    private static JournalFact SameSecondForget(long id, string? @object = null) =>
        new(id, Restated, "note", "states", RestatedBody, @object, @object is null ? null : "note", "project",
            "stated", false, null, T0.ToUnixTimeSeconds(), T0.ToUnixTimeSeconds(), null, "no longer true",
            T0.ToUnixTimeSeconds());

    /// <summary>A′: the same statement made again within T0 — live, same key as A.</summary>
    private static JournalFact SameSecondRestatement(long id, string? @object = null) =>
        new(id, Restated, "note", "states", RestatedBody, @object, @object is null ? null : "note", "project",
            "stated", false, null, T0.ToUnixTimeSeconds(), null, null, null, T0.ToUnixTimeSeconds());

    private sealed record KeyRow(long Id, long? ValidTo, long? SupersededBy);

    /// <summary>Every row holding the A/A′ key, lowest id first.</summary>
    private static List<KeyRow> KeyRows(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.id, f.valid_to, f.superseded_by FROM fact f JOIN entity e ON e.id = f.subject_id
            WHERE e.path = $path AND f.body = $body AND f.valid_from = $validFrom
            ORDER BY f.id;
            """;
        command.Parameters.AddWithValue("$path", Restated);
        command.Parameters.AddWithValue("$body", RestatedBody);
        command.Parameters.AddWithValue("$validFrom", T0.ToUnixTimeSeconds());
        using var reader = command.ExecuteReader();
        var rows = new List<KeyRow>();
        while (reader.Read())
        {
            rows.Add(new KeyRow(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }

        return rows;
    }

    /// <summary>Dry run first, then apply; the two must agree on Written + AlreadyPresent (F-a invariant).</summary>
    private static ReplayResult ApplyAgreeingWithDryRun(
        SqliteConnection connection, IReadOnlyList<JournalFact> facts, out IReadOnlyDictionary<long, long> idMap)
    {
        var dry = FactJournal.Replay(connection, facts, apply: false);
        var applied = FactJournal.Replay(connection, facts, apply: true, out idMap);
        Assert.Equal(applied.Written + applied.AlreadyPresent, dry.Written + dry.AlreadyPresent);
        Assert.Equal(applied.Written, dry.Written);
        return applied;
    }

    /// <summary>Row 1/3 state: one closed row carrying its retraction, one live, tokenised row with none.</summary>
    private static void AssertForgottenAndRestatedBothPresent(SqliteConnection connection)
    {
        var rows = KeyRows(connection);
        Assert.Equal(2, rows.Count);
        var closed = Assert.Single(rows, r => r.ValidTo is not null);
        var live = Assert.Single(rows, r => r.ValidTo is null);

        Assert.Equal(T0.ToUnixTimeSeconds(), closed.ValidTo);
        Assert.Null(closed.SupersededBy);
        var retraction = Assert.Single(SupersessionRowsFor(connection, closed.Id));
        Assert.Null(retraction.NewFactId);
        Assert.Equal("no longer true", retraction.Reason);
        Assert.False(HasTokenRow(connection, closed.Id));

        Assert.Null(live.SupersededBy);
        Assert.Empty(SupersessionRowsFor(connection, live.Id));
        Assert.True(HasTokenRow(connection, live.Id));
    }

    /// <summary>Row 1: the same-second forget and restatement both come back. Before adr-5 A′ matched A's freshly inserted row and was lost.</summary>
    [Fact]
    public void Replay_ASameSecondForgetAndRestatement_IntoAnEmptyStore_WritesBoth()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        var result = ApplyAgreeingWithDryRun(rebuilt, [SameSecondForget(1), SameSecondRestatement(2)], out _);

        Assert.Equal(new ReplayResult(2, 0, 0, 0), result);
        AssertForgottenAndRestatedBothPresent(rebuilt);
    }

    /// <summary>
    /// Row 2 and the fixed point: replaying the same pair into what row 1 produced writes nothing,
    /// pairs each record with its own row, and a third run counts exactly what the second did.
    /// </summary>
    [Fact]
    public void Replay_ASameSecondForgetAndRestatement_Twice_IsAFixedPointAndMapsEachToItsOwnRow()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        List<JournalFact> facts = [SameSecondForget(1), SameSecondRestatement(2)];
        FactJournal.Replay(rebuilt, facts, apply: true);
        var before = Snapshot(rebuilt);

        var second = ApplyAgreeingWithDryRun(rebuilt, facts, out var idMap);
        var third = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(new ReplayResult(0, 2, 0, 0), second);
        Assert.Equal(second, third);
        Assert.Equal(before, Snapshot(rebuilt));
        var rows = KeyRows(rebuilt);
        Assert.Equal(rows.Single(r => r.ValidTo is not null).Id, idMap[1]);
        Assert.Equal(rows.Single(r => r.ValidTo is null).Id, idMap[2]);
    }

    /// <summary>Row 3: journal order reversed (A′ before A). Before adr-5 A matched A′'s row, was lost, and counted a conflict.</summary>
    [Fact]
    public void Replay_ASameSecondRestatementBeforeItsForget_IntoAnEmptyStore_WritesBoth()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        var result = ApplyAgreeingWithDryRun(rebuilt, [SameSecondRestatement(1), SameSecondForget(2)], out _);

        Assert.Equal(new ReplayResult(2, 0, 0, 0), result);
        AssertForgottenAndRestatedBothPresent(rebuilt);
    }

    /// <summary>
    /// Row 4: an older snapshot still holding A live. Round 1 pairs A′ with the live row; A, left
    /// unpaired, is inserted closed with its retraction. The live row is not touched (D8, D68).
    /// </summary>
    [Fact]
    public void Replay_ASameSecondForgetAndRestatement_IntoAStoreStillBelievingIt_PairsTheRestatementWithTheLiveRow()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var liveId = SeedRawFact(rebuilt, Restated, "states", RestatedBody, T0);

        var result = ApplyAgreeingWithDryRun(rebuilt, [SameSecondForget(1), SameSecondRestatement(2)], out var idMap);

        Assert.Equal(new ReplayResult(1, 1, 0, 0), result);
        Assert.Equal(liveId, idMap[2]);
        Assert.NotEqual(liveId, idMap[1]);
        Assert.Null(ValidToOf(rebuilt, liveId));
        Assert.Empty(SupersessionRowsFor(rebuilt, liveId));
        AssertForgottenAndRestatedBothPresent(rebuilt);
    }

    /// <summary>
    /// Row 7: the target forgot A in the second it was stated, and A′ arrives alone (the sync split).
    /// A closed row whose valid_to equals the live record's valid_from is never its pair, so A′ is
    /// written. The neighbour that must still pair — a row forgotten a second later — is row 8,
    /// <see cref="Replay_AStaleLiveRecord_PairsWithARowForgottenLater_AndStaysForgotten"/>.
    /// </summary>
    [Fact]
    public void Replay_ARestatementAgainstARowForgottenInTheSameSecond_WritesTheRestatement()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var closedId = SeedRawFact(rebuilt, Restated, "states", RestatedBody, T0, validTo: T0);
        SeedRetractionRow(rebuilt, closedId, T0, "no longer true");

        var result = ApplyAgreeingWithDryRun(rebuilt, [SameSecondRestatement(1)], out var idMap);

        Assert.Equal(new ReplayResult(1, 0, 0, 0), result);
        Assert.NotEqual(closedId, idMap[1]);
        Assert.Equal(T0.ToUnixTimeSeconds(), ValidToOf(rebuilt, closedId));
        AssertForgottenAndRestatedBothPresent(rebuilt);
    }

    /// <summary>
    /// Row 8, the negative of row 7's exception: a row forgotten one second after the record began
    /// is the stale record's own row, so round 2 pairs it and nothing is resurrected.
    /// </summary>
    [Fact]
    public void Replay_AStaleLiveRecord_PairsWithARowForgottenLater_AndStaysForgotten()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var closedId = SeedRawFact(rebuilt, Restated, "states", RestatedBody, T0, validTo: T0.AddSeconds(1));
        var before = Snapshot(rebuilt);

        var result = ApplyAgreeingWithDryRun(rebuilt, [SameSecondRestatement(1)], out var idMap);

        Assert.Equal(new ReplayResult(0, 1, 0, 0), result);
        Assert.Equal(closedId, idMap[1]);
        Assert.Equal(before, Snapshot(rebuilt));
    }

    /// <summary>
    /// Row 11: a source that held two closed exact duplicates gets both back. Deliberate change —
    /// before adr-5 the second matched the first's freshly inserted row and counted present.
    /// </summary>
    [Fact]
    public void Replay_TwoIdenticalClosedRecords_IntoAnEmptyStore_WritesBoth()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var duplicate = Record(1, GoneBody, T0, T0.AddHours(1), supersededBy: null, reason: null);

        var result = ApplyAgreeingWithDryRun(rebuilt, [duplicate, duplicate with { Id = 2 }], out _);

        Assert.Equal(new ReplayResult(2, 0, 0, 0), result);
        Assert.Equal(2, FactRowCountFor(rebuilt, Gone, GoneBody));
    }

    /// <summary>Row 14: the same pair as edges on one object, through the edge arm of the key.</summary>
    [Fact]
    public void Replay_ASameSecondForgetAndRestatementOfAnEdge_IntoAnEmptyStore_WritesBoth()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        var result = ApplyAgreeingWithDryRun(
            rebuilt, [SameSecondForget(1, EdgeObject), SameSecondRestatement(2, EdgeObject)], out _);

        Assert.Equal(new ReplayResult(2, 0, 0, 0), result);
        AssertForgottenAndRestatedBothPresent(rebuilt);

        using var command = rebuilt.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM fact f JOIN entity o ON o.id = f.object_id
            WHERE o.path = $object AND f.body = $body;
            """;
        command.Parameters.AddWithValue("$object", EdgeObject);
        command.Parameters.AddWithValue("$body", RestatedBody);
        Assert.Equal(2L, (long)command.ExecuteScalar()!);
    }
}
