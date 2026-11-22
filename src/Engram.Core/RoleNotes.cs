using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>A role fact the primer can deliver: its id and its body (D100, D106).</summary>
public sealed record RoleFact(long Id, string Body);

/// <summary>How an agent-scope write landed (D100).</summary>
public enum RoleWrite
{
    Written,
    Repeat,
    Capped,
}

/// <summary>
/// Role memory: lessons every future agent of one role needs, written only when the model passes
/// <c>scope:"agent"</c> with its role key, and delivered in that role's primer (D100).
/// </summary>
/// <remarks>
/// Keyed on the hook payload's <c>agent_type</c>, never an instance name, which would fragment
/// memory per spawn. The server cannot attribute an MCP call to a subagent (D43), so the role is
/// whatever the caller passes; the primer tells it the key. <c>/agents/&lt;role&gt;</c> is shared by
/// every session the role runs in, so the leaf is the 8-byte fingerprint (D98).
/// </remarks>
public static class RoleNotes
{
    public const string Root = "/agents";

    /// <summary>
    /// The role key of an ordinary main session, whose payload names no <c>agent_type</c> (D104). An
    /// agent whose <c>agent_type</c> slugs to <c>main</c> shares this memory.
    /// </summary>
    public const string MainRole = "main";

    /// <summary>Agent-scope writes one session may make per role before the rest stay session notes.</summary>
    public const int MaxWritesPerSession = 3;

    /// <summary>Role facts the primer delivers, newest first.</summary>
    public const int MaxDelivered = 5;

    public static string RolePath(string role) => Root + "/" + CannedFactSeeder.Slug(role);

    public static string PathFor(string role, string statement) =>
        RolePath(role) + "/" + FactStore.Fingerprint(statement, ProjectNotes.FingerprintBytes);

    /// <summary>Whether a role key yields an address at all: a key with no ASCII letter or digit slugs to nothing.</summary>
    public static bool IsAddressable(string? role) =>
        !string.IsNullOrWhiteSpace(role) && CannedFactSeeder.Slug(role).Length > 0;

    /// <summary>
    /// Writes a role note, or the existing handle for a live identical statement, or — once this
    /// session has made <see cref="MaxWritesPerSession"/> agent-scope writes for the role — a
    /// session note under <c>/sessions/&lt;id&gt;/&lt;role&gt;/</c>. Never refuses.
    /// </summary>
    public static (long FactId, RoleWrite Outcome) Append(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionExternalId,
        string role,
        string statement,
        string? subject,
        string? evidence,
        DateTimeOffset now,
        string? details = null,
        bool sync = false)
    {
        var sessionId = SessionStore.EnsureSession(connection, transaction, sessionExternalId, now);
        var path = PathFor(role, statement);
        var predicate = ProjectNotes.PredicateFor(statement);

        // A repeat comes first: re-recording a lesson already held writes nothing, so it spends none
        // of the cap and is not demoted to a session note.
        if (FactStore.FindLiveFactId(connection, transaction, path, predicate) is { } existing)
        {
            return (existing, RoleWrite.Repeat);
        }

        if (CountWrittenBySession(connection, transaction, role, sessionId) >= MaxWritesPerSession)
        {
            var (noteId, _) = SessionFacts.Append(
                connection, transaction, sessionExternalId, statement, subject, evidence, role, now, details, sync);
            return (noteId, RoleWrite.Capped);
        }

        var (factId, isRepeat) = SessionFacts.AppendAt(
            connection,
            transaction,
            sessionId,
            path,
            predicate,
            MemoryScopes.Agent,
            statement,
            subject,
            evidence,
            now,
            details,
            sync,
            reason: "re-recorded for the role",
            ensureParents: createdAt => FactStore.EnsureEntity(
                connection, transaction, RolePath(role), SessionFacts.AgentKind, createdAt, displayName: role));

        return (factId, isRepeat ? RoleWrite.Repeat : RoleWrite.Written);
    }

    /// <summary>Live agent-scope facts this session wrote under the role, counted from the store.</summary>
    internal static long CountWrittenBySession(
        SqliteConnection connection, SqliteTransaction? transaction, string role, long sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*)
              FROM entity e
              JOIN fact f ON f.subject_id = e.id
             WHERE e.path >= $lo AND e.path < $hi
               AND f.valid_to IS NULL AND f.scope = $scope AND f.session_id = $session;
            """;
        BindRange(command, role);
        command.Parameters.AddWithValue("$scope", MemoryScopes.Agent);
        command.Parameters.AddWithValue("$session", sessionId);
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// Test seam: when a flow sets this, every <see cref="ReadDelivered"/> on that flow counts
    /// itself, so a test can assert a primer without a role made no extra read.
    /// </summary>
    internal static readonly AsyncLocal<StrongBox<int>?> ReadCounter = new();

    /// <summary>
    /// Up to <see cref="MaxDelivered"/> live role facts, newest first. Newest is the highest fact
    /// id, the one ordering the in-memory path (<see cref="PrimerSummary.From"/>) can also apply.
    /// </summary>
    public static IReadOnlyList<RoleFact> ReadDelivered(SqliteConnection connection, string role)
    {
        if (ReadCounter.Value is { } counter)
        {
            counter.Value++;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.id, f.body
              FROM entity e
              JOIN fact f ON f.subject_id = e.id
             WHERE e.path >= $lo AND e.path < $hi
               AND f.valid_to IS NULL AND f.scope = $scope
             ORDER BY f.id DESC
             LIMIT $limit;
            """;
        BindRange(command, role);
        command.Parameters.AddWithValue("$scope", MemoryScopes.Agent);
        command.Parameters.AddWithValue("$limit", MaxDelivered);

        var delivered = new List<RoleFact>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            delivered.Add(new RoleFact(reader.GetInt64(0), reader.GetString(1)));
        }

        return delivered;
    }

    /// <summary>The in-memory twin of <see cref="ReadDelivered"/> over a catalog already read.</summary>
    public static IReadOnlyList<RoleFact> SelectDelivered(IReadOnlyList<CannedFact> facts, string role)
    {
        var prefix = RolePath(role) + "/";
        return facts
            .Where(fact => fact.Scope == MemoryScopes.Agent
                && fact.SubjectPath is { } path
                && path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(fact => new RoleFact(FactCatalog.TryParseHandle(fact.Id, out var id) ? id : 0, fact.Body))
            .OrderByDescending(entry => entry.Id)
            .Take(MaxDelivered)
            .ToList();
    }

    // A path range rather than LIKE: it seeks entity's unique path index, and '0' is the
    // character after '/', so the range is exactly the subtree.
    private static void BindRange(SqliteCommand command, string role)
    {
        var rolePath = RolePath(role);
        command.Parameters.AddWithValue("$lo", rolePath + "/");
        command.Parameters.AddWithValue("$hi", rolePath + "0");
    }
}
