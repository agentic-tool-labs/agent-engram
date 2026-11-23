using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Engram.Core.Tests;

/// <summary>
/// The binary and the plugin are released as one version, so every place that states it must
/// agree.
/// </summary>
/// <remarks>
/// A plugin update is keyed on the version in the marketplace, so a plugin change released without
/// a bump never reaches an installed plugin, and a bump that misses one file ships a binary and a
/// plugin that disagree about which release they are. Nothing at run time can see that; this test
/// is the one place that does.
/// </remarks>
public class VersionAlignmentTests
{
    [Fact]
    public void EveryPlaceThatStatesTheVersion_StatesTheSameOne()
    {
        var root = FindRepoRoot();

        var plugin = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "plugin", ".claude-plugin", "plugin.json")))!;
        var marketplace = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".claude-plugin", "marketplace.json")))!;
        var changelog = Regex.Match(
            File.ReadAllText(Path.Combine(root, "CHANGELOG.md")),
            @"^## (\S+)",
            RegexOptions.Multiline);

        // The two places the plugin states its version to the binary at run time (D108).
        var sessionStart = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "plugin", "hooks", "hooks.json")))!
            ["hooks"]!["SessionStart"]!.AsArray()
            .SelectMany(group => group!["hooks"]!.AsArray())
            .Select(hook => hook!["command"]!.GetValue<string>())
            .Single(command => command.EndsWith(" hook session-start", StringComparison.Ordinal));
        var hookVersion = Regex.Match(sessionStart, @"^ENGRAM_PLUGIN_VERSION=(\S+) ");
        var bridgeVersion = Regex.Match(
            File.ReadAllText(Path.Combine(root, "plugin", "mods", "shared", "client.ts")),
            @"^export const PLUGIN_VERSION = '([^']*)'$",
            RegexOptions.Multiline);

        var stated = new List<(string Where, string? Version)>
        {
            ("EngramVersion.Current", EngramVersion.Current),
            ("plugin/.claude-plugin/plugin.json version", plugin["version"]?.GetValue<string>()),
            (".claude-plugin/marketplace.json metadata.version", marketplace["metadata"]?["version"]?.GetValue<string>()),
            ("CHANGELOG.md newest heading", changelog.Success ? changelog.Groups[1].Value : null),
            ("plugin/hooks/hooks.json session-start ENGRAM_PLUGIN_VERSION", hookVersion.Success ? hookVersion.Groups[1].Value : null),
            ("plugin/mods/shared/client.ts PLUGIN_VERSION", bridgeVersion.Success ? bridgeVersion.Groups[1].Value : null),
        };

        var plugins = marketplace["plugins"]!.AsArray();
        Assert.NotEmpty(plugins);
        foreach (var entry in plugins)
        {
            stated.Add(($".claude-plugin/marketplace.json plugins[{entry!["name"]}].version", entry["version"]?.GetValue<string>()));
        }

        var disagreeing = stated
            .Where(place => place.Version != EngramVersion.Current)
            .Select(place => $"{place.Where} = {place.Version ?? "(absent)"}")
            .ToList();

        Assert.True(
            disagreeing.Count == 0,
            $"EngramVersion.Current is {EngramVersion.Current}, but:\n  " + string.Join("\n  ", disagreeing));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Engram.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate repo root (Engram.sln) by walking up from AppContext.BaseDirectory.");
    }
}
