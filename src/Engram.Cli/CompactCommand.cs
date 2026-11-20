using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Cli;

/// <summary>
/// Prunes regenerable facts — dry-run by default, like everything that changes the store.
/// The report is the same selection that <c>--apply</c> acts on, stopped before the writes.
/// </summary>
internal static class CompactCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        var apply = false;
        var retired = false;
        var auto = false;
        string? path = null;

        for (var i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--apply":
                    apply = true;
                    break;
                case "--retired":
                    retired = true;
                    break;
                case "--auto":
                    auto = true;
                    break;
                case "--path":
                    if (i + 1 >= rest.Length)
                    {
                        stderr.WriteLine("error: --path needs a value");
                        return 1;
                    }

                    path = rest[++i];
                    break;
                default:
                    stderr.WriteLine($"error: unexpected argument '{rest[i]}'");
                    return 1;
            }
        }

        if (path is not null)
        {
            if (StoreCompactor.NormalizePath(path) is not { } normalized)
            {
                stderr.WriteLine("error: --path: a path starts with /, like /projects/acme/code/acme-api");
                return 2;
            }

            path = normalized;
        }

        if (auto && !retired)
        {
            stderr.WriteLine("error: --auto exists only with --retired");
            return 2;
        }

        if (retired && path is not null)
        {
            stderr.WriteLine("error: --retired and --path are mutually exclusive");
            return 2;
        }

        if (auto && !apply)
        {
            // An ambient dry run has no reader (D69).
            stderr.WriteLine("error: --auto needs --apply");
            return 2;
        }

        var home = EngramHome.ResolveFromProcess(homePath);

        // The one read of [indexing] auto_remove_retired (D97): the launcher appends this job
        // unconditionally and the verb decides. Off, absent or unparseable is a silent no-op.
        if (auto && !IndexingSettings.Read(ConfigFile.Load(home.ConfigPath)).AutoRemoveRetired)
        {
            return 0;
        }

        // Compact never creates a store: an empty database has nothing to prune, and
        // --apply materializing one would turn a typo'd --home into a new instance.
        if (!File.Exists(home.DatabasePath))
        {
            stderr.WriteLine($"error: no store at {home.DatabasePath} — run 'engram init' first");
            return 1;
        }

        try
        {
            using var connection = apply
                ? EngramDatabase.OpenInitialized(home)
                : EngramDatabase.Open(home);

            if (retired)
            {
                // The manual batch holds the write lock by design; say so before it does (D97).
                var report = StoreCompactor.CompactRetired(
                    connection, home, apply, auto, DateTimeOffset.UtcNow,
                    estimate => stdout.WriteLine(
                        "writes from running sessions (captures, remembers) will fail until this finishes, "
                        + $"about {estimate.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s"));
                PrintRetired(report, home, stdout);
                return 0;
            }

            var compacted = StoreCompactor.Compact(connection, home, path, apply, DateTimeOffset.UtcNow);

            Print(compacted, home, stdout);
            return 0;
        }
        catch (SqliteException e) when (e.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            stderr.WriteLine("error: this store predates the current schema. Re-run with --apply,");
            stderr.WriteLine("which migrates after snapshotting, or run 'engram doctor' to see where it stands.");
            return 1;
        }
    }

    private static void Print(CompactReport report, EngramHome home, TextWriter stdout)
    {
        stdout.WriteLine(home.DatabasePath);

        var verb = report.Applied ? string.Empty : "would be ";

        if (report.Path is null)
        {
            stdout.WriteLine(report.ClosedPruned > 0
                ? $"  facts: {Count(report.ClosedPruned, "closed regenerable fact")} {verb}pruned"
                : "  facts: no closed regenerable facts to prune");
        }
        else
        {
            stdout.WriteLine(report.FactsPruned > 0
                ? $"  facts: {report.LivePruned} live + {report.ClosedPruned} closed under {report.Path} {verb}pruned"
                : $"  facts: nothing regenerable under {report.Path}");
        }

        if (report.ProtectedByAuthoredHistory > 0)
        {
            stdout.WriteLine(
                $"  kept: {Count(report.ProtectedByAuthoredHistory, "fact")} that authored history, relations, reviews or sync requests depend on — never pruned");
        }

        if (report.SupersessionsRemoved > 0)
        {
            stdout.WriteLine($"  supersessions: {Count(report.SupersessionsRemoved, "row")} {verb}removed with them");
        }

        if (report.VectorsRemoved > 0)
        {
            stdout.WriteLine($"  vectors: {Count(report.VectorsRemoved, "row")} {verb}removed");
        }

        if (report.Path is not null)
        {
            stdout.WriteLine(report.EntitiesRemoved > 0
                ? $"  entities: {Count(report.EntitiesRemoved, "code entity")} {verb}removed"
                : "  entities: none left unreferenced");
            stdout.WriteLine($"  entities kept for rename history: {report.EntitiesKeptForRenameHistory}");
            stdout.WriteLine(
                $"  index state: {Count(report.FileStatesRemoved, "file record")} + "
                + $"{Count(report.ReposDeregistered, "repo registration")} {verb}cleared, so a re-index re-reads");
        }

        foreach (var note in report.Notes)
        {
            stdout.WriteLine($"  note: {note}");
        }

        stdout.WriteLine(report.Applied
            ? $"  snapshot: {report.SnapshotName} taken first; store vacuumed"
            : "  snapshot: would be taken first, unconditionally");

        if (!report.Applied)
        {
            stdout.WriteLine();
            stdout.WriteLine(report.AnythingToPrune
                ? "Dry run only — nothing was written. Re-run with --apply to compact."
                : "Dry run only — nothing to prune. --apply would still checkpoint and vacuum.");
        }
    }

    private static void PrintRetired(RetiredCompactReport report, EngramHome home, TextWriter stdout)
    {
        stdout.WriteLine(home.DatabasePath);
        var verb = report.Applied ? string.Empty : "would be ";

        if (report.Prefixes.Count == 0)
        {
            stdout.WriteLine("  retired paths: none to remove");
        }

        foreach (var prefix in report.Prefixes)
        {
            stdout.WriteLine($"  {prefix.Path}");
            stdout.WriteLine($"    facts: {prefix.LivePruned} live + {prefix.ClosedPruned} closed {verb}pruned");
            stdout.WriteLine($"    kept: {Count(prefix.ProtectedByAuthoredHistory, "fact")} that authored history, relations, reviews or sync requests depend on");
            stdout.WriteLine($"    entities: {prefix.EntitiesRemoved} {verb}removed, {prefix.EntitiesKeptForRenameHistory} kept for rename history");
            stdout.WriteLine($"    authored facts left in place: {prefix.AuthoredLeftInPlace}");
            foreach (var authored in prefix.AuthoredPaths ?? [])
            {
                stdout.WriteLine($"      {authored}");
            }

            stdout.WriteLine(
                $"    index state: {Count(prefix.FileStatesRemoved, "file record")} + "
                + $"{Count(prefix.ReposDeregistered, "repo registration")} {verb}cleared");
            stdout.WriteLine($"    vectors: {Count(prefix.VectorsRemoved, "row")} {verb}removed");
        }

        foreach (var skip in report.Skipped)
        {
            stdout.WriteLine($"  skipped: {skip.RepoPath}: {skip.Reason}");
        }

        if (report.Prefixes.Count > 0)
        {
            stdout.WriteLine(
                $"  total: {report.Prefixes.Sum(p => p.LivePruned)} live + {report.Prefixes.Sum(p => p.ClosedPruned)} closed facts, "
                + $"{report.Prefixes.Sum(p => p.ProtectedByAuthoredHistory)} protected, "
                + $"{report.Prefixes.Sum(p => p.EntitiesRemoved)} entities, "
                + $"{report.Prefixes.Sum(p => p.EntitiesKeptForRenameHistory)} kept for rename history, "
                + $"{report.Prefixes.Sum(p => p.AuthoredLeftInPlace)} authored left in place, "
                + $"{report.Prefixes.Sum(p => p.FileStatesRemoved)} file states, "
                + $"{report.Prefixes.Sum(p => p.ReposDeregistered)} registry rows, "
                + $"{Count(report.Vectors, "vector")}, {Count(report.Supersessions, "supersession")} {verb}removed");
        }

        foreach (var note in report.Notes)
        {
            stdout.WriteLine($"  note: {note}");
        }

        if (report.Applied)
        {
            if (report.SnapshotName is not null)
            {
                stdout.WriteLine(report.Vacuumed
                    ? $"  snapshot: {report.SnapshotName} taken first; store vacuumed"
                    : $"  snapshot: {report.SnapshotName} taken first; checkpointed, not vacuumed");
            }

            return;
        }

        stdout.WriteLine();
        stdout.WriteLine(report.Prefixes.Count > 0
            ? "Dry run only — nothing was written. Re-run with --apply to remove these."
            : "Dry run only — nothing to remove.");
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
