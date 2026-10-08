namespace Engram.EndToEnd.Tests;

/// <summary>
/// Tier 3. <c>engram invariant</c> on the published binary, including the one property the
/// indexer owns: an invariant outlives its file.
/// </summary>
public class InvariantCommandTests
{
    private const string Rule = "Always take the lock before reading the counter.";

    [Fact]
    public void AddListRemove_RoundTripsOnThePublishedBinary()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var (_, file) = IndexedRepo(home);

        var (addExit, addOut, addErr) = EngramProcess.Run(home.Root, "invariant", "add", file, Rule);
        Assert.Equal(0, addExit);
        Assert.Equal(string.Empty, addErr);
        Assert.Contains("recorded", addOut, StringComparison.Ordinal);

        var (_, repeatOut, _) = EngramProcess.Run(home.Root, "invariant", "add", file, Rule);
        Assert.Contains("already recorded", repeatOut, StringComparison.Ordinal);

        var (listExit, listOut, _) = EngramProcess.Run(home.Root, "invariant", "list", file);
        Assert.Equal(0, listExit);
        Assert.Contains(Rule, listOut, StringComparison.Ordinal);

        var handle = Handle(listOut);
        var (dryExit, dryOut, _) = EngramProcess.Run(home.Root, "invariant", "remove", handle);
        Assert.Equal(0, dryExit);
        Assert.Contains("Dry run", dryOut, StringComparison.Ordinal);
        Assert.Contains(Rule, EngramProcess.Run(home.Root, "invariant", "list").Stdout, StringComparison.Ordinal);

        var (removeExit, _, _) = EngramProcess.Run(home.Root, "invariant", "remove", handle, "--apply");
        Assert.Equal(0, removeExit);
        Assert.Contains("No live invariants", EngramProcess.Run(home.Root, "invariant", "list").Stdout, StringComparison.Ordinal);
    }

    // Deleting the file and re-running the indexer closes the file's code facts and must leave the
    // invariant alone. Falsify by making the invariant regenerable: it closes with the file.
    [Fact]
    public void AnInvariantSurvivesItsFileBeingDeletedAndTheRepoReindexed()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var (repo, file) = IndexedRepo(home);
        Assert.Equal(0, EngramProcess.Run(home.Root, "invariant", "add", file, Rule).ExitCode);

        File.Delete(file);
        var (indexExit, _, _) = EngramProcess.Run(home.Root, "index", "--apply", repo);
        Assert.Equal(0, indexExit);

        Assert.Contains(Rule, EngramProcess.Run(home.Root, "invariant", "list").Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AddOnAFileOutsideAnyIndexedRepo_ExitsOneAndNamesEnrolment()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var outside = Path.Combine(home.Root, "outside.txt");
        File.WriteAllText(outside, "x");

        var (exit, stdout, stderr) = EngramProcess.Run(home.Root, "invariant", "add", outside, Rule);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("repo enroll", stderr, StringComparison.Ordinal);
    }

    private static (string Repo, string File) IndexedRepo(TestHome home)
    {
        var repo = Path.Combine(home.Root, "inv-checkout");
        Directory.CreateDirectory(repo);
        var file = Path.Combine(repo, "NOTES.md");
        File.WriteAllText(file, "# Deployment notes\n\nShip on Tuesdays.\n");

        // A second file, so that deleting the first leaves a scan with something in it; an empty
        // repository never reaches the deletion pass.
        File.WriteAllText(Path.Combine(repo, "OTHER.md"), "# Other\n\nUnrelated.\n");

        // A git checkout, because that is what the registry records the canonical root of; a plain
        // directory is registered under the spelling it was given, which on macOS differs from the
        // resolved one for anything under /var.
        Assert.SkipUnless(GitInit(repo), "git is not available.");

        var (exit, _, stderr) = EngramProcess.Run(home.Root, "index", "--apply", repo);
        Assert.True(exit == 0, stderr);
        return (repo, file);
    }

    private static string Handle(string listLine)
    {
        var start = listLine.IndexOf('[', StringComparison.Ordinal) + 1;
        return listLine[start..listLine.IndexOf(']', StringComparison.Ordinal)];
    }

    private static bool GitInit(string directory)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("init");
            info.ArgumentList.Add("-q");

            using var process = System.Diagnostics.Process.Start(info);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
