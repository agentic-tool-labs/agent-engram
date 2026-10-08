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

    // Two mods that each take a shared event bare make the engine refuse the plugin, and one mod
    // taking it bare is invisible to every gate until a second arrives. So any mod registering one
    // of these events passes its shared constant, whether or not another mod does yet.
    [Fact]
    public void ShippedModSources_PassTheSharedConstantForEverySharedEvent()
    {
        var sources = ModSourceFiles(PluginSandbox.PluginDirectory).ToList();

        Assert.NotEmpty(sources);
        Assert.Empty(RegistrationsWithoutTheSharedConstant(PluginSandbox.PluginDirectory, sources, SharedConstantAllowList));
    }

    [Fact]
    public void TheAllowList_ExcusesOnlyRegistrationsThatAreStillBare()
    {
        var sources = ModSourceFiles(PluginSandbox.PluginDirectory).ToList();

        Assert.Empty(StaleAllowListEntries(PluginSandbox.PluginDirectory, sources, SharedConstantAllowList));
    }

    [Fact]
    public void AnAllowListEntryWhoseRegistrationNowPassesItsConstant_IsStale()
    {
        using var tree = new TempTree();
        tree.Write("mods/a/index.ts", "on('turn.complete', ANY_TURN_COMPLETE, ($, e, next) => next(e))\n");

        var stale = Assert.Single(StaleAllowListEntries(tree.Root, ModSourceFiles(tree.Root), [("mods/a/index.ts", "turn.complete")]));

        Assert.Contains("mods/a/index.ts", stale);
        Assert.Contains("'turn.complete'", stale);
    }

    [Fact]
    public void AnAllowListEntryForAMissingFileOrAnUnregisteredEvent_IsStale()
    {
        using var tree = new TempTree();
        tree.Write("mods/a/index.ts", "on('turn.complete', ($, e, next) => next(e))\n");

        var stale = StaleAllowListEntries(
            tree.Root,
            ModSourceFiles(tree.Root),
            [("mods/gone/index.ts", "turn.complete"), ("mods/a/index.ts", "prompt.submit")]);

        Assert.Equal(2, stale.Count);
    }

    // The nearest input that is not stale: the entry still excuses a bare registration.
    [Fact]
    public void AnAllowListEntryThatStillExcusesABareRegistration_IsNotStale()
    {
        using var tree = new TempTree();
        tree.Write("mods/a/index.ts", "on('turn.complete', ($, e, next) => next(e))\n");

        Assert.Empty(StaleAllowListEntries(tree.Root, ModSourceFiles(tree.Root), [("mods/a/index.ts", "turn.complete")]));
    }

    [Theory]
    [InlineData("session.start", "ANY_SESSION_START")]
    [InlineData("turn.start", "ANY_TURN_START")]
    [InlineData("turn.complete", "ANY_TURN_COMPLETE")]
    [InlineData("prompt.submit", "ANY_PROMPT_SUBMIT")]
    public void ALoneBareRegistrationOfASharedEvent_IsReportedNamingTheFileAndTheConstant(string eventName, string constant)
    {
        using var tree = new TempTree();
        tree.Write("mods/lens/index.ts", $"on('{eventName}', async ($, e, next) => next(e))\n");

        var only = Assert.Single(RegistrationsWithoutTheSharedConstant(tree.Root, ModSourceFiles(tree.Root), []));

        Assert.Contains("mods/lens/index.ts:1", only);
        Assert.Contains(constant, only);
        Assert.Contains($"'{eventName}'", only);
    }

    // The nearest inputs that must not be reported: each event with its own constant, an event
    // that is not shared, shared/ itself, and test files.
    [Fact]
    public void RegistrationsThatPassTheirConstant_OrAreOutOfScope_AreNotReported()
    {
        using var tree = new TempTree();
        tree.Write("mods/a/index.ts", "on('session.start', ANY_SESSION_START, async ($, e, next) => next(e))\non('turn.complete', ANY_TURN_COMPLETE, handler)\non('ui.render', { component: 'Pane' }, ($, e) => e)\non('tool.call', ($, e, next) => next(e))\n");
        tree.Write("mods/shared/events.ts", "on('session.start', ($, e, next) => next(e))\n");
        tree.Write("mods/a/a.test.ts", "on('prompt.submit', (_$, e) => ({ text: e.text }))\n");

        Assert.Empty(RegistrationsWithoutTheSharedConstant(tree.Root, ModSourceFiles(tree.Root), []));
    }

    // Another event's constant, a hand-written matcher, a handler where the matcher goes, and a
    // longer name that merely starts with the right one are each not the shared constant.
    [Theory]
    [InlineData("on('session.start', ANY_TURN_START, async ($, e, next) => next(e))")]
    [InlineData("on('turn.start', { turnId: /.*/ }, async ($, e, next) => next(e))")]
    [InlineData("on('prompt.submit', handler)")]
    [InlineData("on('turn.complete', ANY_TURN_COMPLETE_EXTRA, handler)")]
    [InlineData("on('turn.complete', ANY_TURN_COMPLETE)")]
    public void ARegistrationWithoutExactlyItsOwnConstant_IsReported(string line)
    {
        using var tree = new TempTree();
        tree.Write("mods/a/index.ts", line + "\n");

        Assert.Single(RegistrationsWithoutTheSharedConstant(tree.Root, ModSourceFiles(tree.Root), []));
    }

    [Fact]
    public void AnAllowListEntry_ExcusesThatFileAndEventOnly()
    {
        using var tree = new TempTree();
        tree.Write("mods/a/index.ts", "on('turn.complete', ($, e, next) => next(e))\non('prompt.submit', ($, e, next) => next(e))\n");
        tree.Write("mods/b/index.ts", "on('turn.complete', ($, e, next) => next(e))\n");

        var found = RegistrationsWithoutTheSharedConstant(tree.Root, ModSourceFiles(tree.Root), [("mods/a/index.ts", "turn.complete")]);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, f => f.Contains("mods/a/index.ts:2"));
        Assert.Contains(found, f => f.Contains("mods/b/index.ts:1"));
    }

    // A hook that throws before `next` stops the hooks beneath it, and one that calls `next` twice
    // re-runs the core, so every handler of every mod wraps `next` once and catches to the fallback.
    [Fact]
    public void ShippedModSources_WrapEveryHandlerInTheNoThrowGuard()
    {
        var sources = ModSourceFiles(PluginSandbox.PluginDirectory).ToList();

        Assert.NotEmpty(sources);
        Assert.Empty(HandlersOutsideTheNoThrowGuard(PluginSandbox.PluginDirectory, sources));
    }

    private const string CompliantHandler = "on('turn.start', ANY_TURN_START, async ($, e, next) => {\n  const go = once(next)\n  try {\n    return await go(e)\n  } catch {\n    return go.fallback(e)\n  }\n})\n";

    [Fact]
    public void AHandlerThatCallsOnceAndFallsBack_IsNotReported()
    {
        using var tree = new TempTree();
        tree.Write("mods/a/index.ts", CompliantHandler + "on('tool.call', { tool: 'Edit' }, async ($, e, next) => {\n  const go = once(next)\n  try {\n    if (next.signal.aborted) return go(e)\n    return go(e)\n  } catch { return go.fallback(e) }\n})\n");
        tree.Write("mods/shared/guard.ts", "next(e)\n");
        tree.Write("mods/a/a.test.ts", "on('x', (_$, e) => next(e))\n");

        Assert.Empty(HandlersOutsideTheNoThrowGuard(tree.Root, ModSourceFiles(tree.Root)));
    }

    [Theory]
    [InlineData("on('turn.start', ANY_TURN_START, async ($, e, next) => {\n  return next(e)\n})\n", "once(next)")]
    [InlineData("on('turn.start', ANY_TURN_START, async ($, e, next) => {\n  const go = once(next)\n  return go(e)\n})\n", "go.fallback(e)")]
    [InlineData("on('turn.start', ANY_TURN_START, async ($, e, next) => {\n  const go = once(next)\n  try { return next(e) } catch { return go.fallback(e) }\n})\n", "next directly")]
    [InlineData("on('command.run', { command: 'x' }, async ($) => {\n  return { text: 'x' }\n})\n", "once(next)")]
    public void AHandlerOutsideTheGuard_IsReportedNamingTheFileAndWhatIsMissing(string source, string expected)
    {
        using var tree = new TempTree();
        tree.Write("mods/a/index.ts", source);

        var only = Assert.Single(HandlersOutsideTheNoThrowGuard(tree.Root, ModSourceFiles(tree.Root)));

        Assert.Contains("mods/a/index.ts:1", only);
        Assert.Contains(expected, only);
    }

    // The engine's budget `.catch` is the one direct `next(e)` a handler may carry; any other is not.
    [Fact]
    public void TheBudgetCatch_IsTheOnlyDirectNextCallAHandlerMayCarry()
    {
        using var tree = new TempTree();
        tree.Write("mods/a/index.ts", CompliantHandler.TrimEnd('\n') + ".catch(($, e, next) => next(e))\n");
        tree.Write("mods/b/index.ts", CompliantHandler.TrimEnd('\n') + ".catch(($, e, next) => { return next(e) })\n");

        var only = Assert.Single(HandlersOutsideTheNoThrowGuard(tree.Root, ModSourceFiles(tree.Root)));

        Assert.Contains("mods/b/index.ts:1", only);
    }

    // Where a registration is written must not take it out of the rule. Each of these is an
    // unwrapped registration the rule has to see, however it hides.
    [Theory]
    [InlineData("const x = 1; on('turn.start', ANY_TURN_START, async ($, e, next) => next(e))\n")]
    [InlineData("return on('turn.start', ANY_TURN_START, async ($, e, next) => next(e))\n")]
    [InlineData("register(on('turn.start', ANY_TURN_START, async ($, e, next) => next(e)))\n")]
    [InlineData("on(`turn.start`, ANY_TURN_START, async ($, e, next) => next(e))\n")]
    [InlineData("on(\n  'turn.start',\n  ANY_TURN_START,\n  async ($, e, next) => next(e),\n)\n")]
    [InlineData("on('turn.start', ANY_TURN_START, handler)\n")]
    public void AnUnwrappedRegistrationWhereverItIsWritten_IsReported(string source)
    {
        using var tree = new TempTree();
        tree.Write("mods/a/index.ts", source);

        Assert.Single(HandlersOutsideTheNoThrowGuard(tree.Root, ModSourceFiles(tree.Root)));
    }

    // A wrapped handler on the same line must not excuse the unwrapped one that follows it, and
    // `next` reached through an alias or spelled with a space is still a direct use.
    [Fact]
    public void AnUnwrappedRegistrationAfterAWrappedOneOnTheSameLine_IsReportedAlone()
    {
        using var tree = new TempTree();
        tree.Write("mods/a/index.ts", CompliantHandler.TrimEnd('\n') + "; on('turn.complete', ANY_TURN_COMPLETE, async ($, e, next) => next(e))\n");

        var only = Assert.Single(HandlersOutsideTheNoThrowGuard(tree.Root, ModSourceFiles(tree.Root)));

        Assert.Contains("mods/a/index.ts:8", only);
    }

    [Theory]
    [InlineData("const n = next\n  try { return await n(e) } catch { return go.fallback(e) }", "aliasing")]
    [InlineData("try { return await go(e) } catch { return next (e) }", "directly")]
    [InlineData("try { return await go(e) } catch { return helper(e, next) }", "aliasing")]
    public void NextUsedOutsideTheWrapper_EvenWithTheWrapperPresent_IsReported(string body, string expected)
    {
        using var tree = new TempTree();
        tree.Write("mods/a/index.ts", "on('turn.start', ANY_TURN_START, async ($, e, next) => {\n  const go = once(next)\n  " + body + "\n})\n");

        var only = Assert.Single(HandlersOutsideTheNoThrowGuard(tree.Root, ModSourceFiles(tree.Root)));

        Assert.Contains(expected, only);
    }

    // The nearest inputs that must not be reported: a registration or a use of `next` that is only
    // text, and the property reads and the wrapped form a handler legitimately has.
    [Fact]
    public void RegistrationsAndNextThatAreOnlyCommentOrStringText_AreNotReported()
    {
        using var tree = new TempTree();
        tree.Write(
            "mods/a/index.ts",
            "// on('turn.start', ANY_TURN_START, async ($, e, next) => next(e))\n"
            + "const s = \"on('turn.start', x)\"\n"
            + CompliantHandler.Replace("return await go(e)", "const note = 'press next to go on'\n    const key = `next-${e.turnId}`\n    if (next.signal.aborted) return go(e)\n    return await go(e)"));

        Assert.Empty(HandlersOutsideTheNoThrowGuard(tree.Root, ModSourceFiles(tree.Root)));
    }

    // The rule is the only per-registration enforcement (the behaviour tests cannot see a missing
    // wrapper on a hook that does not throw), so every registration in the plugin is checked by
    // taking its wrapper away and expecting exactly that registration to be reported.
    [Fact]
    public void RemovingTheWrapperFromAnyShippedRegistration_IsReportedAtThatRegistrationAlone()
    {
        var dir = PluginSandbox.PluginDirectory;
        var checkedRegistrations = 0;
        foreach (var file in ModSourceFiles(dir))
        {
            var relative = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (!IsInGuardScope(relative, file))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (var (index, eventName, end) in HandlerRegistrations(text, Mask(text)))
            {
                var segment = text.Substring(index, end - index);
                var stripped = UnwrapHandler(segment);
                Assert.NotEqual(segment, stripped);
                var line = text.AsSpan(0, index).Count('\n') + 1;

                using var tree = new TempTree();
                tree.Write(relative, text[..index] + stripped + text[end..]);
                var found = HandlersOutsideTheNoThrowGuard(tree.Root, ModSourceFiles(tree.Root));

                var only = Assert.Single(found);
                Assert.Contains($"{relative}:{line}:", only);
                checkedRegistrations++;
            }
        }

        Assert.True(checkedRegistrations >= 24, $"Only {checkedRegistrations} registrations were checked; the scan lost some.");
    }

    // One compliant handler must not excuse the next one in the same file.
    [Fact]
    public void ASecondHandlerInTheSameFileThatIsNotWrapped_IsReportedAlone()
    {
        using var tree = new TempTree();
        tree.Write("mods/a/index.ts", CompliantHandler + "on('turn.complete', ANY_TURN_COMPLETE, async ($, e, next) => next(e))\n");

        var only = Assert.Single(HandlersOutsideTheNoThrowGuard(tree.Root, ModSourceFiles(tree.Root)));

        Assert.Contains("mods/a/index.ts:9", only);
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

    private static readonly Dictionary<string, string> SharedEventConstants = new()
    {
        ["session.start"] = "ANY_SESSION_START",
        ["turn.start"] = "ANY_TURN_START",
        ["turn.complete"] = "ANY_TURN_COMPLETE",
        ["prompt.submit"] = "ANY_PROMPT_SUBMIT",
    };

    // (file, event) pairs excused while a mod's integration round is pending. Empty now that every
    // mod passes its constant; the mechanism stays so a new round can land in two steps, and
    // StaleAllowListEntries fails any entry whose registration no longer needs excusing.
    private static readonly (string File, string Event)[] SharedConstantAllowList = [];

    private static readonly Regex SharedEventRegistrationPattern = new(
        @"\bon\(\s*['""](session\.start|turn\.start|turn\.complete|prompt\.submit)['""]\s*,\s*([A-Za-z_$][\w$]*)?",
        RegexOptions.Compiled);

    private static List<string> RegistrationsWithoutTheSharedConstant(
        string pluginDirectory,
        IEnumerable<string> files,
        (string File, string Event)[] allowList) =>
        SharedEventViolations(pluginDirectory, files)
            .Where(v => !allowList.Contains((v.File, v.Event)))
            .Select(v => v.Message)
            .ToList();

    // An excuse for a registration that is gone, or that now passes its constant, would go on
    // hiding the next bare one in that file, so each entry must still excuse a real violation.
    private static List<string> StaleAllowListEntries(
        string pluginDirectory,
        IEnumerable<string> files,
        (string File, string Event)[] allowList)
    {
        var live = SharedEventViolations(pluginDirectory, files).Select(v => (v.File, v.Event)).ToHashSet();
        return allowList
            .Where(e => !live.Contains(e))
            .Select(e => $"{e.File}: allow-list entry for '{e.Event}' excuses nothing; remove it from SharedConstantAllowList")
            .ToList();
    }

    private static List<(string File, string Event, string Message)> SharedEventViolations(
        string pluginDirectory,
        IEnumerable<string> files)
    {
        var found = new List<(string File, string Event, string Message)>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(pluginDirectory, file).Replace('\\', '/');
            if (relative.StartsWith("mods/shared/", StringComparison.Ordinal)
                || Path.GetFileName(file).Contains(".test.", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (Match match in SharedEventRegistrationPattern.Matches(text))
            {
                var eventName = match.Groups[1].Value;
                var constant = SharedEventConstants[eventName];
                var passed = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;
                var isMatcherPosition = text.AsSpan(match.Index + match.Length).TrimStart().StartsWith(",");
                if (passed == constant && isMatcherPosition)
                {
                    continue;
                }

                var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                found.Add((relative, eventName, $"{relative}:{line}: on('{eventName}') must pass {constant} as its second argument (import it from ../shared/events)"));
            }
        }

        return found;
    }

    // A registration is found in the source text and then judged on a masked copy of it, the same
    // length with comments and the insides of strings blanked: a registration inside a comment does
    // not count, and the word "next" in a message is not a use of `next`. It is found wherever it
    // stands (mid-line, in a nested call, over several lines, with a backtick event name), so a
    // registration cannot step out of the rule by where it is written. The check is on presence: it
    // cannot tell that the `try` covers the whole body.
    private static readonly Regex HandlerRegistrationPattern = new(
        @"(?<![\w.$])on\(\s*['""`]([A-Za-z][A-Za-z0-9.]*)['""`]",
        RegexOptions.Compiled);

    private static readonly Regex OnceNextPattern = new(@"\bonce\(\s*next\s*\)", RegexOptions.Compiled);

    private static readonly Regex DirectNextCallPattern = new(@"(?<![\w.$])next\s*\(", RegexOptions.Compiled);

    // `next` as a value (an alias, an argument) rather than a property read or the one wrapped form.
    private static readonly Regex BareNextPattern = new(@"(?<![\w.$'""`-])next\b(?!\s*\.)", RegexOptions.Compiled);

    private static readonly Regex SignatureNextPattern = new(@",\s*next\s*\)\s*=>", RegexOptions.Compiled);

    // The engine's own `.catch` runs when a hook outruns its budget, which no try/catch in the hook
    // can see; its `next(e)` replays what an earlier call settled to rather than calling again.
    private static readonly Regex BudgetCatchPattern = new(
        @"\.catch\(\s*\(\s*\$\s*,\s*e\s*,\s*next\s*\)\s*=>\s*next\(\s*e\s*\)\s*\)",
        RegexOptions.Compiled);

    private static string Mask(string text)
    {
        var chars = text.ToCharArray();
        var depths = new Stack<int>();
        var inTemplate = false;

        void Blank(int at)
        {
            if (chars[at] != '\n' && chars[at] != '\r')
            {
                chars[at] = ' ';
            }
        }

        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (inTemplate)
            {
                if (c == '\\')
                {
                    Blank(i);
                    if (i + 1 < chars.Length)
                    {
                        Blank(++i);
                    }
                }
                else if (c == '`')
                {
                    inTemplate = false;
                }
                else if (c == '$' && i + 1 < chars.Length && chars[i + 1] == '{')
                {
                    i++;
                    depths.Push(0);
                    inTemplate = false;
                }
                else
                {
                    Blank(i);
                }

                continue;
            }

            if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
            {
                while (i < chars.Length && chars[i] != '\n')
                {
                    Blank(i++);
                }
            }
            else if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                Blank(i++);
                Blank(i++);
                while (i < chars.Length && !(chars[i] == '*' && i + 1 < chars.Length && chars[i + 1] == '/'))
                {
                    Blank(i++);
                }

                if (i < chars.Length)
                {
                    Blank(i++);
                    Blank(i);
                }
            }
            else if (c == '\'' || c == '"')
            {
                for (i++; i < chars.Length && chars[i] != c && chars[i] != '\n'; i++)
                {
                    if (chars[i] == '\\' && i + 1 < chars.Length)
                    {
                        Blank(i++);
                    }

                    Blank(i);
                }
            }
            else if (c == '`')
            {
                inTemplate = true;
            }
            else if (c == '{' && depths.Count > 0)
            {
                depths.Push(depths.Pop() + 1);
            }
            else if (c == '}' && depths.Count > 0)
            {
                var depth = depths.Pop();
                if (depth == 0)
                {
                    inTemplate = true;
                }
                else
                {
                    depths.Push(depth - 1);
                }
            }
        }

        return new string(chars);
    }

    private static List<(int Index, string Event, int End)> HandlerRegistrations(string text, string masked)
    {
        var starts = HandlerRegistrationPattern.Matches(text)
            .Where(m => string.CompareOrdinal(masked, m.Index, "on(", 0, 3) == 0)
            .ToList();
        return starts
            .Select((m, i) => (m.Index, m.Groups[1].Value, i + 1 < starts.Count ? starts[i + 1].Index : text.Length))
            .ToList();
    }

    private static bool IsInGuardScope(string relative, string file) =>
        !relative.StartsWith("mods/shared/", StringComparison.Ordinal)
        && !Path.GetFileName(file).Contains(".test.", StringComparison.Ordinal);

    // A handler's text runs from its `on(` to the next registration or the end of the file, so a
    // handler sharing a file with another cannot be excused by it; a helper after the last handler
    // is the one thing that could confuse the check, and none of the mods has one.
    private static List<string> HandlersOutsideTheNoThrowGuard(string pluginDirectory, IEnumerable<string> files)
    {
        var found = new List<string>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(pluginDirectory, file).Replace('\\', '/');
            if (!IsInGuardScope(relative, file))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var masked = Mask(text);
            foreach (var (index, _, end) in HandlerRegistrations(text, masked))
            {
                var handler = masked.Substring(index, end - index);
                var rest = BudgetCatchPattern.Replace(handler, string.Empty);
                rest = OnceNextPattern.Replace(rest, string.Empty);
                rest = SignatureNextPattern.Replace(rest, " =>");
                var missing = new List<string>();
                if (!OnceNextPattern.IsMatch(handler))
                {
                    missing.Add("wrap next with once(next)");
                }

                if (!handler.Contains(".fallback(", StringComparison.Ordinal))
                {
                    missing.Add("catch to go.fallback(e)");
                }

                if (DirectNextCallPattern.IsMatch(rest))
                {
                    missing.Add("stop calling next directly (call the once wrapper)");
                }
                else if (BareNextPattern.IsMatch(rest))
                {
                    missing.Add("stop passing or aliasing next (use the once wrapper)");
                }

                if (missing.Count > 0)
                {
                    var line = text.AsSpan(0, index).Count('\n') + 1;
                    found.Add($"{relative}:{line}: handler must {string.Join(" and ", missing)} (see mods/shared/guard.ts)");
                }
            }
        }

        return found;
    }

    // What a developer removing the wrapper leaves behind: no once(next), calls straight to next.
    private static string UnwrapHandler(string handler)
    {
        var text = Regex.Replace(handler, @"^[ \t]*const go = once\(next\)\r?\n", string.Empty, RegexOptions.Multiline);
        text = text.Replace("go.fallback(", "next(", StringComparison.Ordinal);
        return Regex.Replace(text, @"(?<![\w.$])go\(", "next(");
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
