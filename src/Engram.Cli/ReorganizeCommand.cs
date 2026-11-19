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
    // Fixed, not options (D87 ruling 10): there is no second caller, and an unbounded source prefix
    // could re-file session notes or directives.
    private const string LedgerFrom = "/ah2";
    private const string LedgerTo = "/tools/ah2";
    private const string MisfiledReason = "misfiled capture: not the user's own words";

    private static readonly string[] CaptureRoots = [UserFacts.AboutYouPath, UserFacts.InstructionPath];

    private static readonly string[] ToolingMarkers =
        ["[hierarchy-msg", "ah msg", "ah2", "[Subagent", "task-notification"];

    private const string Usage =
        """
        usage: engram reorganize <--ledger|--captures> [options]

          --ledger               re-file the ah2 ledger from /ah2 to /tools/ah2, scope "tool"
          --captures             list /user captures that look like agent text, by rule and tier
          --apply                act; without it nothing is written
          --captures --apply --tier a
                                 close every tier A (agent-signature) suspect
          --captures --apply --only f…,f…
                                 close exactly these printed suspects

        --apply refuses while the server is running. Snapshots the store before the first write.
        """;

    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        var ledger = false;
        var captures = false;
        var apply = false;
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
                case "--tier" or "--only":
                    if (i + 1 >= rest.Length)
                    {
                        stderr.WriteLine($"error: {rest[i]} needs a value");
                        return 1;
                    }

                    if (rest[i] == "--tier")
                    {
                        tier = rest[++i];
                    }
                    else
                    {
                        only = rest[++i];
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

        // Only --apply refuses: a dry run only reads, and the capture list has to be reviewable
        // without stopping memory for every session (D87 ruling 2; D38/D42 for the apply).
        var lifecycle = new ServerLifecycle(new ProcessInspector(), new HttpServerHealthChecker(), new ProcessServerLauncher());
        var serverAlive = lifecycle.Status(home, EngramVersion.Current, ServerLifecycleTimeouts.Default.HealthCheckTimeout).ServerIsAlive;
        if (serverAlive && apply)
        {
            stderr.WriteLine("error: the server is running, and its recall and embedding backlog read these rows.");
            stderr.WriteLine("       Run 'engram stop' first, then reorganize, then 'engram start'.");
            return 1;
        }

        using var connection = apply ? EngramDatabase.OpenInitialized(home) : EngramDatabase.Open(home);
        var room = Tui.Detect().Room;

        if (serverAlive)
        {
            stdout.WriteLine("note: the server is running; --apply will need it stopped first ('engram stop').");
        }

        // Every captures refusal is decided before any step writes, so a combined run never
        // commits the ledger and then refuses the captures.
        (int LiveCount, List<(StoredFact Fact, List<string> Rules)> Suspects)? suspects =
            captures ? ReadSuspects(connection) : null;
        if (suspects is not null && apply)
        {
            var suspectIds = suspects.Value.Suspects.Select(s => s.Fact.Id).ToHashSet();
            var unknown = onlyIds.Where(id => !suspectIds.Contains(id)).ToList();
            if (unknown.Count > 0)
            {
                stderr.WriteLine(
                    "error: not a printed suspect, so not closed (this is not a general forget): "
                    + string.Join(",", unknown.Select(FactCatalog.HandleFor)));
                return 1;
            }
        }

        var snapshotTaken = false;
        bool SnapshotOnce()
        {
            if (snapshotTaken)
            {
                return true;
            }

            using var held = BackupStore.TryLock(home);
            if (held is null)
            {
                stderr.WriteLine("error: a backup is already running; nothing was changed. Try again when it finishes.");
                return false;
            }

            if (BackupCommand.TakeNow(home, BackupSettings.Read(ConfigFile.Load(home.ConfigPath)), connection, stdout, stderr, "pre-reorganize") is null)
            {
                return false;
            }

            snapshotTaken = true;
            return true;
        }

        var exit = 0;
        if (ledger)
        {
            exit = Ledger(connection, apply, room, SnapshotOnce, stdout);
        }

        if (suspects is not null && exit == 0)
        {
            exit = Captures(suspects.Value, connection, apply, tier is not null, onlyIds, room, SnapshotOnce, stdout);
        }

        return exit;
    }

    private static int Ledger(SqliteConnection connection, bool apply, int room, Func<bool> snapshot, TextWriter stdout)
    {
        var selected = ReadLiveUnder(connection, LedgerFrom);
        stdout.WriteLine($"ledger: {selected.Count} live fact(s) under {LedgerFrom} → {LedgerTo}, scope \"{FactStore.ToolScope}\"");

        foreach (var (id, path, body) in selected)
        {
            stdout.WriteLine(WithBody($"  {FactCatalog.HandleFor(id)}  {path} → {Target(path)}", body, room));
        }

        if (!apply)
        {
            // The same decision the apply makes, read without writing (FactStore.PlanRefile).
            var planned = selected
                .Select(s => (s.Id, Result: FactStore.PlanRefile(connection, null, s.Id, Target(s.Path))))
                .ToList();
            PrintCounts(planned, "would be: ", stdout);
            stdout.WriteLine("Dry run only — nothing was changed. Re-run with --apply to re-file them.");
            return 0;
        }

        if (selected.Count == 0)
        {
            PrintCounts([], string.Empty, stdout);
            return 0;
        }

        if (!snapshot())
        {
            return 1;
        }

        var results = new List<(long Id, RefileResult Result)>();
        var now = DateTimeOffset.UtcNow;
        var reason = $"re-filed: another tool's ledger, {LedgerFrom} → {LedgerTo}";
        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            foreach (var (id, path, _) in selected)
            {
                results.Add((id, FactStore.Refile(
                    connection, transaction, id, Target(path), FactStore.ToolScope, "observed", reason, now)));
            }

            transaction.Commit();
        }

        PrintCounts(results, string.Empty, stdout);
        return 0;
    }

    /// <summary>
    /// The four counts (D87 rulings 1 and 9), each printed apart, then every source left in place
    /// beside the older target that kept it there — those are the rows still in user scope.
    /// </summary>
    private static void PrintCounts(List<(long Id, RefileResult Result)> results, string prefix, TextWriter stdout)
    {
        int Count(RefileOutcome outcome) => results.Count(r => r.Result.Outcome == outcome);

        var line = $"{prefix}re-filed {Count(RefileOutcome.Refiled)}, "
            + $"already re-filed {Count(RefileOutcome.AlreadyAtTarget)}, "
            + $"superseded by newer target {Count(RefileOutcome.SupersededByNewerTarget)}, "
            + $"left in place {Count(RefileOutcome.TargetConflict)}";
        if (Count(RefileOutcome.SourceNotLive) > 0)
        {
            line += $", no longer live {Count(RefileOutcome.SourceNotLive)}";
        }

        stdout.WriteLine(line);
        foreach (var (id, result) in results.Where(r => r.Result.Outcome == RefileOutcome.TargetConflict))
        {
            stdout.WriteLine(
                $"  left in place: {FactCatalog.HandleFor(id)} (target {FactCatalog.HandleFor(result.TargetFactId!.Value)} is older)");
        }
    }

    private static (int LiveCount, List<(StoredFact Fact, List<string> Rules)> Suspects) ReadSuspects(SqliteConnection connection)
    {
        var live = CaptureRoots.SelectMany(root => FactStore.ReadSubtree(connection, root)).ToList();
        var inSessions = SessionBodies(connection, live.Select(f => f.Body).Distinct(StringComparer.Ordinal).ToList());
        var suspects = live
            .Select(f => (Fact: f, Rules: Rules(f.Body, inSessions)))
            .Where(s => s.Rules.Count > 0)
            .ToList();
        return (live.Count, suspects);
    }

    private static int Captures(
        (int LiveCount, List<(StoredFact Fact, List<string> Rules)> Suspects) plan,
        SqliteConnection connection,
        bool apply,
        bool tierA,
        HashSet<long> onlyIds,
        int room,
        Func<bool> snapshot,
        TextWriter stdout)
    {
        var suspects = plan.Suspects;
        var tierASet = suspects.Where(s => s.Rules.Any(r => r is "R1" or "R2" or "R5")).ToList();
        var tierBSet = suspects.Except(tierASet)
            .OrderBy(s => s.Rules[0], StringComparer.Ordinal)
            .ThenByDescending(s => s.Fact.Body.Length)
            .ToList();

        stdout.WriteLine($"captures: {plan.LiveCount} live under {string.Join(", ", CaptureRoots)}; {suspects.Count} suspect");
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

        var close = tierA ? tierASet.Select(s => s.Fact.Id).ToList() : onlyIds.Order().ToList();
        if (close.Count == 0)
        {
            stdout.WriteLine("closed 0");
            return 0;
        }

        if (!snapshot())
        {
            return 1;
        }

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

    private static string Target(string path) => LedgerTo + path[LedgerFrom.Length..];

    /// <summary>
    /// The handle and paths in full, then the body on the same line clipped to what is left —
    /// the address is what someone acts on, so it is never the part ellipsed.
    /// </summary>
    private static string WithBody(string head, string body, int room) =>
        head + "  " + Tui.Clip(body.ReplaceLineEndings(" "), Math.Max(room - head.Length - 2, 24));
}
