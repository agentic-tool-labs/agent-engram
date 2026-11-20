using System.Runtime.CompilerServices;
using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// P5 (D100): <c>engram_remember scope:"agent"</c> files a lesson under the caller's role, capped at
/// three per session per role, and the role's primer delivers the newest five.
/// </summary>
public sealed class RoleNotesTests
{
    private static readonly McpHomeState Initialized = new(true);
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private const string Role = "wrangl:architect";

    [Fact]
    public void AgentScope_WithARole_FilesUnderTheRole()
    {
        using var sandbox = new SandboxHome();

        var response = Remember(sandbox, "s1", "Never design in the implementor.", "agent", Role);

        var fact = Assert.Single(Notes(sandbox));
        Assert.Matches("^/agents/wrangl-architect/[0-9a-f]{16}$", fact.Path);
        Assert.Equal(RoleNotes.PathFor(Role, "Never design in the implementor."), fact.Path);
        Assert.Equal("agent", fact.Scope);
        Assert.Equal("note", fact.Kind);
        Assert.Equal("warns", fact.Predicate);
        Assert.Equal("observed", fact.LearnedVia);
        Assert.Equal((Role, "agent"), EntityNameAndKind(sandbox, "/agents/wrangl-architect"));
        Assert.DoesNotContain('\n', response);
        Assert.Equal("agent", LastRemember(sandbox).Scope);
    }

    [Fact]
    public void AgentScope_WithoutARole_KeepsASessionNote()
    {
        using var sandbox = new SandboxHome();

        var response = Remember(sandbox, "s1", "A lesson.", "agent", agent: null);

        Assert.StartsWith("/sessions/", Assert.Single(Notes(sandbox)).Path, StringComparison.Ordinal);
        Assert.EndsWith("\nno usable agent key — kept as a session note; pass your role key from the primer as agent", response, StringComparison.Ordinal);
        Assert.Null(LastRemember(sandbox).Scope);
    }

    [Fact]
    public void AgentScope_WithAKeyThatSlugsToNothing_KeepsASessionNote()
    {
        using var sandbox = new SandboxHome();

        var response = Remember(sandbox, "s1", "A lesson.", "agent", "???");

        Assert.StartsWith("/sessions/", Assert.Single(Notes(sandbox)).Path, StringComparison.Ordinal);
        Assert.EndsWith("\nno usable agent key — kept as a session note; pass your role key from the primer as agent", response, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFourthAgentWriteInOneSession_IsASessionNote()
    {
        using var sandbox = new SandboxHome();
        for (var i = 1; i <= 3; i++)
        {
            Assert.DoesNotContain('\n', Remember(sandbox, "s1", $"Lesson number {i}.", "agent", Role));
        }

        var fourth = Remember(sandbox, "s1", "Lesson number 4.", "agent", Role);

        var notes = Notes(sandbox);
        Assert.Equal(3, notes.Count(note => note.Scope == "agent"));
        var kept = Assert.Single(notes, note => note.Scope == "session");
        Assert.Matches("^/sessions/[0-9]+/wrangl-architect/[0-9a-f]{8}$", kept.Path);
        Assert.EndsWith($"\nrole memory is for lessons every future {Role} needs; kept as a session note", fourth, StringComparison.Ordinal);
        Assert.Null(LastRemember(sandbox).Scope);
    }

    [Fact]
    public void TheCap_IsPerSession_SoAnotherSessionStillWrites()
    {
        using var sandbox = new SandboxHome();
        for (var i = 1; i <= 3; i++)
        {
            Remember(sandbox, "s1", $"Lesson number {i}.", "agent", Role);
        }

        var other = Remember(sandbox, "s2", "Lesson from another session.", "agent", Role);

        Assert.DoesNotContain('\n', other);
        Assert.Equal(4, Notes(sandbox).Count(note => note.Scope == "agent"));
    }

    [Fact]
    public void TheCap_IsPerRole_SoAnotherRoleStillWrites()
    {
        using var sandbox = new SandboxHome();
        for (var i = 1; i <= 3; i++)
        {
            Remember(sandbox, "s1", $"Lesson number {i}.", "agent", Role);
        }

        var other = Remember(sandbox, "s1", "A reviewer lesson.", "agent", "wrangl:reviewer");

        Assert.DoesNotContain('\n', other);
        Assert.Single(Notes(sandbox), note => note.Path.StartsWith("/agents/wrangl-reviewer/", StringComparison.Ordinal));
    }

    [Fact]
    public void ARepeat_ReturnsTheExistingHandle_EvenPastTheCap()
    {
        using var sandbox = new SandboxHome();
        var first = HandleOf(Remember(sandbox, "s1", "Lesson number 1.", "agent", Role));
        Remember(sandbox, "s1", "Lesson number 2.", "agent", Role);
        Remember(sandbox, "s1", "Lesson number 3.", "agent", Role);

        var again = Remember(sandbox, "s1", "lesson number 1", "agent", Role);

        Assert.Equal(first, HandleOf(again));
        Assert.DoesNotContain('\n', again);
        Assert.Equal(3, Notes(sandbox).Count);
    }

    [Fact]
    public void TwoRolesThatSlugAlike_ShareAPath_AndKeepTheFirstWritersName()
    {
        using var sandbox = new SandboxHome();

        Remember(sandbox, "s1", "First lesson.", "agent", "wrangl:architect");
        Remember(sandbox, "s1", "Second lesson.", "agent", "wrangl architect");

        Assert.Equal(2, Notes(sandbox).Count(note => note.Path.StartsWith("/agents/wrangl-architect/", StringComparison.Ordinal)));
        Assert.Equal(("wrangl:architect", "agent"), EntityNameAndKind(sandbox, "/agents/wrangl-architect"));
    }

    [Fact]
    public void SessionNotesUnderAnAgentSegment_AreNeverRoleMemory()
    {
        using var sandbox = new SandboxHome();
        Remember(sandbox, "s1", "A subagent's working note.", scope: null, agent: Role);

        using var connection = Open(sandbox);
        var summary = PrimerSummary.Read(connection, Now, Role);

        Assert.Empty(summary.RoleFacts!);
        AssertPlusKeyLineOnly(
            PrimerBuilder.BuildForSubagent(PrimerSummary.Read(connection, Now), MemoryPrecedence.EngramFirst),
            PrimerBuilder.BuildForSubagent(summary, MemoryPrecedence.EngramFirst));
    }

    [Fact]
    public void SixRoleFacts_DeliverTheFiveNewest()
    {
        using var sandbox = new SandboxHome();
        for (var i = 1; i <= 6; i++)
        {
            Remember(sandbox, "s" + i, $"Role lesson {i}.", "agent", Role);
        }

        using var connection = Open(sandbox);
        var primer = PrimerBuilder.BuildForSubagent(PrimerSummary.Read(connection, Now, Role), MemoryPrecedence.EngramFirst, out var delivered);

        Assert.Equal(5, delivered);
        var block = primer[primer.IndexOf($"Role memory for {Role}:", StringComparison.Ordinal)..].Split('\n');
        Assert.Equal($"Your role memory key: {Role}.", block[1]);
        Assert.Equal(["- Role lesson 6.", "- Role lesson 5.", "- Role lesson 4.", "- Role lesson 3.", "- Role lesson 2."], block[2..7]);
        Assert.DoesNotContain("Role lesson 1.", primer, StringComparison.Ordinal);
    }

    [Fact]
    public void ARoleWithNoFacts_AddsExactlyTheKeyLine()
    {
        using var sandbox = new SandboxHome();
        Remember(sandbox, "s1", "A reviewer lesson.", "agent", "wrangl:reviewer");

        using var connection = Open(sandbox);

        AssertPlusKeyLineOnly(
            PrimerBuilder.BuildForSubagent(PrimerSummary.Read(connection, Now), MemoryPrecedence.EngramFirst),
            PrimerBuilder.BuildForSubagent(PrimerSummary.Read(connection, Now, Role), MemoryPrecedence.EngramFirst));
        AssertPlusKeyLineOnly(
            PrimerBuilder.Build(PrimerSummary.Read(connection, Now), MemoryPrecedence.EngramFirst),
            PrimerBuilder.Build(PrimerSummary.Read(connection, Now, Role), MemoryPrecedence.EngramFirst));
    }

    [Fact]
    public void WithoutARole_ThePrimerHasNoKeyLine()
    {
        using var sandbox = new SandboxHome();
        Remember(sandbox, "s1", "A role lesson.", "agent", Role);

        using var connection = Open(sandbox);

        Assert.DoesNotContain("role memory key", PrimerBuilder.BuildForSubagent(PrimerSummary.Read(connection, Now), MemoryPrecedence.EngramFirst), StringComparison.Ordinal);
        Assert.DoesNotContain("role memory key", PrimerBuilder.Build(PrimerSummary.Read(connection, Now, role: ""), MemoryPrecedence.EngramFirst), StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstanceStyleKey_StillLands()
    {
        using var sandbox = new SandboxHome();

        var response = Remember(sandbox, "s1", "A lesson under an instance name.", "agent", "engram-architect-2");

        Assert.DoesNotContain('\n', response);
        Assert.StartsWith("/agents/engram-architect-2/", Assert.Single(Notes(sandbox)).Path, StringComparison.Ordinal);
    }

    private static void AssertPlusKeyLineOnly(string plain, string withRole)
    {
        var plainLines = plain.Split('\n');
        var roleLines = withRole.Split('\n');
        var keyLine = $"Your role memory key: {Role}.";

        Assert.Equal(plainLines.Length + 1, roleLines.Length);
        Assert.Single(roleLines, line => line == keyLine);
        Assert.Equal(plainLines, roleLines.Where(line => line != keyLine));
    }

    [Fact]
    public void ARoleFact_MovesNoPrimerCountOrExample_OnEitherSummaryPath()
    {
        using var sandbox = new SandboxHome();
        Remember(sandbox, "s1", "Seed so the store is not empty.", scope: null, agent: null);
        string Primers()
        {
            using var connection = Open(sandbox);
            return PrimerBuilder.Build(PrimerSummary.Read(connection, Now), MemoryPrecedence.EngramFirst)
                + "\n--\n" + PrimerBuilder.BuildForSubagent(PrimerSummary.Read(connection, Now), MemoryPrecedence.EngramFirst);
        }

        var before = Primers();
        Remember(sandbox, "s1", "Quokka lesson for one role only.", "agent", Role);

        Assert.Equal(before, Primers());
        using var connection = Open(sandbox);
        foreach (var summary in new[] { PrimerSummary.Read(connection, Now), PrimerSummary.From(FactCatalog.ReadLongTerm(connection, Now)) })
        {
            Assert.DoesNotContain(summary.ExampleCandidates, fact => fact.SubjectPath?.StartsWith("/agents/", StringComparison.Ordinal) == true);
            Assert.DoesNotContain("agents", summary.TopicCounts.Keys);
        }

        Assert.Equal(
            PrimerSummary.Read(connection, Now).FactCount,
            PrimerSummary.From(FactCatalog.ReadLongTerm(connection, Now)).FactCount);
    }

    [Fact]
    public void WithoutARole_ThePrimerMakesNoRoleRead()
    {
        using var sandbox = new SandboxHome();
        using var connection = Open(sandbox);

        Assert.Equal(0, CountRoleReads(() => PrimerSummary.Read(connection, Now, role: null)));
        Assert.Equal(0, CountRoleReads(() => PrimerSummary.Read(connection, Now, role: "")));
        Assert.Equal(1, CountRoleReads(() => PrimerSummary.Read(connection, Now, Role)));
    }

    [Fact]
    public void TheRoleBlock_RendersTheSameFromBothSummaryPaths()
    {
        using var sandbox = new SandboxHome();
        for (var i = 1; i <= 6; i++)
        {
            Remember(sandbox, "s" + i, $"Role lesson {i}.", "agent", Role);
        }

        Remember(sandbox, "s1", "A reviewer lesson.", "agent", "wrangl:reviewer");

        using var connection = Open(sandbox);
        var reference = PrimerSummary.From(FactCatalog.ReadLongTerm(connection, Now), Role);
        var summary = PrimerSummary.Read(connection, Now, Role);

        var subagent = PrimerBuilder.BuildForSubagent(summary, MemoryPrecedence.EngramFirst);
        Assert.Contains("Role memory for", subagent, StringComparison.Ordinal);
        Assert.Equal(PrimerBuilder.BuildForSubagent(reference, MemoryPrecedence.EngramFirst), subagent);
        Assert.Equal(PrimerBuilder.Build(reference, MemoryPrecedence.EngramFirst), PrimerBuilder.Build(summary, MemoryPrecedence.EngramFirst));
    }

    [Fact]
    public void AgentFacts_AreInDefaultRecall_AndReachedByTheAgentType()
    {
        using var sandbox = new SandboxHome();
        var handle = HandleOf(Remember(sandbox, "s1", "Quokka reviews always run the touched classes.", "agent", Role));

        var typed = EngramMcpTools.Recall(
            sandbox.Home, new McpSessionId("s9"), Initialized, new LocalRuntime(sandbox.Home), new SessionPinStore(),
            "quokka reviews touched classes", types: ["agent"]);
        var plain = EngramMcpTools.Recall(
            sandbox.Home, new McpSessionId("s9"), Initialized, new LocalRuntime(sandbox.Home), new SessionPinStore(),
            "quokka reviews touched classes");

        Assert.Contains($"[{handle}]", typed, StringComparison.Ordinal);
        Assert.Contains($"[{handle}]", plain, StringComparison.Ordinal);
    }

    private static string Remember(SandboxHome sandbox, string session, string statement, string? scope, string? agent) =>
        EngramMcpTools.Remember(
            sandbox.Home, new McpSessionId(session), Initialized, new LocalRuntime(sandbox.Home), statement,
            agent: agent, scope: scope);

    private static int CountRoleReads(Action action)
    {
        var counter = new StrongBox<int>();
        RoleNotes.ReadCounter.Value = counter;
        try
        {
            action();
        }
        finally
        {
            RoleNotes.ReadCounter.Value = null;
        }

        return counter.Value;
    }

    private sealed record NoteRow(string Path, string Kind, string Predicate, string Scope, string LearnedVia);

    private static List<NoteRow> Notes(SandboxHome sandbox)
    {
        using var connection = Open(sandbox);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT e.path, e.kind, f.predicate, f.scope, f.learned_via
            FROM fact f JOIN entity e ON e.id = f.subject_id
            WHERE f.valid_to IS NULL AND (e.path LIKE '/sessions/%' OR e.path LIKE '/agents/%')
            ORDER BY f.id;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<NoteRow>();
        while (reader.Read())
        {
            rows.Add(new NoteRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        return rows;
    }

    private static (string Name, string Kind) EntityNameAndKind(SandboxHome sandbox, string path)
    {
        using var connection = Open(sandbox);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, kind FROM entity WHERE path = $path;";
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), $"no entity at {path}");
        return (reader.GetString(0), reader.GetString(1));
    }

    private static TelemetryRecord LastRemember(SandboxHome sandbox) =>
        File.ReadAllLines(Telemetry.ResolvePath(sandbox.Home))
            .Select(Telemetry.TryParse)
            .Last(record => record?.Kind == TelemetryEventKind.Remember)!;

    private static SqliteConnection Open(SandboxHome sandbox) => EngramDatabase.Open(sandbox.Home);

    private static string HandleOf(string response) => response[1..response.IndexOf(']', StringComparison.Ordinal)];
}
