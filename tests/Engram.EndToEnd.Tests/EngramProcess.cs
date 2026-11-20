using System.Diagnostics;

namespace Engram.EndToEnd.Tests;

internal static class EngramProcess
{
    public static (int ExitCode, string Stdout, string Stderr) Run(string home, params string[] args) =>
        RunWithStdin(home, stdin: null, args);

    public static (int ExitCode, string Stdout, string Stderr) RunWithStdin(string home, string? stdin, params string[] args) =>
        Execute(EndToEndBinary.Path!, home, stdin, args);

    // Distinct name rather than an inserted optional parameter: RunWithStdin's params array
    // means a positional insertion would silently swallow the first arg of every existing call.
    public static (int ExitCode, string Stdout, string Stderr) RunWithStdinFromDirectory(
        string home, string workingDirectory, string? stdin, params string[] args) =>
        Execute(EndToEndBinary.Path!, home, stdin, args, workingDirectory);

    internal static (int ExitCode, string Stdout, string Stderr) Execute(
        string binary, string home, string? stdin, string[] args, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };

        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["ENGRAM_HOME"] = home;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start engram process.");

        if (stdin is not null)
        {
            try
            {
                process.StandardInput.Write(stdin);
            }
            catch (IOException)
            {
                // A hook that exits before draining stdin is correct behaviour, not a
                // failure: an uninitialised home is meant to be a silent no-op, and the
                // switch that reads the payload is never reached. The write then lands on
                // a closed pipe. Swallowing it here keeps that path testable at all.
            }
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        // Drain both pipes concurrently while the bounded wait runs. Reading on this
        // thread first would trade one deadlock for another: a full pipe blocks the
        // child (GitFileLister documents that half), but ReadToEnd blocks until the
        // pipe's WRITE end closes, and that is not the same event as the child
        // exiting. Windows CI measured the gap — a crashed binary left its console
        // host holding the redirected handle, EOF never came, and the 10-second
        // bound below was never reached. Seven runs burned to the job cap on it.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("engram process did not exit within 10 seconds.");
        }

        // The process is gone; the readers may not be done — or may never finish, if
        // something it spawned inherited the pipe and outlived it. A bounded join is
        // what keeps one wedged invocation from hanging the whole suite.
        //
        // 30 s, not 5 (D101). The one time this fired in QA, seven tests hit it in the same
        // second, several of them for commands that spawn nothing, so no inherited pipe can
        // explain it: the readers were not scheduled at all. Thread-pool starvation in this test
        // host is the suspect and is not confirmed, which is why the message reports the pool
        // instead of naming a cause. The bound was never what guarded the detach leak — that
        // leaked chain lives 0.2 s, far inside either bound — so raising it hides nothing that
        // the 5 s bound caught. A pipe held open forever still fails here.
        var sinceExit = Stopwatch.StartNew();
        if (!Task.WaitAll([stdoutTask, stderrTask], 30_000))
        {
            throw new TimeoutException(
                $"engram exited but its output pipes had not closed {sinceExit.Elapsed.TotalSeconds:0.0} s later "
                    + $"(thread pool: {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} pending work items). "
                    + "Either something still holds the write end, or the readers were never scheduled.");
        }

        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }
}
