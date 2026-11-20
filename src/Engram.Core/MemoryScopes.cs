using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// The one registry of memory types. A memory's type is its <c>fact.scope</c> column, which every
/// producer already writes (D85); every reader and validator takes the list from here rather than
/// spelling it again.
/// </summary>
public static class MemoryScopes
{
    public const string Session = "session";
    public const string User = "user";
    public const string Project = "project";
    public const string Agent = "agent";
    public const string Code = "code";
    public const string Tool = "tool";

    /// <summary>Every scope name recall's <c>types</c> filter accepts, in display order.</summary>
    public static IReadOnlyList<string> All { get; } = [Session, User, Project, Agent, Code, Tool];

    /// <summary>
    /// Scopes an unfiltered recall leaves out: another tool's ledger is not this project's memory.
    /// </summary>
    public static IReadOnlyList<string> ExcludedByDefault { get; } = [Tool];

    /// <summary>Scopes <c>engram_remember</c> accepts in <c>scope</c>, the default first (D98).</summary>
    public static IReadOnlyList<string> Writable { get; } = [Session, Project, Agent];

    /// <summary>
    /// The one implementation of the <c>types</c> predicate, over a fact aliased <c>f</c>. Every
    /// statement that honours a filter uses this text and <see cref="BindFilter"/>; a second
    /// hand-written <c>scope IN</c> is how explain's lanes would drift from recall's (D30).
    /// </summary>
    internal const string FilterSql = "f.scope IN (SELECT value FROM json_each($types))";

    /// <summary>
    /// The default exclusion, over a fact aliased <c>f</c>: matches what an unfiltered recall leaves
    /// out. Recall's <c>excluded</c> CTE is its one consumer (D90).
    /// </summary>
    internal static string ExcludedByDefaultSql { get; } =
        "f.scope IN (" + string.Join(", ", ExcludedByDefault.Select(scope => $"'{scope}'")) + ")";

    /// <summary>Binds <c>$types</c> for <see cref="FilterSql"/> from a normalized list.</summary>
    internal static void BindFilter(SqliteCommand command, IReadOnlyList<string> types)
    {
        var array = new JsonArray();
        foreach (var type in types)
        {
            ((IList<JsonNode?>)array).Add(JsonValue.Create(type));
        }

        command.Parameters.AddWithValue("$types", array.ToJsonString());
    }

    /// <summary>
    /// Validates a requested <c>types</c> filter. Null means no filter. An empty list or any name
    /// outside <see cref="All"/> is an error that names the valid values — never silently ignored,
    /// because an ignored filter reads as "nothing of that type exists". Duplicates collapse, and the
    /// result is in <see cref="All"/>'s order so one filter always prints one header.
    /// </summary>
    public static bool TryNormalize(
        IReadOnlyCollection<string>? requested, out IReadOnlyList<string>? types, out string? error)
    {
        types = null;
        error = null;
        if (requested is null)
        {
            return true;
        }

        var unknown = requested.Where(name => !All.Contains(name, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        if (requested.Count == 0 || unknown.Count > 0)
        {
            error = (requested.Count == 0 ? "types must name at least one kind of memory"
                    : unknown.Any(string.IsNullOrWhiteSpace) ? "empty type name"
                    : $"unknown types: {string.Join(", ", unknown)}")
                + $"; valid types are {string.Join(", ", All)}";
            return false;
        }

        types = All.Where(name => requested.Contains(name, StringComparer.Ordinal)).ToList();
        return true;
    }
}
