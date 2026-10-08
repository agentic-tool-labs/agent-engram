using Engram.Core;

namespace Engram.Cli;

/// <summary>
/// <c>engram invariant</c> — authoring and listing of load-bearing rules about one file.
/// <c>add</c> acts immediately, since it can only create; <c>remove</c> is dry-run first (D49),
/// the same convention as <c>engram directive</c>.
/// </summary>
public static class InvariantCommand
{
    public static int Run(string? homePath, string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            stderr.WriteLine("error: expected a subcommand — add, list, or remove.");
            return 2;
        }

        var home = EngramHome.ResolveFromProcess(homePath);
        var rest = args[1..];

        return args[0] switch
        {
            "add" => Add(home, rest, stdout, stderr),
            "list" => List(home, rest, stdout, stderr),
            "remove" => Remove(home, rest, stdout, stderr),
            _ => Unknown(args[0], stderr),
        };
    }

    private static int Unknown(string subcommand, TextWriter stderr)
    {
        stderr.WriteLine($"error: unknown subcommand '{subcommand}' — expected add, list, or remove.");
        return 2;
    }

    private static int Add(EngramHome home, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[0]) || string.IsNullOrWhiteSpace(args[1]))
        {
            stderr.WriteLine("""Usage: engram invariant add <file> "<statement>" """);
            return 2;
        }

        var statement = args[1].Trim();
        var cost = TokenEstimator.Estimate(statement);
        if (cost > InvariantFacts.MaxStatementTokens)
        {
            stderr.WriteLine(
                $"error: refused — this statement costs {cost} tokens against a cap of "
                    + $"{InvariantFacts.MaxStatementTokens}. Shorten it.");
            return 1;
        }

        var fullPath = Path.GetFullPath(args[0]);
        if (Directory.Exists(fullPath))
        {
            stderr.WriteLine($"error: '{args[0]}' is a directory — invariants attach to files.");
            return 1;
        }

        if (!File.Exists(fullPath))
        {
            stderr.WriteLine($"error: no such file '{args[0]}'.");
            return 1;
        }

        using var connection = EngramDatabase.OpenInitialized(home);

        if (!TryResolveFile(connection, fullPath, args[0], stderr, out var entityPath))
        {
            return 1;
        }

        var factId = InvariantFacts.Add(connection, entityPath, statement, DateTimeOffset.UtcNow);
        if (factId is null)
        {
            stdout.WriteLine($"already recorded on {args[0]}: \"{statement}\"");
            return 0;
        }

        stdout.WriteLine($"[{FactCatalog.HandleFor(factId.Value)}] recorded on {args[0]}: \"{statement}\"");
        return 0;
    }

    private static int List(EngramHome home, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length > 1)
        {
            stderr.WriteLine("Usage: engram invariant list [<file>]");
            return 2;
        }

        using var connection = EngramDatabase.OpenInitialized(home);

        string? entityPath = null;
        if (args.Length == 1
            && !TryResolveFile(connection, Path.GetFullPath(args[0]), args[0], stderr, out entityPath))
        {
            return 1;
        }

        var invariants = InvariantFacts.ReadLive(connection, entityPath);
        if (invariants.Count == 0)
        {
            stdout.WriteLine(args.Length == 1 ? $"No live invariants on {args[0]}." : "No live invariants.");
            return 0;
        }

        foreach (var invariant in invariants)
        {
            var location = CodePaths.LocationText(invariant.FilePath) ?? invariant.FilePath;
            stdout.WriteLine($"[{FactCatalog.HandleFor(invariant.Id)}] {location}: \"{invariant.Body}\"");
        }

        return 0;
    }

    private static int Remove(EngramHome home, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var apply = args.Contains("--apply");
        var positional = args.Where(a => a != "--apply").ToArray();

        if (positional.Length != 1 || !FactCatalog.TryParseHandle(positional[0], out var factId))
        {
            stderr.WriteLine("Usage: engram invariant remove <id> --apply");
            return 2;
        }

        using var connection = EngramDatabase.OpenInitialized(home);

        if (!InvariantFacts.TryReadLive(connection, factId, out var target))
        {
            stderr.WriteLine($"error: no live invariant with id '{positional[0]}'.");
            return 1;
        }

        if (!apply)
        {
            stdout.WriteLine($"Would retire [{positional[0]}]: \"{target.Body}\"");
            stdout.WriteLine();
            stdout.WriteLine("Dry run only — nothing was changed. Re-run with --apply to retire it.");
            return 0;
        }

        FactStore.Forget(connection, factId, "retired via engram invariant remove", DateTimeOffset.UtcNow);

        stdout.WriteLine($"Retired [{positional[0]}]: \"{target.Body}\"");
        return 0;
    }

    private static bool TryResolveFile(
        Microsoft.Data.Sqlite.SqliteConnection connection, string fullPath, string given, TextWriter stderr, out string entityPath)
    {
        if (CodeEntityResolver.Resolve(connection, fullPath) is { } resolved)
        {
            entityPath = resolved.EntityPath;
            return true;
        }

        stderr.WriteLine(
            $"error: '{given}' is not inside an indexed repository. Enrol and index it first "
                + "('engram repo enroll', then 'engram index --apply').");
        entityPath = string.Empty;
        return false;
    }
}
