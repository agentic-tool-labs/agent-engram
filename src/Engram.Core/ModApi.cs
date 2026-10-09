using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

public sealed record ModApiResult(int Status, string Json);

/// <summary>
/// The operations behind <c>POST /mod/v1/&lt;op&gt;</c>. Everything past the HTTP envelope lives here
/// so it is testable against a real store without a server; the route layer only enforces what is
/// about HTTP itself (method, content type, header, size).
/// </summary>
/// <remarks>
/// Mods are not the model, and what they do must not read as the model using memory: these
/// operations never touch the MCP session machinery, never write <c>recall</c>, <c>remember</c> or
/// <c>session-open</c> records, and record themselves as <c>mod-call</c> under Claude Code's session
/// id. Lookups write nothing at all — <c>captures</c> runs once per prompt and <c>path-facts</c> once
/// per edit, and logging them would change what the telemetry log is.
/// </remarks>
public static class ModApi
{
    public const int MaxBodyBytes = 64 * 1024;

    private const int MaxQueryChars = 2000;
    private const int MaxEvidenceChars = 300;
    private const int MaxSessionIdChars = 128;
    private const int MaxModChars = 32;
    private const int MaxCaptures = 20;
    private const int MaxPathFacts = 50;
    private const string ForgetReason = "retracted by the user";
    private const int MaxEventEpochChars = 64;
    private const int DefaultTailLimit = 20;
    private const int MaxTailLimit = 50;

    /// <summary>
    /// How far before the caller's retraction cursor a read reaches back. A writer stamps its
    /// time before it takes the write lock and may then wait up to the 5 s <c>busy_timeout</c>, so a
    /// retraction can commit after one stamped later was already read; ten seconds covers that wait
    /// and the rounding to whole seconds.
    /// </summary>
    internal const int RetractionSlackSeconds = 10;

    private static readonly string[] Operations =
        ["recall", "fact", "history", "forget", "remember", "captures", "path-facts", "tail"];

    public static bool IsKnownOp(string op) => Array.IndexOf(Operations, op) >= 0;

    /// <summary>Whether a value may name a mod: lowercase letters, digits and hyphens, 1–32 characters.</summary>
    public static bool IsModName(string? value) =>
        value is { Length: >= 1 and <= MaxModChars } && value.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    public static ModApiResult Error(int status, string code, string? detail = null) =>
        new(status, JsonSerializer.Serialize(new ModError(code, detail), ModApiJsonContext.Default.ModError));

    /// <param name="feed">
    /// The server's telemetry feed. Without one the <c>tail</c> op reports its activity part as
    /// null, which is what a caller that has no server behind it should see.
    /// </param>
    public static ModApiResult Execute(
        EngramHome home,
        LocalRuntime local,
        string op,
        string headerMod,
        ReadOnlySpan<byte> body,
        TelemetryFeed? feed = null)
    {
        if (!IsKnownOp(op))
        {
            return Error(404, "not_found", $"unknown operation '{op}'");
        }

        ModRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(body, ModApiJsonContext.Default.ModRequest);
        }
        catch (JsonException)
        {
            return Error(400, "bad_request", "body is not a JSON object of the expected shape");
        }

        if (request is null)
        {
            return Error(400, "bad_request", "body is not a JSON object");
        }

        if (!string.Equals(request.Mod, headerMod, StringComparison.Ordinal))
        {
            return Error(400, "bad_request", "mod does not match the X-Engram-Mod header");
        }

        if (!File.Exists(home.ConfigPath))
        {
            return Error(503, "not_initialised", "Engram home is not initialised (run 'engram init')");
        }

        try
        {
            return op switch
            {
                "recall" => Recall(home, local, request),
                "fact" => Fact(home, request),
                "history" => History(home, request),
                "forget" => Forget(home, request),
                "remember" => Remember(home, request),
                "captures" => Captures(home, request),
                "tail" => Tail(home, request, feed),
                _ => PathFacts(home, request),
            };
        }
        catch (ModApiException e)
        {
            return Error(e.Status, e.Code, e.Message);
        }
        catch (Exception)
        {
            // Anything else would reach ASP.NET as a bare 500 with no JSON body and an error-level
            // log line carrying the exception text, which can include request data.
            return Error(500, "internal", "the operation failed");
        }
    }

    private static ModApiResult Recall(EngramHome home, LocalRuntime local, ModRequest request)
    {
        var sessionId = RequireSession(request);
        var query = request.Query ?? string.Empty;
        if (query.Trim().Length is 0 or > MaxQueryChars)
        {
            throw Bad($"query must be 1 to {MaxQueryChars} characters");
        }

        if (request.BudgetTokens is { } requested && (requested < 50 || requested > 4000))
        {
            throw Bad("budget_tokens must be between 50 and 4000");
        }

        if (request.Mode is not null && request.Mode is not ("shadow" or "inject"))
        {
            throw Bad("mode must be 'shadow' or 'inject'");
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var detail = RecallSearch.Run(home, local, query, request.BudgetTokens, sessionId, pinnedFactIds: null);
        var result = detail.Result;
        var durationMs = Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, 2);

        var coverage = RecallEngine.ToText(result.Coverage);
        RecordCall(home, request, sessionId, "recall", query, coverage, durationMs);

        var response = new ModRecallResponse(
            coverage, result.FactCount, [.. detail.Notes], detail.Gaps, result.Text,
            [.. detail.Packed.Select(ToRecallFact)]);
        return Ok(JsonSerializer.Serialize(response, ModApiJsonContext.Default.ModRecallResponse));
    }

    /// <summary>A packed candidate as the API reports it, from the values its line was built from.</summary>
    internal static ModRecallFact ToRecallFact(RecallCandidate candidate)
    {
        var source = candidate.Source
            ?? throw new InvalidOperationException($"candidate {candidate.Handle} carries no source record");
        return new ModRecallFact(
            candidate.Handle,
            candidate.FactId ?? throw new InvalidOperationException($"candidate {candidate.Handle} has no fact id"),
            source.Body,
            source.Scope,
            source.Versions,
            source.WithheldChars,
            source.Location,
            new ModRecallLanes(candidate.LexicalRank, candidate.OverlapRank, candidate.VectorRank));
    }

    private static ModApiResult Fact(EngramHome home, ModRequest request)
    {
        var factId = RequireFact(request);
        using var connection = EngramDatabase.OpenInitialized(home);
        var fact = FactStore.ReadById(connection, factId) ?? throw NotFound(request.FactId!);
        var versions = FactStore.History(connection, fact.SubjectPath, fact.Predicate).Count;

        var response = new ModFactResponse(
            FactCatalog.HandleFor(fact.Id), fact.Id, fact.SubjectPath, fact.Predicate, fact.Body, fact.Details,
            fact.Scope, fact.LearnedVia, fact.Evidence, fact.ValidFrom, fact.ValidTo, fact.ValidTo is null, versions);
        return Ok(JsonSerializer.Serialize(response, ModApiJsonContext.Default.ModFactResponse));
    }

    private static ModApiResult History(EngramHome home, ModRequest request)
    {
        var factId = RequireFact(request);
        using var connection = EngramDatabase.OpenInitialized(home);
        var fact = FactStore.ReadById(connection, factId) ?? throw NotFound(request.FactId!);

        var chain = FactStore.History(connection, fact.SubjectPath, fact.Predicate);
        var reasons = MemoryBrowser.Reasons(connection, chain.Where(v => v.ValidTo is not null).Select(v => v.Id));
        var versions = chain
            .Select(v => new ModHistoryVersion(
                FactCatalog.HandleFor(v.Id), v.Body, v.ValidFrom, v.ValidTo, v.LearnedVia, reasons.GetValueOrDefault(v.Id)))
            .ToArray();

        var response = new ModHistoryResponse(fact.SubjectPath, fact.Predicate, versions);
        return Ok(JsonSerializer.Serialize(response, ModApiJsonContext.Default.ModHistoryResponse));
    }

    private static ModApiResult Forget(EngramHome home, ModRequest request)
    {
        var sessionId = RequireSession(request);
        var factId = RequireFact(request);

        using var connection = EngramDatabase.OpenInitialized(home);
        if (FactStore.ReadById(connection, factId) is null)
        {
            throw NotFound(request.FactId!);
        }

        // The session row is resolved inside the write transaction, as SessionFacts.Append does: in
        // autocommit two first forgets from one new session race the select-then-insert on
        // external_id, and a forget that closes nothing would still leave a session row behind.
        var now = DateTimeOffset.UtcNow;
        bool closed;
        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            var sessionRow = SessionStore.EnsureSession(connection, transaction, sessionId, now);
            closed = FactStore.Forget(connection, transaction, factId, ForgetReason, now, sessionRow);
            if (closed)
            {
                transaction.Commit();
            }
            else
            {
                transaction.Rollback();
            }
        }

        if (closed)
        {
            RecordCall(home, request, sessionId, "forget");
        }

        return Ok(JsonSerializer.Serialize(new ModForgetResponse(FactCatalog.HandleFor(factId), closed), ModApiJsonContext.Default.ModForgetResponse));
    }

    private static ModApiResult Remember(EngramHome home, ModRequest request)
    {
        var sessionId = RequireSession(request);
        var statement = request.Statement;
        if (string.IsNullOrWhiteSpace(statement))
        {
            throw Bad("statement is required");
        }

        if (request.Evidence is not { } evidence || evidence.Trim().Length is 0 or > MaxEvidenceChars)
        {
            throw Bad($"evidence is required, 1 to {MaxEvidenceChars} characters");
        }

        if (DetailsCeiling.Error(request.Details) is { } ceilingError)
        {
            throw Bad(ceilingError);
        }

        using var connection = EngramDatabase.OpenInitialized(home);
        using var transaction = EngramDatabase.BeginWrite(connection);
        var (factId, isRepeat) = SessionFacts.Append(
            connection, transaction, sessionId, statement, request.Subject, evidence, null, DateTimeOffset.UtcNow, request.Details);

        if (isRepeat)
        {
            transaction.Rollback();
        }
        else
        {
            transaction.Commit();
        }

        RecordCall(home, request, sessionId, "remember");
        return Ok(JsonSerializer.Serialize(new ModRememberResponse(FactCatalog.HandleFor(factId), factId, !isRepeat), ModApiJsonContext.Default.ModRememberResponse));
    }

    private static ModApiResult Captures(EngramHome home, ModRequest request)
    {
        var sessionId = RequireSession(request);
        if (request.Since is not { } since || since < 0)
        {
            throw Bad("since is required: unix seconds, 0 or later");
        }

        using var connection = EngramDatabase.OpenInitialized(home);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.id, f.body, f.created_at
            FROM fact f
            JOIN session s ON s.id = f.session_id
            WHERE s.external_id = $session
              AND f.valid_to IS NULL
              AND substr(f.path, 1, $prefixLen) = $prefix
              AND f.learned_via = $learnedVia
              AND f.created_at >= $since
            ORDER BY f.created_at, f.id
            LIMIT $limit;
            """;
        var prefix = UserFacts.Root + "/";
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$prefix", prefix);
        command.Parameters.AddWithValue("$prefixLen", prefix.Length);
        command.Parameters.AddWithValue("$learnedVia", UserFacts.LearnedVia);
        command.Parameters.AddWithValue("$since", since);
        command.Parameters.AddWithValue("$limit", MaxCaptures);

        var captures = new List<ModCapture>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            captures.Add(new ModCapture(FactCatalog.HandleFor(id), id, reader.GetString(1), reader.GetInt64(2)));
        }

        return Ok(JsonSerializer.Serialize(new ModCapturesResponse([.. captures]), ModApiJsonContext.Default.ModCapturesResponse));
    }

    private static ModApiResult PathFacts(EngramHome home, ModRequest request)
    {
        var path = request.Path;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)
            || path.Split('/', '\\').Contains(".."))
        {
            throw Bad("path must be absolute and contain no '..' segment");
        }

        using var connection = EngramDatabase.OpenInitialized(home);
        if (CodeEntityResolver.Resolve(connection, path) is not var (entityPath, repoPath))
        {
            return Ok(JsonSerializer.Serialize(new ModPathFactsResponse(null, null, []), ModApiJsonContext.Default.ModPathFactsResponse));
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT fact.id, fact.path, fact.predicate, fact.body, fact.scope, fact.regenerable, fact.valid_from
            FROM fact
            WHERE fact.valid_to IS NULL
              AND {LiveCodeFacts.UnderFilePredicate}
              AND ($predicate IS NULL OR fact.predicate = $predicate)
            ORDER BY fact.regenerable, fact.valid_from DESC, fact.id
            LIMIT $limit;
            """;
        LiveCodeFacts.BindFile(command, entityPath);
        command.Parameters.AddWithValue("$predicate", (object?)request.Predicate ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", MaxPathFacts);

        var facts = new List<ModPathFact>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            facts.Add(new ModPathFact(
                FactCatalog.HandleFor(id), id, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt64(5) != 0, reader.GetInt64(6)));
        }

        return Ok(JsonSerializer.Serialize(new ModPathFactsResponse(entityPath, repoPath, [.. facts]), ModApiJsonContext.Default.ModPathFactsResponse));
    }

    /// <summary>
    /// Where a fact came from, by first match. One function, so a write row and the retraction of
    /// the same fact can never disagree about it.
    /// </summary>
    internal static string OriginOf(string path, string predicate, string learnedVia, bool replaces)
    {
        if (path.StartsWith(DirectiveFacts.Root + "/", StringComparison.Ordinal))
        {
            return "directive";
        }

        if (predicate == InvariantFacts.Predicate)
        {
            return "invariant";
        }

        var sessionPrefix = SessionFacts.Root + "/";
        if (path.StartsWith(sessionPrefix, StringComparison.Ordinal))
        {
            // /sessions/<row>/<agent>/<fingerprint>: the agent segment only exists below a subagent.
            var segments = path[sessionPrefix.Length..].Split('/');
            if (segments.Length >= 3 && segments[1] == CompactionDigest.HarvesterAgent)
            {
                return "compaction";
            }
        }

        if (replaces)
        {
            return "revision";
        }

        if (path.StartsWith(UserFacts.Root + "/", StringComparison.Ordinal) && learnedVia == UserFacts.LearnedVia)
        {
            return "capture";
        }

        return path.StartsWith(sessionPrefix, StringComparison.Ordinal) ? "note" : "other";
    }

    /// <summary>
    /// Writes since a rowid cursor. A rowid seek, with the earlier version of a thread resolved
    /// through <c>ix_supersession_new</c> — filtering <c>fact.superseded_by</c> would scan the corpus.
    /// </summary>
    internal const string TailWritesSql =
        """
        SELECT f.id, f.created_at, f.path, f.predicate, f.learned_via, f.body, f.evidence, f.valid_to,
               s.external_id,
               (SELECT x.old_fact_id FROM supersession x WHERE x.new_fact_id = f.id)
        FROM fact f
        LEFT JOIN session s ON s.id = f.session_id
        WHERE f.id > $after AND f.id <= $head AND f.regenerable = 0
        """;

    internal const string TailWritesCountSql =
        """
        SELECT COUNT(*)
        FROM fact f
        LEFT JOIN session s ON s.id = f.session_id
        WHERE f.id > $after AND f.id <= $head AND f.regenerable = 0
        """;

    /// <summary>
    /// Retractions in a window. Two constructs here are load-bearing, and each has a test that goes
    /// red without it. <c>INDEXED BY</c>: with <c>ANALYZE</c> run the planner still prefers
    /// <c>ix_supersession_new (new_fact_id=?)</c> for <c>IS NULL</c>, which reads every retraction
    /// ever made and sorts them; a store without the index therefore fails here rather than
    /// scanning. The unary plus in <see cref="TailSessionFilter"/>, appended to this statement for a
    /// session scope: without it the planner starts at <c>session.external_id</c> and walks the
    /// session's facts instead of seeking the retraction window.
    /// </summary>
    internal const string TailRetractionsSql =
        """
        SELECT x.old_fact_id, x.created_at, x.reason, f.path, f.predicate, f.learned_via, f.body,
               s.external_id,
               (SELECT y.old_fact_id FROM supersession y WHERE y.new_fact_id = x.old_fact_id)
        FROM supersession x INDEXED BY ix_supersession_retracted
        JOIN fact f ON f.id = x.old_fact_id
        LEFT JOIN session s ON s.id = x.session_id
        WHERE x.new_fact_id IS NULL AND x.created_at >= $since AND f.regenerable = 0
        """;

    internal const string TailWritesOrder = " ORDER BY f.id DESC LIMIT $limit;";

    internal const string TailRetractionsOrder = " ORDER BY x.created_at DESC, x.old_fact_id DESC LIMIT $limit;";

    /// <summary>
    /// The unary plus keeps the planner from starting at <c>session.external_id</c>, which would
    /// walk every fact of the session through <c>ix_fact_session</c> instead of seeking the rowid
    /// range; the session row is then found by primary key from each fact in range.
    /// </summary>
    internal const string TailSessionFilter = " AND +s.external_id = $session";

    private static ModApiResult Tail(EngramHome home, ModRequest request, TelemetryFeed? feed)
    {
        var sessionId = RequireSession(request);
        if (request.After is < 0)
        {
            throw Bad("after must be 0 or later");
        }

        if (request.ClosedAfter is < 0)
        {
            throw Bad("closed_after must be 0 or later");
        }

        if (request.EventAfter is < 0)
        {
            throw Bad("event_after must be 0 or later");
        }

        if (request.EventEpoch is { Length: > MaxEventEpochChars })
        {
            throw Bad($"event_epoch must be at most {MaxEventEpochChars} characters");
        }

        var onlyThisSession = request.Scope switch
        {
            null or "session" => true,
            "all" => false,
            _ => throw Bad("scope must be 'session' or 'all'"),
        };

        var limit = request.Limit ?? DefaultTailLimit;
        if (limit is < 1 or > MaxTailLimit)
        {
            throw Bad($"limit must be 1 to {MaxTailLimit}");
        }

        // Read before the store: telemetry is appended after the write it reports, so an event
        // returned here always has its write visible to the read below.
        ModTailEvents? events = null;
        if (feed is not null)
        {
            feed.Demand();
            var read = feed.Read(request.EventEpoch, request.EventAfter, limit, onlyThisSession ? sessionId : null);
            events = new ModTailEvents(
                read.Epoch, read.Head, [.. read.Rows.Select(e => new ModTailEvent(e.Seq, e.Record))], read.Skipped);
        }

        using var connection = EngramDatabase.OpenInitialized(home);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var head = TailHead(connection);

        var writes = request.After is { } after
            ? ReadTailWrites(connection, sessionId, onlyThisSession, after, head, limit)
            : new ModTailWrites([], null);
        var retractions = request.ClosedAfter is { } closedAfter
            ? ReadTailRetractions(connection, sessionId, onlyThisSession, closedAfter, limit)
            : new ModTailRetractions([]);

        return Ok(JsonSerializer.Serialize(
            new ModTailResponse(head, now, writes, retractions, events), ModApiJsonContext.Default.ModTailResponse));
    }

    internal const string TailHeadSql = "SELECT COALESCE(MAX(id), 0) FROM fact;";

    private static long TailHead(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = TailHeadSql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static ModTailWrites ReadTailWrites(
        SqliteConnection connection, string sessionId, bool onlyThisSession, long after, long head, int limit)
    {
        var scope = onlyThisSession ? TailSessionFilter : string.Empty;

        using var command = connection.CreateCommand();
        command.CommandText = TailWritesSql + scope + TailWritesOrder;
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$head", head);
        command.Parameters.AddWithValue("$limit", limit);
        if (onlyThisSession)
        {
            command.Parameters.AddWithValue("$session", sessionId);
        }

        var rows = new List<ModTailWrite>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var replaces = reader.IsDBNull(9) ? (long?)null : reader.GetInt64(9);
                rows.Add(new ModTailWrite(
                    FactCatalog.HandleFor(id),
                    id,
                    reader.GetInt64(1),
                    OriginOf(reader.GetString(2), reader.GetString(3), reader.GetString(4), replaces is not null),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    replaces is { } earlier ? FactCatalog.HandleFor(earlier) : null,
                    reader.IsDBNull(7),
                    !reader.IsDBNull(8) && reader.GetString(8) == sessionId));
            }
        }

        using var count = connection.CreateCommand();
        count.CommandText = TailWritesCountSql + scope + ";";
        count.Parameters.AddWithValue("$after", after);
        count.Parameters.AddWithValue("$head", head);
        if (onlyThisSession)
        {
            count.Parameters.AddWithValue("$session", sessionId);
        }

        return new ModTailWrites([.. rows], Convert.ToInt64(count.ExecuteScalar()) - rows.Count);
    }

    private static ModTailRetractions ReadTailRetractions(
        SqliteConnection connection, string sessionId, bool onlyThisSession, long closedAfter, int limit)
    {
        var scope = onlyThisSession ? TailSessionFilter : string.Empty;

        using var command = connection.CreateCommand();
        command.CommandText = TailRetractionsSql + scope + TailRetractionsOrder;
        command.Parameters.AddWithValue("$since", Math.Max(0, closedAfter - RetractionSlackSeconds));
        command.Parameters.AddWithValue("$limit", limit);
        if (onlyThisSession)
        {
            command.Parameters.AddWithValue("$session", sessionId);
        }

        var rows = new List<ModTailRetraction>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            rows.Add(new ModTailRetraction(
                FactCatalog.HandleFor(id),
                id,
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(6),
                OriginOf(reader.GetString(3), reader.GetString(4), reader.GetString(5), !reader.IsDBNull(8)),
                !reader.IsDBNull(7) && reader.GetString(7) == sessionId));
        }

        return new ModTailRetractions([.. rows]);
    }

    private static void RecordCall(
        EngramHome home, ModRequest request, string sessionId, string tool, string? query = null, string? coverage = null,
        double? durationMs = null) =>
        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: sessionId,
            Kind: TelemetryEventKind.ModCall,
            Query: query,
            Coverage: coverage,
            Tool: tool,
            Mod: request.Mod,
            Mode: request.Mode,
            DurationMs: durationMs));

    private static string RequireSession(ModRequest request)
    {
        var id = request.SessionId;
        if (id is not { Length: >= 1 and <= MaxSessionIdChars }
            || !id.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-'))
        {
            throw Bad($"session_id is required: 1 to {MaxSessionIdChars} characters of letters, digits, '_' and '-'");
        }

        return id;
    }

    private static long RequireFact(ModRequest request) =>
        request.FactId is { } handle && FactCatalog.TryParseHandle(handle, out var id)
            ? id
            : throw Bad("fact_id is required and looks like 'f42'");

    private static ModApiResult Ok(string json) => new(200, json);

    private static ModApiException Bad(string detail) => new(400, "bad_request", detail);

    private static ModApiException NotFound(string handle) => new(404, "not_found", $"no fact '{handle}'");

    private sealed class ModApiException(int status, string code, string detail) : Exception(detail)
    {
        public int Status { get; } = status;

        public string Code { get; } = code;
    }
}
