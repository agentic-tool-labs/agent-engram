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

    private static readonly string[] Operations =
        ["recall", "fact", "history", "forget", "remember", "captures", "path-facts"];

    public static bool IsKnownOp(string op) => Array.IndexOf(Operations, op) >= 0;

    /// <summary>Whether a value may name a mod: lowercase letters, digits and hyphens, 1–32 characters.</summary>
    public static bool IsModName(string? value) =>
        value is { Length: >= 1 and <= MaxModChars } && value.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    public static ModApiResult Error(int status, string code, string? detail = null) =>
        new(status, JsonSerializer.Serialize(new ModError(code, detail), ModApiJsonContext.Default.ModError));

    public static ModApiResult Execute(
        EngramHome home, LocalRuntime local, string op, string headerMod, ReadOnlySpan<byte> body)
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

        var detail = RecallSearch.Run(home, local, query, request.BudgetTokens, sessionId, pinnedFactIds: null);
        var result = detail.Result;

        var coverage = RecallEngine.ToText(result.Coverage);
        RecordCall(home, request, sessionId, "recall", query, coverage);

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

        var closed = FactStore.Forget(connection, factId, ForgetReason, DateTimeOffset.UtcNow);
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

    private static void RecordCall(
        EngramHome home, ModRequest request, string sessionId, string tool, string? query = null, string? coverage = null) =>
        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: sessionId,
            Kind: TelemetryEventKind.ModCall,
            Query: query,
            Coverage: coverage,
            Tool: tool,
            Mod: request.Mod,
            Mode: request.Mode));

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
