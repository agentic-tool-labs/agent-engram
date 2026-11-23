using System.Text.RegularExpressions;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// <c>engram --version</c> on the published binary (D108): the binary's version, the plugin's as the
/// newest session start recorded it, and a third line only when they differ. It reads, never writes,
/// and answers on a home that does not exist.
/// </summary>
public class VersionCommandTests
{
    private static readonly string BinaryVersion = Regex.Match(
        File.ReadAllText(Path.Combine(InstallerHarness.RepoRoot, "src", "Engram.Core", "EngramVersion.cs")),
        "Current = \"([^\"]+)\"").Groups[1].Value;

    private static string[] Lines(string stdout) =>
        stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();

    private static void Log(TestHome home, params string[] records) =>
        File.WriteAllLines(Path.Combine(home.Root, "telemetry.jsonl"), records);

    private static string SessionStart(string? pluginVersion) =>
        "{\"timestamp\":\"" + DateTime.UtcNow.ToString("o") + "\",\"session_id\":\"s\",\"kind\":\"session-start\""
        + (pluginVersion is null ? "" : ",\"plugin_version\":\"" + pluginVersion + "\"") + "}";

    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            files[Path.GetRelativePath(root, path)] = $"{info.Length}@{info.LastWriteTimeUtc:O}";
        }

        return files;
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("version")]
    public void OnAHomeThatDoesNotExist_PrintsTwoLines_AndCreatesNothing(string verb)
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        var root = Path.Combine(Path.GetTempPath(), "engram-e2e-" + Guid.NewGuid().ToString("N"));

        var (exitCode, stdout, stderr) = EngramProcess.Run(root, verb);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal([$"engram {BinaryVersion}", "plugin not recorded"], Lines(stdout));
        Assert.False(Directory.Exists(root), "--version created the home it was asked about");
    }

    /// <summary>An empty home stays empty: no <c>engram.db</c>, no log, nothing.</summary>
    [Fact]
    public void OnAnEmptyHome_ViaTheHomeFlag_WritesNothing()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome(initialize: false);
        var elsewhere = Path.Combine(Path.GetTempPath(), "engram-e2e-" + Guid.NewGuid().ToString("N"));

        // ENGRAM_HOME names a home that does not exist; --home must win, like every other verb.
        var (exitCode, stdout, _) = EngramProcess.Run(elsewhere, "--home", home.Root, "--version");

        Assert.Equal(0, exitCode);
        Assert.Equal([$"engram {BinaryVersion}", "plugin not recorded"], Lines(stdout));
        Assert.Empty(Directory.EnumerateFileSystemEntries(home.Root));
        Assert.False(Directory.Exists(elsewhere));
    }

    [Fact]
    public void OnAnInitialisedHome_LeavesEveryFileUnchanged()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        Log(home, SessionStart("0.0.1"));

        var before = Snapshot(home.Root);
        var (exitCode, _, _) = EngramProcess.Run(home.Root, "--version");
        var after = Snapshot(home.Root);

        Assert.Equal(0, exitCode);
        Assert.Equal(before, after);
    }

    [Fact]
    public void AnEmptyLog_IsNotRecorded()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        Log(home);

        var (exitCode, stdout, _) = EngramProcess.Run(home.Root, "--version");

        Assert.Equal(0, exitCode);
        Assert.Equal([$"engram {BinaryVersion}", "plugin not recorded"], Lines(stdout));
    }

    // Only the newest session start answers, as in doctor's row.
    [Fact]
    public void ANewestSessionStartWithoutTheField_IsNotRecorded()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        Log(home, SessionStart("0.0.1"), SessionStart(null));

        var (exitCode, stdout, _) = EngramProcess.Run(home.Root, "--version");

        Assert.Equal(0, exitCode);
        Assert.Equal([$"engram {BinaryVersion}", "plugin not recorded"], Lines(stdout));
    }

    [Fact]
    public void AMatchingPlugin_PrintsTwoLines()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        Log(home, SessionStart(BinaryVersion));

        var (exitCode, stdout, _) = EngramProcess.Run(home.Root, "--version");

        Assert.Equal(0, exitCode);
        Assert.Equal([$"engram {BinaryVersion}", $"plugin {BinaryVersion}"], Lines(stdout));
    }

    [Fact]
    public void ADifferentPlugin_AddsTheThirdLine_AndStillExitsZero()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        Log(home, SessionStart("0.0.1"));

        var (exitCode, stdout, _) = EngramProcess.Run(home.Root, "--version");

        Assert.Equal(0, exitCode);
        Assert.Equal(
            [$"engram {BinaryVersion}", "plugin 0.0.1", "versions differ: update the plugin and the binary to the same release"],
            Lines(stdout));
    }

    // The nearest input that must not select it: --version after another verb's name.
    [Fact]
    public void VersionAfterAnotherVerb_IsThatVerbsBusiness()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (_, stdout, _) = EngramProcess.Run(home.Root, "home", "--version");

        Assert.DoesNotContain($"engram {BinaryVersion}", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("plugin not recorded", stdout, StringComparison.Ordinal);
    }
}
