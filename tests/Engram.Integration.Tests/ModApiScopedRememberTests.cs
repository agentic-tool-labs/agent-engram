using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// D105: <c>/mod/v1/remember</c> takes a scope and routes it through the same
/// <see cref="ScopedRemember"/> that <c>engram_remember</c> uses, so every fallback says why in the
/// MCP tool's own words.
/// </summary>
public sealed class ModApiScopedRememberTests : IDisposable
{
    private const string Mod = "digest";
    private const string Session = "cc-session-1";
    private readonly List<string> scratch = [];

    public void Dispose()
    {
        foreach (var directory in scratch)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static (int Status, JsonNode Body) Remember(SandboxHome sandbox, JsonObject fields, string session = Session)
    {
        var body = new JsonObject { ["mod"] = Mod, ["session_id"] = session, ["evidence"] = "a test" };
        foreach (var (key, value) in fields)
        {
            body[key] = value?.DeepClone();
        }

        var result = ModApi.Execute(
            sandbox.Home, new LocalRuntime(sandbox.Home), "remember", Mod, Encoding.UTF8.GetBytes(body.ToJsonString()));
        return (result.Status, JsonNode.Parse(result.Json)!);
    }

    private static (string Path, string Scope) Row(SandboxHome sandbox, JsonNode body)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT e.path, f.scope FROM fact f JOIN entity e ON e.id = f.subject_id WHERE f.id = $id;";
        command.Parameters.AddWithValue("$id", (long)body["id"]!);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.GetString(1));
    }

    private static int LiveFactCount(SandboxHome sandbox)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM fact WHERE valid_to IS NULL;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    [Fact]
    public void AgentScope_WithNoAgent_WritesARoleNoteForMain()
    {
        using var sandbox = new SandboxHome();

        var (status, body) = Remember(sandbox, new JsonObject { ["statement"] = "Read the spec before the code.", ["scope"] = "agent" });

        Assert.Equal(200, status);
        var (path, scope) = Row(sandbox, body);
        Assert.StartsWith("/agents/main/", path, StringComparison.Ordinal);
        Assert.Equal(MemoryScopes.Agent, scope);
        Assert.Null(body["notice"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AgentScope_WithAnEmptyAgent_IsTheAbsentOne_AndWritesForMain(string agent)
    {
        using var sandbox = new SandboxHome();

        var (_, body) = Remember(sandbox, new JsonObject { ["statement"] = "Read the spec first.", ["scope"] = "agent", ["agent"] = agent });

        Assert.StartsWith("/agents/main/", Row(sandbox, body).Path, StringComparison.Ordinal);
        Assert.Null(body["notice"]);
    }

    [Fact]
    public void AgentScope_WithANamedAgent_WritesUnderThatRole()
    {
        using var sandbox = new SandboxHome();

        var (_, body) = Remember(sandbox, new JsonObject
        {
            ["statement"] = "Grep before reading.", ["scope"] = "agent", ["agent"] = "explorer",
        });

        Assert.StartsWith("/agents/explorer/", Row(sandbox, body).Path, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentScope_WithAnUnaddressableAgent_KeepsASessionNote_AndSaysSoInTheMcpToolsWords()
    {
        using var sandbox = new SandboxHome();

        var (status, body) = Remember(sandbox, new JsonObject
        {
            ["statement"] = "A lesson with no usable key.", ["scope"] = "agent", ["agent"] = "???",
        });
        var mcp = EngramMcpTools.Remember(
            sandbox.Home, new McpSessionId("mcp-1"), new McpHomeState(true), new LocalRuntime(sandbox.Home),
            "Another lesson with no usable key.", agent: "???", scope: "agent");

        Assert.Equal(200, status);
        Assert.Equal(MemoryScopes.Session, Row(sandbox, body).Scope);
        var notice = (string)body["notice"]!;
        Assert.Contains("no usable agent key", notice, StringComparison.Ordinal);
        Assert.EndsWith("\n" + notice, mcp, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentScope_TheFourthRoleWriteInOneSession_IsASessionNote_AndSaysSo()
    {
        using var sandbox = new SandboxHome();
        for (var i = 1; i <= RoleNotes.MaxWritesPerSession; i++)
        {
            var (_, written) = Remember(sandbox, new JsonObject { ["statement"] = $"Lesson number {i}.", ["scope"] = "agent" });
            Assert.Equal(MemoryScopes.Agent, Row(sandbox, written).Scope);
        }

        var (status, body) = Remember(sandbox, new JsonObject { ["statement"] = "Lesson number four.", ["scope"] = "agent" });

        Assert.Equal(200, status);
        Assert.Equal(MemoryScopes.Session, Row(sandbox, body).Scope);
        Assert.Equal("role memory is for lessons every future main needs; kept as a session note", (string)body["notice"]!);
    }

    // The nearest input that must not hit the cap: three role writes from another Claude Code
    // session leave this session's first write a role note.
    [Fact]
    public void AgentScope_TheCapCountsOneSessionRow_NotAnother()
    {
        using var sandbox = new SandboxHome();
        for (var i = 1; i <= RoleNotes.MaxWritesPerSession; i++)
        {
            Remember(sandbox, new JsonObject { ["statement"] = $"Lesson number {i}.", ["scope"] = "agent" }, "cc-session-other");
        }

        var (_, body) = Remember(sandbox, new JsonObject { ["statement"] = "Lesson number four.", ["scope"] = "agent" });

        Assert.Equal(MemoryScopes.Agent, Row(sandbox, body).Scope);
        Assert.Null(body["notice"]);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("Agent")]
    [InlineData("")]
    public void AnUnknownScope_Is400_AndWritesNothing(string scope)
    {
        using var sandbox = new SandboxHome();
        var before = LiveFactCount(sandbox);

        var (status, body) = Remember(sandbox, new JsonObject { ["statement"] = "Never written.", ["scope"] = scope });

        Assert.Equal(400, status);
        Assert.Contains("unknown scope", (string)body["detail"]!, StringComparison.Ordinal);
        Assert.Equal(before, LiveFactCount(sandbox));
    }

    [Fact]
    public void NoScope_WithAnAgentField_WritesTheSameSessionNoteAsWithout()
    {
        using var sandbox = new SandboxHome();

        var (_, plain) = Remember(sandbox, new JsonObject { ["statement"] = "A plain note." });
        var (_, withAgent) = Remember(sandbox, new JsonObject { ["statement"] = "A plain note, again.", ["agent"] = "explorer" }, "cc-session-2");

        Assert.Equal(MemoryScopes.Session, Row(sandbox, plain).Scope);
        Assert.Equal(MemoryScopes.Session, Row(sandbox, withAgent).Scope);
        Assert.DoesNotContain("explorer", Row(sandbox, withAgent).Path, StringComparison.Ordinal);
        Assert.Null(plain["notice"]);
        Assert.Equal(["handle", "id", "created"], plain.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void ProjectScope_WithNoProjectDir_KeepsASessionNote_AndSaysWhy()
    {
        using var sandbox = new SandboxHome();

        var (status, body) = Remember(sandbox, new JsonObject { ["statement"] = "We ship on Tuesdays.", ["scope"] = "project" });

        Assert.Equal(200, status);
        Assert.Equal(MemoryScopes.Session, Row(sandbox, body).Scope);
        Assert.StartsWith("no project_dir given", (string)body["notice"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectScope_WithAnIndexedCheckout_WritesAProjectNote()
    {
        using var sandbox = new SandboxHome();
        var repo = IndexedCheckout(sandbox);

        var (status, body) = Remember(sandbox, new JsonObject
        {
            ["statement"] = "The build retries flaky uploads three times.", ["scope"] = "project", ["project_dir"] = repo,
        });

        Assert.Equal(200, status);
        var (path, scope) = Row(sandbox, body);
        Assert.Equal(MemoryScopes.Project, scope);
        Assert.Matches("^/projects/[^/]+/notes/", path);
        Assert.Null(body["notice"]);
    }

    private string IndexedCheckout(SandboxHome sandbox)
    {
        var repo = Path.Combine(Path.GetTempPath(), "engram-d105-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        scratch.Add(repo);
        Assert.True(Git(repo, "init", "-q"));
        Assert.True(Git(repo, "remote", "add", "origin", "https://example.invalid/acme/widget-" + Path.GetFileName(repo) + ".git"));
        File.WriteAllText(Path.Combine(repo, "src", "Widget.cs"), "namespace Acme; public class Widget { }\n");

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        CodeIndexer.Index(
            connection, sandbox.Home, ConfigFile.Empty, IndexingSettings.Default,
            new IndexOptions(repo, Apply: true, Drain: false, Full: false), DateTimeOffset.UtcNow);
        return repo;
    }

    private static bool Git(string directory, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in args)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        process.WaitForExit(10_000);
        return process.HasExited && process.ExitCode == 0;
    }
}
