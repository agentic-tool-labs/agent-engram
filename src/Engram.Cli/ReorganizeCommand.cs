using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Cli;

/// <summary>
/// <c>engram reorganize</c>: re-files another tool's ledger out of user memory (<c>--ledger</c>) and
/// closes captures that are not the user's own words (<c>--captures</c>). Dry run unless
/// <c>--apply</c> is given; refuses while a server is alive; snapshots before its first write (D87).
/// </summary>
internal static partial class ReorganizeCommand
{
    private const string DefaultLedgerFrom = "/ah2";
    private const string DefaultLedgerTo = "/tools/ah2";
    private const string MisfiledReason = "misfiled capture: not the user's own words";

    private static readonly string[] CaptureRoots = [UserFacts.AboutYouPath, UserFacts.InstructionPath];

    private static readonly string[] ToolingMarkers =
        ["[hierarchy-msg", "ah msg", "ah2", "[Subagent", "task-notification"];

    private const string Usage =
        """
        usage: engram reorganize <--ledger|--captures> [options]

          --ledger [--from /ah2] [--to /tools/ah2]
                                 re-file another tool's ledger to scope "tool" under --to
          --captures             list /user captures that look like agent text, by rule and tier
          --apply                act; without it nothing is written
          --captures --apply --tier a
                                 close every tier A (agent-signature) suspect
          --captures --apply --only f…,f…
                                 close exactly these printed suspects

        Refuses while the server is running. Snapshots the store before the first write.
        """;

    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        var ledger = false;
        var captures = false;
        var apply = false;
        var from = DefaultLedgerFrom;
        var to = DefaultLedgerTo;
        string? tier = null;
        string? only = null;

        for (var i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--ledger":
                    ledger = true;
                    break;
                case "--captures":
                    captures = true;
                    break;
                case "--apply":
                    apply = true;
                    break;
                case "--from" or "--to" or "--tier" or "--only":
                    if (i + 1 >= rest.Length)
                    {
                        stderr.WriteLine($"error: {rest[i]} needs a value");
                        return 1;
                    }

                    var value = rest[++i];
                    switch (rest[i - 1])
                    {
                        case "--from": from = value; break;
                        case "--to": to = value; break;
                        case "--tier": tier = value; break;
                        default: only = value; break;
                    }

                    break;
                default:
                    stderr.WriteLine($"error: unexpected argument '{rest[i]}'");
                    stderr.Write(Usage);
                    return 1;
            }
        }

        if (!ledger && !captures)
        {
            stderr.Write(Usage);
            return 1;
        }

        if (!from.StartsWith('/') || !to.StartsWith('/') || Under(to, from))
        {
            stderr.WriteLine("error: --from and --to take rooted memory paths, and --to may not lie under --from");
            return 1;
        }

        if (tier is not null && tier != "a")
        {
            stderr.WriteLine("error: --tier takes only 'a'; tier B suspects are closed by handle with --only");
            return 1;
        }

        if ((tier is not null || only is not null) && !captures)
        {
            stderr.WriteLine("error: --tier and --only belong to --captures");
            return 1;
        }

        if (captures && apply && (tier is null) == (only is null))
        {
            stderr.WriteLine("error: --captures --apply needs exactly one of:");
            stderr.WriteLine("  engram reorganize --captures --apply --tier a");
            stderr.WriteLine("  engram reorganize --captures --apply --only f…,f…");
            return 1;
        }

        var onlyIds = new HashSet<long>();
        if (only is not null)
        {
            foreach (var handle in only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!FactCatalog.TryParseHandle(handle, out var id))
                {
                    stderr.WriteLine($"error: '{handle}' is not a fact handle");
                    return 1;
                }

                onlyIds.Add(id);
            }
        }

        var home = EngramHome.ResolveFromProcess(homePath);
        if (!File.Exists(home.DatabasePath))
        {
            stderr.WriteLine($"error: no store at {home.DatabasePath} — run 'engram init' first");
            return 1;
        }

        var lifecycle = new ServerLifecycle(new ProcessInspector(), new HttpServerHealthChecker(), new ProcessServerLauncher());
        if (lifecycle.Status(home, EngramVersion.Current, ServerLifecycleTimeouts.Default.HealthCheckTimeout).ServerIsAlive)
        {
            stderr.WriteLine("error: the server is running, and its recall and embedding backlog read these rows.");
            stderr.WriteLine("       Run 'engram stop' first, then reorganize, then 'engram start'.");
            return 1;
        }

        using var connection = apply ? EngramDatabase.OpenInitialized(home) : EngramDatabase.Open(home);
        var room = Tui.Detect().Room;
        var snapshotTaken = false;

        void SnapshotOnce()
        {
            if (snapshotTaken)
            {
                return;
            }

            var snapshot = BackupStore.Take(connection, home, DateTimeOffset.UtcNow, label: "pre-reorganize");
            BackupStore.PruneAfterWrite(home, snapshot);
            stdout.WriteLine($"snapshot: {snapshot.Name}");
            snapshotTaken = true;
        }

        var exit = 0;
        if (ledger)
        {
            exit = Ledger(connection, from, to, apply, room, SnapshotOnce, stdout);
        }

        if (captures && exit == 0)
        {
            exit = Captures(connection, apply, tier is not null, onlyIds, room, SnapshotOnce, stdout, stderr);
        }

        return exit;
    }

    private static int Ledger(
        SqliteConnection connection, string from, string to, bool apply, int room, Action snapshot, TextWriter stdout)
    {
        var selected = ReadLiveUnder(connection, from);
        stdout.WriteLine($"ledger: {selected.Count} live fact(s) under {from} → {to}, scope \"{FactStore.ToolScope}\"");

        foreach (var (id, path, body) in selected)
        {
            stdout.WriteLine(WithBody($"  {FactCatalog.HandleFor(id)}  {path} → {Target(path, from, to)}", body, room));
        }

        if (!apply)
        {
            stdout.WriteLine("Dry run only — nothing was changed. Re-run with --apply to re-file them.");
            return 0;
        }

        if (selected.Count == 0)
        {
            stdout.WriteLine("re-filed 0");
            return 0;
        }

        snapshot();

        var counts = new Dictionary<RefileOutcome, int>();
        var now = DateTimeOffset.UtcNow;
        var reason = $"re-filed: another tool's ledger, {from} → {to}";
        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            foreach (var (id, path, _) in selected)
            {
                var outcome = FactStore.Refile(
                    connection, transaction, id, Target(path, from, to), FactStore.ToolScope, "observed", reason, now).Outcome;
                counts[outcome] = counts.GetValueOrDefault(outcome) + 1;
            }

            transaction.Commit();
        }

        stdout.WriteLine(
            $"re-filed {counts.GetValueOrDefault(RefileOutcome.Refiled)}, "
            + $"already at target {counts.GetValueOrDefault(RefileOutcome.AlreadyAtTarget)}, "
            + $"skipped (no longer live) {counts.GetValueOrDefault(RefileOutcome.SourceNotLive)}, "
            + $"left in place (target holds a different belief) {counts.GetValueOrDefault(RefileOutcome.TargetConflict)}");
        return 0;
    }

    private static int Captures(
        SqliteConnection connection,
        bool apply,
        bool tierA,
        HashSet<long> onlyIds,
        int room,
        Action snapshot,
        TextWriter stdout,
        TextWriter stderr)
    {
        var live = CaptureRoots.SelectMany(root => FactStore.ReadSubtree(connection, root)).ToList();
        var inSessions = SessionBodies(connection, live.Select(f => f.Body).Distinct(StringComparer.Ordinal).ToList());
        var suspects = live
            .Select(f => (Fact: f, Rules: Rules(f.Body, inSessions)))
            .Where(s => s.Rules.Count > 0)
            .ToList();

        var tierASet = suspects.Where(s => s.Rules.Any(r => r is "R1" or "R2" or "R5")).ToList();
        var tierBSet = suspects.Except(tierASet)
            .OrderBy(s => s.Rules[0], StringComparer.Ordinal)
            .ThenByDescending(s => s.Fact.Body.Length)
            .ToList();

        stdout.WriteLine($"captures: {live.Count} live under {string.Join(", ", CaptureRoots)}; {suspects.Count} suspect");
        foreach (var (rule, name) in RuleNames)
        {
            stdout.WriteLine($"  {rule} {name}: {suspects.Count(s => s.Rules.Contains(rule))}");
        }

        stdout.WriteLine($"  tier A (agent signature): {tierASet.Count}");
        stdout.WriteLine($"  tier B (shape only): {tierBSet.Count}");

        stdout.WriteLine("tier A — closable as a set:");
        foreach (var (fact, rules) in tierASet)
        {
            stdout.WriteLine($"  {FactCatalog.HandleFor(fact.Id)}  {fact.SubjectPath}  [{string.Join(",", rules)}]");
            foreach (var line in fact.Body.Split('\n'))
            {
                stdout.WriteLine("    " + line.TrimEnd('\r'));
            }
        }

        stdout.WriteLine("tier B — closable only by handle:");
        foreach (var (fact, rules) in tierBSet)
        {
            stdout.WriteLine(WithBody(
                $"  {FactCatalog.HandleFor(fact.Id)}  {fact.SubjectPath}  [{string.Join(",", rules)}]", fact.Body, room));
        }

        stdout.WriteLine("to act:");
        stdout.WriteLine("  engram reorganize --captures --apply --tier a");
        stdout.WriteLine("  engram reorganize --captures --apply --only f…,f…");

        if (!apply)
        {
            stdout.WriteLine("Dry run only — nothing was changed.");
            return 0;
        }

        var suspectIds = suspects.Select(s => s.Fact.Id).ToHashSet();
        var unknown = onlyIds.Where(id => !suspectIds.Contains(id)).ToList();
        if (unknown.Count > 0)
        {
            stderr.WriteLine(
                "error: not a printed suspect, so not closed (this is not a general forget): "
                + string.Join(",", unknown.Select(FactCatalog.HandleFor)));
            return 1;
        }

        var close = tierA ? tierASet.Select(s => s.Fact.Id).ToList() : onlyIds.Order().ToList();
        if (close.Count == 0)
        {
            stdout.WriteLine("closed 0");
            return 0;
        }

        snapshot();

        var closed = 0;
        var now = DateTimeOffset.UtcNow;
        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            foreach (var id in close)
            {
                closed += FactStore.Forget(connection, transaction, id, MisfiledReason, now) ? 1 : 0;
            }

            transaction.Commit();
        }

        stdout.WriteLine($"closed {closed}: {string.Join(",", close.Select(FactCatalog.HandleFor))}");
        return 0;
    }

    private static readonly (string Rule, string Name)[] RuleNames =
    [
        ("R1", "agent-report opener"),
        ("R2", "tooling marker"),
        ("R5", "duplicate of a session note"),
        ("R3", "code citation"),
        ("R4", "over 400 characters"),
    ];

    /// <summary>The suspect rules a capture body trips, in rule order.</summary>
    internal static List<string> Rules(string body, IReadOnlySet<string> sessionBodies)
    {
        var rules = new List<string>();
        if (AgentOpener().IsMatch(body))
        {
            rules.Add("R1");
        }

        if (ToolingMarkers.Any(marker => body.Contains(marker, StringComparison.Ordinal)))
        {
            rules.Add("R2");
        }

        if (CodeCitation().IsMatch(body))
        {
            rules.Add("R3");
        }

        if (body.Length > 400)
        {
            rules.Add("R4");
        }

        if (sessionBodies.Contains(body))
        {
            rules.Add("R5");
        }

        return rules;
    }

    [GeneratedRegex(@"^(?:Verified|Implemented|Fixed|Added|Reviewed|Done|DONE|BLOCKED|VERDICT|Spec|Report)\b")]
    private static partial Regex AgentOpener();

    [GeneratedRegex(@"(?<![\w/.-])[\w./-]*[\w-]\.[A-Za-z][A-Za-z0-9]{0,9}:\d+\b")]
    private static partial Regex CodeCitation();

    /// <summary>Which of <paramref name="bodies"/> also exist, live or closed, as a <c>/sessions/</c> note.</summary>
    private static HashSet<string> SessionBodies(SqliteConnection connection, List<string> bodies)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (bodies.Count == 0)
        {
            return found;
        }

        var array = new JsonArray();
        foreach (var body in bodies)
        {
            ((IList<JsonNode?>)array).Add(JsonValue.Create(body));
        }

        // A range over ix_fact_path ('/' is 0x2F, so '/sessions0' bounds the subtree exactly — the
        // ReadSubtree argument), live and closed rows alike; the body list becomes an ephemeral
        // index rather than a scan of every fact per capture.
        var low = SessionFacts.Root + "/";
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT body
              FROM fact
             WHERE path >= $low AND path < $high
               AND body IN (SELECT value FROM json_each($bodies));
            """;
        command.Parameters.AddWithValue("$low", low);
        command.Parameters.AddWithValue("$high", SessionFacts.Root + "0");
        command.Parameters.AddWithValue("$bodies", array.ToJsonString());

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            found.Add(reader.GetString(0));
        }

        return found;
    }

    /// <summary>Live facts at or under <paramref name="prefix"/>, by <see cref="FactStore.MoveSubtree"/>'s boundary rule.</summary>
    private static List<(long Id, string Path, string Body)> ReadLiveUnder(SqliteConnection connection, string prefix)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.id, e.path, f.body
              FROM fact f
              JOIN entity e ON e.id = f.subject_id
             WHERE f.valid_to IS NULL
               AND substr(e.path, 1, $len) = $old
               AND (length(e.path) = $len OR substr(e.path, $len + 1, 1) IN ('/', '#'))
             ORDER BY f.id;
            """;
        command.Parameters.AddWithValue("$len", prefix.Length);
        command.Parameters.AddWithValue("$old", prefix);

        var rows = new List<(long, string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private static bool Under(string path, string prefix) =>
        path == prefix
        || (path.StartsWith(prefix, StringComparison.Ordinal) && path[prefix.Length] is '/' or '#');

    private static string Target(string path, string from, string to) => to + path[from.Length..];

    /// <summary>
    /// The handle and paths in full, then the body on the same line clipped to what is left —
    /// the address is what someone acts on, so it is never the part ellipsed.
    /// </summary>
    private static string WithBody(string head, string body, int room) =>
        head + "  " + Tui.Clip(body.ReplaceLineEndings(" "), Math.Max(room - head.Length - 2, 24));
}
