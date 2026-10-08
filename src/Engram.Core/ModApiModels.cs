using System.Text.Json.Serialization;

namespace Engram.Core;

/// <summary>
/// One request body for every <c>/mod/v1/*</c> operation. A single optional-everything shape, because
/// which fields an operation needs is validated per operation and a field an operation ignores is not
/// an error.
/// </summary>
public sealed record ModRequest(
    [property: JsonPropertyName("mod")] string? Mod = null,
    [property: JsonPropertyName("session_id")] string? SessionId = null,
    [property: JsonPropertyName("query")] string? Query = null,
    [property: JsonPropertyName("budget_tokens")] int? BudgetTokens = null,
    [property: JsonPropertyName("mode")] string? Mode = null,
    [property: JsonPropertyName("fact_id")] string? FactId = null,
    [property: JsonPropertyName("statement")] string? Statement = null,
    [property: JsonPropertyName("details")] string? Details = null,
    [property: JsonPropertyName("subject")] string? Subject = null,
    [property: JsonPropertyName("evidence")] string? Evidence = null,
    [property: JsonPropertyName("since")] long? Since = null,
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("predicate")] string? Predicate = null);

public sealed record ModError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("detail")] string? Detail = null);

public sealed record ModRecallLanes(
    [property: JsonPropertyName("lexical")] int? Lexical,
    [property: JsonPropertyName("overlap")] int? Overlap,
    [property: JsonPropertyName("vector")] int? Vector);

public sealed record ModRecallFact(
    [property: JsonPropertyName("handle")] string Handle,
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("versions")] int Versions,
    [property: JsonPropertyName("withheld_chars")] int WithheldChars,
    [property: JsonPropertyName("location")] string? Location,
    [property: JsonPropertyName("lanes")] ModRecallLanes Lanes);

public sealed record ModRecallResponse(
    [property: JsonPropertyName("coverage")] string Coverage,
    [property: JsonPropertyName("fact_count")] int FactCount,
    [property: JsonPropertyName("notes")] string[] Notes,
    [property: JsonPropertyName("gaps")] string? Gaps,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("facts")] ModRecallFact[] Facts);

public sealed record ModFactResponse(
    [property: JsonPropertyName("handle")] string Handle,
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("predicate")] string Predicate,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("details")] string? Details,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("learned_via")] string LearnedVia,
    [property: JsonPropertyName("evidence")] string? Evidence,
    [property: JsonPropertyName("valid_from")] long ValidFrom,
    [property: JsonPropertyName("valid_to")] long? ValidTo,
    [property: JsonPropertyName("live")] bool Live,
    [property: JsonPropertyName("versions")] int Versions);

public sealed record ModHistoryVersion(
    [property: JsonPropertyName("handle")] string Handle,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("valid_from")] long ValidFrom,
    [property: JsonPropertyName("valid_to")] long? ValidTo,
    [property: JsonPropertyName("learned_via")] string LearnedVia,
    [property: JsonPropertyName("closed_reason")] string? ClosedReason);

public sealed record ModHistoryResponse(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("predicate")] string Predicate,
    [property: JsonPropertyName("versions")] ModHistoryVersion[] Versions);

public sealed record ModForgetResponse(
    [property: JsonPropertyName("handle")] string Handle,
    [property: JsonPropertyName("retracted")] bool Retracted);

public sealed record ModRememberResponse(
    [property: JsonPropertyName("handle")] string Handle,
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("created")] bool Created);

public sealed record ModCapture(
    [property: JsonPropertyName("handle")] string Handle,
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("created_at")] long CreatedAt);

public sealed record ModCapturesResponse(
    [property: JsonPropertyName("captures")] ModCapture[] Captures);

public sealed record ModPathFact(
    [property: JsonPropertyName("handle")] string Handle,
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("subject_path")] string SubjectPath,
    [property: JsonPropertyName("predicate")] string Predicate,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("regenerable")] bool Regenerable,
    [property: JsonPropertyName("valid_from")] long ValidFrom);

public sealed record ModPathFactsResponse(
    [property: JsonPropertyName("entity_path")] string? EntityPath,
    [property: JsonPropertyName("repo")] string? Repo,
    [property: JsonPropertyName("facts")] ModPathFact[] Facts);

[JsonSerializable(typeof(ModRequest))]
[JsonSerializable(typeof(ModError))]
[JsonSerializable(typeof(ModRecallResponse))]
[JsonSerializable(typeof(ModFactResponse))]
[JsonSerializable(typeof(ModHistoryResponse))]
[JsonSerializable(typeof(ModForgetResponse))]
[JsonSerializable(typeof(ModRememberResponse))]
[JsonSerializable(typeof(ModCapturesResponse))]
[JsonSerializable(typeof(ModPathFactsResponse))]
public sealed partial class ModApiJsonContext : JsonSerializerContext;
