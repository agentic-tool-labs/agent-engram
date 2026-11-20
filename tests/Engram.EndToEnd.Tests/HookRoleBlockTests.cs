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
    public void SubagentStart_WithoutAnAgentType_HasNoBlock()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);
        using var home = new TestHome();
        SeedRoleFact(home.Root);

        var context = Primer(home.Root, """{"session_id":"s1","agent_id":"a1"}""", "subagent-start", "SubagentStart");

        Assert.DoesNotContain("Role memory for", context, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("""{"session_id":"s1","agent_type":"explorer","agent_id":"a1"}""")]
    [InlineData("""{"session_id":"s1"}""")]
    public void SessionStart_WithAnAgentIdOrNeitherField_IsThePlainPrimer(string payload)
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);
        using var home = new TestHome();
        SeedRoleFact(home.Root);

        var context = Primer(home.Root, payload, "session-start", "SessionStart");
        var plain = Primer(home.Root, """{"session_id":"s2"}""", "session-start", "SessionStart");

        Assert.DoesNotContain("Role memory for", context, StringComparison.Ordinal);
        Assert.Equal(plain, context);
        Assert.Equal(JsonValueKind.Null, Record(home.Root, "session-start").GetProperty("role_fact_count").ValueKind);
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
    private static void SeedRoleFact(string home)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(home, "engram.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO entity (path, kind, name, created_at) VALUES ('/agents/explorer', 'agent', 'explorer', 1);
            INSERT INTO entity (path, kind, name, created_at) VALUES ('/agents/explorer/00112233aabbccdd', 'note', '00112233aabbccdd', 1);
            INSERT INTO fact (subject_id, predicate, body, path, scope, learned_via, valid_from, created_at)
            VALUES ((SELECT id FROM entity WHERE path = '/agents/explorer/00112233aabbccdd'), 'holds',
                    'Grep the spec before the code.', '/agents/explorer/00112233aabbccdd', 'agent', 'observed', 1, 1);
            """;
        command.ExecuteNonQuery();
    }
}
