using System.Diagnostics;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// Drives <c>scripts/plugin-typecheck.sh</c>, the mods' type-check gate. Its contract is that it
/// checks against the running engine's types or stops with exit 2 and never runs tsc without them,
/// because tsc without the types reports hundreds of errors that read as a code problem.
/// Rows that need the engine skip with a named reason when <c>claude</c> is not on PATH.
/// </summary>
public class PluginTypecheckScriptTests
{
    private const string NoSuchEngine = "0.0.0-no-such-engine";

    private static readonly string RepoRoot = Directory.GetParent(PluginSandbox.PluginDirectory)!.FullName;

    private static readonly string Script = Path.Combine(RepoRoot, "scripts", "plugin-typecheck.sh");

    private static bool ClaudeOnPath { get; } = (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(directory => File.Exists(Path.Combine(directory, "claude")));

    private const string NoClaude = "claude is not on PATH, so the gate has no engine to take types from.";

    [Fact]
    public void NoTypesAnywhere_ExitsTwoNamingTheVersion_AndNeverRunsTsc()
    {
        using var plugin = new TempDirectory();

        var result = RunScript(plugin.Path, NoSuchEngine);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("cannot type-check", result.Error);
        Assert.Contains(NoSuchEngine, result.Error);
        Assert.Contains("tsc was not run", result.Error);
        Assert.DoesNotContain("tsc (typescript", result.Output);
        Assert.False(File.Exists(Path.Combine(plugin.Path, ".claude-plugin", "types", "claude-code", "index.d.ts")));
    }

    // macOS ships bash 3.2, where expanding an empty array under `set -u` aborts; with claude off
    // PATH the script must still end in its own exit 2 and message rather than an unbound variable.
    [Fact]
    public void WithoutClaude_UnderTheSystemBash_ExitsTwoWithTheMessage()
    {
        Assert.SkipUnless(File.Exists("/bin/bash"), "/bin/bash is not present.");
        Assert.SkipWhen(File.Exists("/usr/bin/claude") || File.Exists("/bin/claude"), "claude is installed in a system directory.");
        using var plugin = new TempDirectory();

        var result = RunScript(plugin.Path, NoSuchEngine, shell: "/bin/bash", path: "/usr/bin:/bin");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("tsc was not run", result.Error);
        Assert.DoesNotContain("unbound variable", result.Error);
    }

    // The nearest input to the row above that must not be accepted: types are present, but
    // stamped by another engine version. Checking against them would pass or fail code on an
    // API the running engine does not have.
    [Fact]
    public void TypesStampedByAnotherVersion_AreNotUsed()
    {
        using var plugin = new TempDirectory();
        WriteTypes(plugin.Path, stamp: "1.0.0 (some other engine)");

        var result = RunScript(plugin.Path, NoSuchEngine);

        Assert.Equal(2, result.ExitCode);
        Assert.DoesNotContain("types: reused", result.Output);
        Assert.DoesNotContain("tsc (typescript", result.Output);
    }

    // The stamp is the only difference from the row above, so this shows it is what the script
    // reads. The plugin is empty, so the run then stops at validate: past the types, before tsc.
    [Fact]
    public void TypesStampedByThisVersion_AreReused()
    {
        Assert.SkipUnless(ClaudeOnPath, NoClaude);
        using var plugin = new TempDirectory();
        WriteTypes(plugin.Path, stamp: NoSuchEngine + " (test)");

        var result = RunScript(plugin.Path, NoSuchEngine);

        Assert.Contains("types: reused", result.Output);
        Assert.DoesNotContain("cannot type-check", result.Error);
        Assert.NotEqual(2, result.ExitCode);
        Assert.DoesNotContain("tsc (typescript", result.Output);
    }

    [Fact]
    public void ShippedPlugin_PassesTheGate()
    {
        Assert.SkipUnless(ClaudeOnPath, NoClaude);

        var result = RunScript(pluginDirectory: null, version: null);

        SkipWhenEngineTypesAreUnavailable(result);
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}\n{result.Output}\n{result.Error}");
        Assert.Contains("tsc (typescript", result.Output);
    }

    // A relative plugin path must resolve against the repo, not the caller's directory, or a run from
    // elsewhere writes a types directory there and then fails validate, which reads as a code problem.
    [Fact]
    public void RunFromAnotherDirectory_UsesThePluginBesideTheScript()
    {
        Assert.SkipUnless(ClaudeOnPath, NoClaude);
        using var tree = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tree.Path, "scripts"));
        Directory.CreateDirectory(Path.Combine(tree.Path, "plugin"));
        var script = Path.Combine(tree.Path, "scripts", "plugin-typecheck.sh");
        File.Copy(Script, script);

        var result = RunScript(pluginDirectory: null, version: null, script: script, workingDirectory: Path.Combine(tree.Path, "scripts"));

        SkipWhenEngineTypesAreUnavailable(result);
        Assert.True(File.Exists(Path.Combine(tree.Path, "plugin", ".claude-plugin", "types", "claude-code", "index.d.ts")), result.Output);
        Assert.False(Directory.Exists(Path.Combine(tree.Path, "scripts", "plugin")));
    }

    // The gate is only worth running if it can fail: a copy of the plugin passes, and the same
    // copy with one type error planted in an unreferenced file does not.
    [Fact]
    public void PlantedTypeError_IsReported_AndTheCleanCopyBeforeItIsNot()
    {
        Assert.SkipUnless(ClaudeOnPath, NoClaude);
        using var copy = new TempDirectory();
        CopyPlugin(PluginSandbox.PluginDirectory, copy.Path);

        var clean = RunScript(copy.Path, version: null);
        SkipWhenEngineTypesAreUnavailable(clean);
        Assert.True(clean.ExitCode == 0, $"clean copy: exit {clean.ExitCode}\n{clean.Output}\n{clean.Error}");

        File.WriteAllText(Path.Combine(copy.Path, "mods", "shared", "planted.ts"), "export const planted: number = 'not a number'\n");
        var planted = RunScript(copy.Path, version: null);

        Assert.Equal(1, planted.ExitCode);
        Assert.Contains("TS2322", planted.Output);
    }

    // Whether this machine has types for the installed engine is environment state that changes with
    // every engine update, not a fault in the plugin; the script says how to fix it, and the rows
    // that pin its exit-2 contract run regardless.
    private static void SkipWhenEngineTypesAreUnavailable(ScriptResult result) =>
        Assert.SkipWhen(result.ExitCode == 2 && result.Error.Contains("cannot type-check"), result.Error);

    private static void WriteTypes(string pluginDirectory, string stamp)
    {
        var types = Path.Combine(pluginDirectory, ".claude-plugin", "types");
        Directory.CreateDirectory(Path.Combine(types, "claude-code"));
        File.WriteAllText(Path.Combine(types, "claude-code", "index.d.ts"), "export {}\n");
        File.WriteAllText(Path.Combine(types, "tsconfig.json"), "{}\n");
        File.WriteAllText(Path.Combine(types, ".engine-version"), stamp + "\n");
    }

    private static void CopyPlugin(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            if (IsLaidTypes(relative))
            {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (IsLaidTypes(relative))
            {
                continue;
            }

            File.Copy(file, Path.Combine(destination, relative));
        }
    }

    private static bool IsLaidTypes(string relative) =>
        relative.Replace('\\', '/').StartsWith(".claude-plugin/types", StringComparison.Ordinal);

    private static ScriptResult RunScript(string? pluginDirectory, string? version, string shell = "bash", string? path = null, string? script = null, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo(shell)
        {
            WorkingDirectory = workingDirectory ?? RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        startInfo.ArgumentList.Add(script ?? Script);
        if (pluginDirectory is not null)
        {
            startInfo.Environment["PLUGIN_TYPECHECK_PLUGIN_DIR"] = pluginDirectory;
        }

        if (version is not null)
        {
            startInfo.Environment["PLUGIN_TYPECHECK_VERSION"] = version;
        }

        if (path is not null)
        {
            startInfo.Environment["PATH"] = path;
        }

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("plugin-typecheck.sh did not finish in five minutes.");
        }

        return new ScriptResult(process.ExitCode, output.Result, error.Result);
    }

    private sealed record ScriptResult(int ExitCode, string Output, string Error);

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "engram-typecheck-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
