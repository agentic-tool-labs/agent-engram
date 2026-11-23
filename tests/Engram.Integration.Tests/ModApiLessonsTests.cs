using System.Text;
using System.Text.Json.Nodes;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>D106: the <c>lessons</c> lookup answers a role's current delivery set and writes nothing.</summary>
public class ModApiLessonsTests
{
    private const string Mod = "digest";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static (int Status, JsonNode Body) Lessons(SandboxHome sandbox, JsonObject body)
    {
        body["mod"] ??= Mod;
        var result = ModApi.Execute(
            sandbox.Home, new LocalRuntime(sandbox.Home), "lessons", Mod, Encoding.UTF8.GetBytes(body.ToJsonString()));
        return (result.Status, JsonNode.Parse(result.Json)!);
    }

    private static long Lesson(SandboxHome sandbox, string role, string statement, string session = "writer")
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var transaction = EngramDatabase.BeginWrite(connection);
        var (id, _) = RoleNotes.Append(connection, transaction, session, role, statement, null, "a test", Now);
        transaction.Commit();
        return id;
    }

    private static List<(string Id, string Text)> Current(JsonNode body) =>
        body["current"]!.AsArray().Select(l => ((string)l!["id"]!, (string)l["text"]!)).ToList();

    [Fact]
    public void WithNoAgent_AnswersForMain_InDeliveryOrder()
    {
        using var sandbox = new SandboxHome();
        var first = Lesson(sandbox, "main", "Ask before deleting.");
        var second = Lesson(sandbox, "main", "Run the targeted tests first.");
        Lesson(sandbox, "explorer", "Grep before reading.");

        var (status, body) = Lessons(sandbox, new JsonObject { ["session_id"] = "s1" });

        Assert.Equal(200, status);
        Assert.Equal(
            [(FactCatalog.HandleFor(second), "Run the targeted tests first."), (FactCatalog.HandleFor(first), "Ask before deleting.")],
            Current(body));
    }

    [Fact]
    public void WithAnAgent_AnswersForThatRoleOnly()
    {
        using var sandbox = new SandboxHome();
        Lesson(sandbox, "main", "Ask before deleting.");
        var explorer = Lesson(sandbox, "explorer", "Grep before reading.");

        var (_, body) = Lessons(sandbox, new JsonObject { ["session_id"] = "s1", ["agent"] = "explorer" });

        Assert.Equal([(FactCatalog.HandleFor(explorer), "Grep before reading.")], Current(body));
    }

    [Fact]
    public void MatchesWhatThePrimerDelivers()
    {
        using var sandbox = new SandboxHome();
        for (var i = 1; i <= 7; i++)
        {
            Lesson(sandbox, "main", $"Lesson {i}.", "writer-" + (i % 3));
        }

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var summary = PrimerSummary.Read(connection, Now, "main");
        var (_, body) = Lessons(sandbox, new JsonObject { ["session_id"] = "s1" });

        Assert.Equal(summary.RoleFacts!.Select(f => (FactCatalog.HandleFor(f.Id), f.Body)), Current(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void AnEmptyAgent_IsTheAbsentOne_AndAnswersForMain(string agent)
    {
        using var sandbox = new SandboxHome();
        var lesson = Lesson(sandbox, "main", "Ask before deleting.");

        var (_, body) = Lessons(sandbox, new JsonObject { ["session_id"] = "s1", ["agent"] = agent });

        Assert.Equal([(FactCatalog.HandleFor(lesson), "Ask before deleting.")], Current(body));
    }

    [Fact]
    public void AnUnaddressableAgent_HasNoLessons()
    {
        using var sandbox = new SandboxHome();
        Lesson(sandbox, "main", "Ask before deleting.");

        var (status, body) = Lessons(sandbox, new JsonObject { ["session_id"] = "s1", ["agent"] = "???" });

        Assert.Equal(200, status);
        Assert.Empty(Current(body));
    }

    [Fact]
    public void WithoutASessionId_Is400()
    {
        using var sandbox = new SandboxHome();

        Assert.Equal(400, Lessons(sandbox, new JsonObject()).Status);
    }

    [Fact]
    public void IsALookup()
    {
        Assert.True(ModApi.IsKnownOp("lessons"));
        Assert.True(ModApi.IsLookup("lessons"));
    }

    [Fact]
    public void LeavesEveryFileInTheHomeUnchanged_TelemetryIncluded()
    {
        using var sandbox = new SandboxHome();
        Lesson(sandbox, "main", "Ask before deleting.");
        Telemetry.Append(sandbox.Home, new TelemetryRecord(DateTime.UtcNow.ToString("o"), "s0", TelemetryEventKind.SessionStart));
        Lessons(sandbox, new JsonObject { ["session_id"] = "s1" });
        var before = Snapshot(sandbox.Home.Root);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(200, Lessons(sandbox, new JsonObject { ["session_id"] = "s1" }).Status);
        }

        Assert.Equal(before, Snapshot(sandbox.Home.Root));
    }

    private static Dictionary<string, (long Size, DateTime Modified)> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)));
}
