using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// D100 on the published binary: a role block reaches a subagent whose payload names its
/// <c>agent_type</c>, and a main session only when the payload carries <c>agent_type</c> without an
/// <c>agent_id</c>.
/// </summary>
public class HookRoleBlockTests
{
    private const string Block = "Role memory for explorer:";

    [Fact]
    public void SubagentStart_WithARoleThatHasFacts_DeliversTheBlock_AndCountsIt()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);
        using var home = new TestHome();
        SeedRoleFact(home.Root);

        var context = Primer(home.Root, """{"session_id":"s1","agent_id":"a1","agent_type":"explorer"}""", "subagent-start", "SubagentStart");

        Assert.Contains(Block + "\nYour role memory key: explorer.\n- Grep the spec before the code.", context, StringComparison.Ordinal);
        Assert.Equal(1, Record(home.Root, "subagent-start").GetProperty("role_fact_count").GetInt32());
    }

    [Fact]
    public void SubagentStart_WithARoleThatHasNoFacts_AddsOnlyTheKeyLine()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);
        using var home = new TestHome();

        var plain = Primer(home.Root, """{"session_id":"s1","agent_id":"a1"}""", "subagent-start", "SubagentStart").Split('\n');
        var withRole = Primer(home.Root, """{"session_id":"s1","agent_id":"a1","agent_type":"planner"}""", "subagent-start", "SubagentStart").Split('\n');

        Assert.Equal(plain.Length + 1, withRole.Length);
        Assert.Equal(plain, withRole.Where(line => line != "Your role memory key: planner."));
    }

    [Fact]
    public void SubagentStart_WithoutAnAgentType_HasNoBlock()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);
        using var home = new TestHome();
        SeedRoleFact(home.Root);

        var context = Primer(home.Root, """{"session_id":"s1","agent_id":"a1"}""", "subagent-start", "SubagentStart");

        Assert.DoesNotContain("Role memory for", context, StringComparison.Ordinal);
        Assert.DoesNotContain("role memory key", context, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, Record(home.Root, "subagent-start").GetProperty("role_fact_count").ValueKind);
    }

    [Fact]
    public void SessionStart_WithAnAgentTypeAndNoAgentId_DeliversTheBlock()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);
        using var home = new TestHome();
        SeedRoleFact(home.Root);

        var context = Primer(home.Root, """{"session_id":"s1","agent_type":"explorer"}""", "session-start", "SessionStart");

        Assert.Contains(Block, context, StringComparison.Ordinal);
        Assert.Equal(1, Record(home.Root, "session-start").GetProperty("role_fact_count").GetInt32());
    }

    [Fact]
    public void SessionStart_WithAnAgentId_HasNoRoleBlockAndNoKeyLine()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);
        using var home = new TestHome();
        SeedRoleFact(home.Root);

        var context = Primer(home.Root, """{"session_id":"s1","agent_type":"explorer","agent_id":"a1"}""", "session-start", "SessionStart");
        var main = Primer(home.Root, """{"session_id":"s2"}""", "session-start", "SessionStart").Split('\n');

        Assert.DoesNotContain("Role memory for", context, StringComparison.Ordinal);
        Assert.DoesNotContain("role memory key", context, StringComparison.Ordinal);
        Assert.Equal(main.Where(line => line != "Your role memory key: main."), context.Split('\n'));
        Assert.Equal(JsonValueKind.Null, Record(home.Root, "session-start").GetProperty("role_fact_count").ValueKind);
    }

    // D104: the ordinary main session names neither field and takes the role key `main`.
    [Fact]
    public void SessionStart_WithNeitherField_UsesTheMainRole()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);
        using var home = new TestHome();
        SeedRoleFact(home.Root, "main");

        var context = Primer(home.Root, """{"session_id":"s1"}""", "session-start", "SessionStart");

        Assert.Contains("Role memory for main:\nYour role memory key: main.\n- Grep the spec before the code.", context, StringComparison.Ordinal);
        Assert.DoesNotContain("Role memory for explorer", context, StringComparison.Ordinal);
        Assert.Equal(1, Record(home.Root, "session-start").GetProperty("role_fact_count").GetInt32());
    }

    [Fact]
    public void SessionStart_WithNeitherField_AndNoMainLessons_AddsOnlyTheMainKeyLine()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);
        using var home = new TestHome();
        SeedRoleFact(home.Root);

        var context = Primer(home.Root, """{"session_id":"s1"}""", "session-start", "SessionStart");

        Assert.Contains("Your role memory key: main.", context, StringComparison.Ordinal);
        Assert.DoesNotContain("Role memory for", context, StringComparison.Ordinal);
        Assert.Equal(0, Record(home.Root, "session-start").GetProperty("role_fact_count").GetInt32());
    }

    private static string Primer(string home, string payload, string hook, string eventName)
    {
        var (exitCode, stdout, stderr) = EngramProcess.RunWithStdin(home, payload, "hook", hook);
        Assert.True(exitCode == 0, $"hook {hook} exited {exitCode}: {stderr}");

        var envelope = JsonDocument.Parse(stdout).RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal(eventName, envelope.GetProperty("hookEventName").GetString());
        return envelope.GetProperty("additionalContext").GetString()!;
    }

    private static JsonElement Record(string home, string kind) =>
        File.ReadAllLines(Path.Combine(home, "telemetry.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement)
            .First(record => record.GetProperty("kind").GetString() == kind);

    // Engram.EndToEnd.Tests has no reference to Engram.Core, so the role fact is written with raw
    // SQL in the shape RoleNotes.Append leaves: a role entity of kind agent, a note entity beneath
    // it, and a live agent-scope fact.
    private static void SeedRoleFact(string home, string role = "explorer")
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(home, "engram.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO entity (path, kind, name, created_at) VALUES ('/agents/' || $role, 'agent', $role, 1);
            INSERT INTO entity (path, kind, name, created_at) VALUES ('/agents/' || $role || '/00112233aabbccdd', 'note', '00112233aabbccdd', 1);
            INSERT INTO fact (subject_id, predicate, body, path, scope, learned_via, valid_from, created_at)
            VALUES ((SELECT id FROM entity WHERE path = '/agents/' || $role || '/00112233aabbccdd'), 'holds',
                    'Grep the spec before the code.', '/agents/' || $role || '/00112233aabbccdd', 'agent', 'observed', 1, 1);
            """;
        command.Parameters.AddWithValue("$role", role);
        command.ExecuteNonQuery();
    }
}
