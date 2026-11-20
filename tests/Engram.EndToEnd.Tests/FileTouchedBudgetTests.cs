using System.Diagnostics;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// D4 rule 4 against the published binary: <c>file-touched</c> does not open the database,
/// and one spool file per invocation is what makes that safe under concurrency.
/// </summary>
public class FileTouchedBudgetTests
{
    private const int Warmup = 20;
    private const int Samples = 100;
    private const int Attempts = 3;

    /// <summary>
    /// Measured on this machine: a database open costs 2.1–2.4 ms (session-start +2.38,
    /// user-prompt +2.12 against the same floor) and <c>file-touched</c> costs +0.02. One
    /// millisecond sits an order of magnitude above the real cost and comfortably below the
    /// cheapest violation, so this fails when the rule is broken and not when the machine is
    /// merely busy.
    /// </summary>
    private const double MaxMarginalCostMs = 1.0;

    /// <summary>The ceiling on the floor-scaled allowance (D101); see where it is applied.</summary>
    private const double MaxScaledAllowanceMs = 1.25;

    /// <summary>
    /// The difference of minimums, not of medians. Process-start noise is one-sided — a
    /// sample is only ever slower than the deterministic work, never faster — so the
    /// minimum of 100 interleaved samples converges on the true cost of each arm, while
    /// the median wanders with however loaded the machine is. Measured where it mattered:
    /// a CI runner with a 66 ms floor (9× this machine) pushed the median difference to
    /// 1.44 ms with the hook doing nothing new. A violation still fails this, because
    /// deterministic work — a database open above all — shifts every sample, including
    /// the fastest one. Proven by planting exactly that: an <c>EngramDatabase.Open</c> in
    /// the hook measured 1.19 ms marginal under this estimator, red against the 1.0
    /// threshold — thinner than the old comment's 2.1–2.4 suggested, because those
    /// numbers were whole hooks and this is a bare open.
    /// </summary>
    private static double Cost(List<double> samples) => samples.Min();

    /// <summary>
    /// The hook's 10 ms budget is almost entirely process start, so asserting the absolute
    /// number would measure the machine. What the code controls is the difference between
    /// this hook and starting the binary to do nothing, and that difference is the rule.
    /// </summary>
    [Fact]
    public void FileTouched_CostsNothingMeasurableOverStartingTheBinary()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        for (var i = 0; i < Warmup; i++)
        {
            TimeNoOp(home.Root);
            TimeFileTouched(home.Root);
        }

        // Best of up to three independent measurements (D101). Under load the hook's real cost
        // grows faster than the floor, so the fastest-of-100 stops converging: idle the marginal
        // is 8% of the floor against a 10% allowance, and the five failures seen under load ran
        // 10.7–18.6%. A violation is deterministic — an opened database shifts every sample of
        // every attempt — so it fails all three, while noise has to win three times running.
        // Raising the ratio was rejected instead: the planted open is 22% of the idle floor, so a
        // 20% allowance would leave it two points to fail by.
        var attempts = new List<string>(Attempts);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var floor = new List<double>(Samples);
            var hook = new List<double>(Samples);

            // Interleaved rather than one batch after the other: a machine that gets busier
            // partway through then slows both arms equally instead of moving the difference this
            // asserts on.
            for (var i = 0; i < Samples; i++)
            {
                floor.Add(TimeNoOp(home.Root));
                hook.Add(TimeFileTouched(home.Root));
            }

            var floorCost = Cost(floor);
            var hookCost = Cost(hook);
            var marginal = hookCost - floorCost;

            // The allowance scales with the floor: a macOS runner with a 32.49 ms floor (4×
            // this machine) kept 2.68 ms of noise in the fastest-of-100 difference, so a flat
            // threshold measures the runner, not the hook. Ten percent of the floor only
            // rises above the local 1.0 ms when the floor itself says the machine is slow —
            // locally the max() keeps the threshold the planted open (1.19 ms) failed.
            //
            // Capped at 1.25 ms (D101), because the scaling admitted the violation it exists to
            // catch: through this test host the floor runs 13.3–24.1 ms, not the 8.6 ms the ten
            // percent was calibrated on, so the uncapped allowance (1.33–2.41 ms) let a planted
            // open (1.53–2.83 ms) pass. Clean costs 0.45–1.45 ms, and best-of-three takes the
            // lowest attempt. The price is the slow-runner headroom above: a runner whose
            // fastest-of-100 noise stays over 1.25 ms in all three attempts fails this.
            var allowance = Math.Max(MaxMarginalCostMs, Math.Min(floorCost * 0.1, MaxScaledAllowanceMs));

            if (marginal < allowance)
            {
                return;
            }

            attempts.Add(
                $"{marginal:0.00} ms (fastest of {Samples}: {hookCost:0.00} ms against a floor of "
                    + $"{floorCost:0.00} ms, allowance {allowance:0.00} ms)");
        }

        Assert.Fail(
            $"file-touched cost more than starting the binary to do nothing in all {Attempts} attempts: "
                + string.Join("; ", attempts) + ". "
                + "D4 rule 4 gives it a 10 ms budget that is almost all process start, so anything "
                + "measurable here — an opened database above all — is most of the headroom.");
    }

    /// <summary>
    /// One spool file per invocation, so concurrent hooks cannot lose each other's records.
    /// </summary>
    /// <remarks>
    /// The alternative D4 rejected is a single shared spool: <c>FileMode.Append</c> is
    /// seek-then-write rather than POSIX <c>O_APPEND</c>, so two hooks can resolve the same
    /// end-of-file offset and one record vanishes. A multi-file edit fires this hook once per
    /// file at once, which is exactly the case that would lose one.
    /// </remarks>
    [Fact]
    public void EightConcurrentTouches_EachLeaveTheirOwnRecord()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var queue = Path.Combine(home.Root, "queue");
        var before = Directory.Exists(queue) ? Directory.GetFiles(queue).Length : 0;

        const int Width = 8;
        const int Each = 4;

        Parallel.For(0, Width, _ =>
        {
            for (var i = 0; i < Each; i++)
            {
                EngramProcess.Run(home.Root, "hook", "file-touched");
            }
        });

        var written = Directory.GetFiles(queue).Length - before;

        Assert.Equal(Width * Each, written);
    }

    private static double TimeNoOp(string home) => Time(home, "home");

    private static double TimeFileTouched(string home) => Time(home, "hook", "file-touched");

    private static double Time(string home, params string[] args)
    {
        var stopwatch = Stopwatch.StartNew();
        EngramProcess.Run(home, args);
        stopwatch.Stop();

        return stopwatch.Elapsed.TotalMilliseconds;
    }
}
