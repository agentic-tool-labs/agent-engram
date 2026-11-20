namespace Engram.EndToEnd.Tests;

internal sealed class TestHome : IDisposable
{
    public string Root { get; }

    public TestHome(bool initialize = true)
    {
        Root = Path.Combine(Path.GetTempPath(), "engram-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        if (initialize)
        {
            var (exitCode, _, stderr) = EngramProcess.Run(Root, "init");
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"engram init failed with exit code {exitCode}: {stderr}");
            }
        }
    }

    public void Dispose() => RemoveTree(Root);

    /// <summary>
    /// Removes a test's temporary tree, tolerating a detached child still writing into it.
    /// </summary>
    /// <remarks>
    /// <para>Session start spawns a maintenance chain that outlives the hook by design, so a test
    /// can reach here while it is mid-write and the recursive delete fails with "directory not
    /// empty". That is a fact about detached children, not a defect the test is placed to catch:
    /// this is cleanup of a temp directory, and turning a cleanup race into a failed assertion
    /// reports a bug that is not there. Retry briefly, then leave it to the operating system's
    /// temp reaper.</para>
    /// <para>The one implementation, for every disposable home in this tier (D101).
    /// <c>InstallerTestHome</c> had its own single-shot delete, and that copy is the one that
    /// failed: measured, the chain runs at reduced priority and lives 0.2 s after the hook idle
    /// and 0.6–24 s under load, and the single-shot copy failed 3 of 12 combined-class runs under
    /// load.</para>
    /// </remarks>
    internal static void RemoveTree(string root)
    {
        for (var attempt = 0; attempt < 10 && Directory.Exists(root); attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
