using System.Text.Json;
using System.Text.RegularExpressions;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// Two checks on the plugin's source that <c>claude plugin test</c> cannot host and
/// <c>claude plugin validate</c> does not make. They read files and drive no binary, so
/// unlike the rest of this project they do not skip without one.
/// </summary>
public class PluginSourceTests
{
    // validate accepts a number field whose default is the string "400" and the plugin then
    // receives a string where it asked for a number; nothing downstream notices.
    [Fact]
    public void ShippedManifest_NumberUserConfigDefaultsAreJsonNumbers()
    {
        var manifest = File.ReadAllText(Path.Combine(PluginSandbox.PluginDirectory, ".claude-plugin", "plugin.json"));

        Assert.Empty(NumberDefaultsThatAreNotNumbers(manifest));
        Assert.True(
            NumberFieldCount(manifest) > 0,
            "The manifest declares no number userConfig field, so the check above proved nothing.");
    }

    [Fact]
    public void NumberDefaultWrittenAsAString_IsReported()
    {
        const string manifest = """
            { "userConfig": { "budget": { "type": "number", "default": "400" } } }
            """;

        Assert.Equal(["budget"], NumberDefaultsThatAreNotNumbers(manifest));
    }

    // The nearest inputs that must not be reported: the same value as a real number, and a
    // string field whose default is a string.
    [Fact]
    public void NumberDefaultWrittenAsANumber_AndStringDefaultOnAStringField_AreNotReported()
    {
        const string manifest = """
            { "userConfig": {
                "budget": { "type": "number", "default": 400 },
                "sound":  { "type": "string", "default": "400" } } }
            """;

        Assert.Empty(NumberDefaultsThatAreNotNumbers(manifest));
    }

    // Mods reach Engram through the server's mod API, never over MCP: an MCP call is recorded as
    // the model's own use of memory and inflates the numbers the adoption gates read. The hooks
    // scanner makes every engine call spell out its noun and event in source, so a text match
    // on the receiver-agnostic ".mcp." is complete.
    [Fact]
    public void ShippedModSources_CallNothingOnMcp()
    {
        var sources = ModSourceFiles(PluginSandbox.PluginDirectory).ToList();

        Assert.NotEmpty(sources);
        Assert.Empty(McpCalls(sources));
    }

    [Theory]
    [InlineData("const r = await $.mcp.call('s', 't', {})")]
    [InlineData("const r = await io.mcp.call('s', 't', {})")]
    [InlineData("return engine.mcp.connect('s')")]
    [InlineData("const r = await $?.mcp?.call('s', 't')")]
    public void McpCall_OnAnyReceiver_IsReportedWithFileAndLine(string line)
    {
        using var tree = new TempTree();
        tree.Write("mods/lens/index.ts", "export const a = 1\n" + line + "\n");

        var found = McpCalls(ModSourceFiles(tree.Root));

        var only = Assert.Single(found);
        Assert.Equal(Path.Combine(tree.Root, "mods", "lens", "index.ts") + ":2", only);
    }

    // Tool names carry "mcp__" and the server's own name carries no dot before "mcp": neither is a call.
    [Theory]
    [InlineData("export const recall = 'mcp__plugin_engram_engram__engram_recall'")]
    [InlineData("const x = settings.mcpServers")]
    [InlineData("const x = $.mcpish.call()")]
    public void McpTextThatIsNotACall_IsNotReported(string line)
    {
        using var tree = new TempTree();
        tree.Write("mods/lens/index.ts", line + "\n");

        Assert.Empty(McpCalls(ModSourceFiles(tree.Root)));
    }

    // A receiver split across lines is still a call; the reported line is where ".mcp" begins.
    [Fact]
    public void McpCall_SplitAcrossLines_IsReportedAtTheLineOfMcp()
    {
        using var tree = new TempTree();
        tree.Write("mods/lens/index.ts", "const r = await $\n  .mcp\n  .call('s', 't')\n");

        var only = Assert.Single(McpCalls(ModSourceFiles(tree.Root)));

        Assert.Equal(Path.Combine(tree.Root, "mods", "lens", "index.ts") + ":2", only);
    }

    [Fact]
    public void McpCall_InAJavaScriptModule_IsReported()
    {
        using var tree = new TempTree();
        tree.Write("mods/lens/index.mjs", "x.mcp.call()\n");

        Assert.Single(McpCalls(ModSourceFiles(tree.Root)));
    }

    [Fact]
    public void McpCall_InAHooksFileOrATestFile_IsReported_ButNotOutsideHooksAndMods()
    {
        using var tree = new TempTree();
        tree.Write("hooks/register.tsx", "x.mcp.call()\n");
        tree.Write("mods/band/band.test.ts", "x.mcp.call()\n");
        tree.Write(".claude-plugin/types/claude-code/index.d.ts", "x.mcp.call()\n");
        tree.Write("commands/notes.ts", "x.mcp.call()\n");

        var found = McpCalls(ModSourceFiles(tree.Root)).Select(f => Path.GetFileName(f.Split(':')[0])).Order().ToList();

        Assert.Equal(["band.test.ts", "register.tsx"], found);
    }

    // The engine loads every mod through one hooks module and refuses an event registered twice
    // there unless each registration has a matcher, so a second mod taking an event bare took the
    // whole plugin down: validate failed and every plugin test with it. A registration is bare when
    // the hook follows the event name directly.
    [Fact]
    public void ShippedModSources_RegisterNoEventBareMoreThanOnce()
    {
        var sources = ModSourceFiles(PluginSandbox.PluginDirectory).ToList();

        Assert.NotEmpty(sources);
        Assert.Empty(EventsRegisteredBareMoreThanOnce(sources));
    }

    [Fact]
    public void TwoBareRegistrationsOfOneEvent_AreReportedWithBothLocations()
    {
        using var tree = new TempTree();
        tree.Write("mods/band/index.tsx", "on('session.start', async ($, e, next) => next(e))\n");
        tree.Write("mods/digest/index.tsx", "x\non(\"session.start\", ($, e, next) => next(e))\n");

        var only = Assert.Single(EventsRegisteredBareMoreThanOnce(ModSourceFiles(tree.Root)));

        Assert.StartsWith("session.start:", only);
        Assert.Contains("band", only);
        Assert.Contains("digest", only);
    }

    // The nearest inputs that must not be reported: one bare and one with a matcher, two with
    // matchers, two different events, and a test file registering on the test's own hooks.
    [Fact]
    public void BareRegistrationsThatTheEngineAccepts_AreNotReported()
    {
        using var tree = new TempTree();
        tree.Write("mods/a/index.ts", "on('session.start', async ($, e, next) => next(e))\non('tool.call', { tool: ['Edit'] }, ($, e, next) => next(e))\n");
        tree.Write("mods/b/index.ts", "on('session.start', ANY_SESSION_START, async ($, e, next) => next(e))\non('tool.call', { tool: ['Edit'] }, ($, e, next) => next(e))\n");
        tree.Write("mods/c/index.ts", "on('turn.complete', ($, e, next) => next(e))\n");
        tree.Write("mods/a/a.test.ts", "on('session.start', (_$, e) => ({ cwd: e.cwd }))\n");

        Assert.Empty(EventsRegisteredBareMoreThanOnce(ModSourceFiles(tree.Root)));
    }

    // A handler passed by name is as bare as an inline one; a name followed by a comma is a matcher.
    [Fact]
    public void BareRegistrationsOfAHandlerPassedByName_AreReported()
    {
        using var tree = new TempTree();
        tree.Write("mods/a/index.ts", "on('turn.complete', onTurn)\n");
        tree.Write("mods/b/index.ts", "on('turn.complete', turns.handler).catch(fallback)\non('prompt.submit', function (_$, e, next) { return next(e) })\n");

        var only = Assert.Single(EventsRegisteredBareMoreThanOnce(ModSourceFiles(tree.Root)));

        Assert.StartsWith("turn.complete:", only);
    }

    [Fact]
    public void ANamedMatcherBeforeAHandler_IsNotABareRegistration()
    {
        using var tree = new TempTree();
        tree.Write("mods/a/index.ts", "on('turn.complete', onTurn)\n");
        tree.Write("mods/b/index.ts", "on('turn.complete', ANY_TURN_COMPLETE, onTurn)\non('turn.complete', ANY_TURN_COMPLETE, async ($, e, next) => next(e))\n");

        Assert.Empty(EventsRegisteredBareMoreThanOnce(ModSourceFiles(tree.Root)));
    }

    [Fact]
    public void TheSameEventRegisteredBareTwiceInOneFile_IsReported()
    {
        using var tree = new TempTree();
        tree.Write("mods/a/index.ts", "on('session.start', ($, e, next) => next(e))\non('session.start', async ($, e, next) => next(e))\n");

        Assert.Single(EventsRegisteredBareMoreThanOnce(ModSourceFiles(tree.Root)));
    }

    private static IEnumerable<string> ModSourceFiles(string pluginDirectory) =>
        new[] { "hooks", "mods" }
            .Select(d => Path.Combine(pluginDirectory, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
            .Where(f => SourceExtensions.Contains(Path.GetExtension(f)))
            .Order(StringComparer.Ordinal);

    // The engine loads every one of these suffixes as a hooks module. The pattern tolerates
    // optional chaining and a receiver split across lines, which a per-line match would miss.
    private static readonly string[] SourceExtensions = [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".mts", ".cts"];

    private static readonly Regex McpCallPattern = new(@"\??\.\s*mcp\s*\??\.", RegexOptions.Compiled);

    private static List<string> McpCalls(IEnumerable<string> files)
    {
        var found = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match match in McpCallPattern.Matches(text))
            {
                var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                found.Add($"{file}:{line}");
            }
        }

        return found;
    }

    private static readonly Regex BareRegistrationPattern = new(
        @"\bon\(\s*['""]([a-z][a-z0-9.]*)['""]\s*,\s*(?:(?:async\s+)?function\b|(?:async\s*)?(?:\(|[A-Za-z_$][\w$]*\s*=>)|[A-Za-z_$][\w$.]*\s*\))",
        RegexOptions.Compiled);

    private static List<string> EventsRegisteredBareMoreThanOnce(IEnumerable<string> files)
    {
        var byEvent = new Dictionary<string, List<string>>();
        foreach (var file in files.Where(f => !Path.GetFileName(f).Contains(".test.", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in BareRegistrationPattern.Matches(text))
            {
                var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                var name = match.Groups[1].Value;
                if (!byEvent.TryGetValue(name, out var places))
                {
                    byEvent[name] = places = [];
                }

                places.Add($"{file}:{line}");
            }
        }

        return byEvent.Where(e => e.Value.Count > 1).Select(e => $"{e.Key}: {string.Join(", ", e.Value)}").Order().ToList();
    }

    private static List<string> NumberDefaultsThatAreNotNumbers(string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
        var bad = new List<string>();
        if (!document.RootElement.TryGetProperty("userConfig", out var config))
        {
            return bad;
        }

        foreach (var field in config.EnumerateObject())
        {
            var isNumber = field.Value.TryGetProperty("type", out var type) && type.GetString() == "number";
            if (isNumber && (!field.Value.TryGetProperty("default", out var def) || def.ValueKind != JsonValueKind.Number))
            {
                bad.Add(field.Name);
            }
        }

        return bad;
    }

    private static int NumberFieldCount(string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
        return document.RootElement.GetProperty("userConfig").EnumerateObject()
            .Count(f => f.Value.TryGetProperty("type", out var t) && t.GetString() == "number");
    }

    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "engram-plugin-source-" + Guid.NewGuid().ToString("N"));

        public TempTree() => Directory.CreateDirectory(Root);

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
