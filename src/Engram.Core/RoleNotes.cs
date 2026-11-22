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

    /// <summary>Proven lessons the primer delivers ahead of the newest (D106).</summary>
    public const int MaxProven = 3;

    /// <summary>
    /// Up to <see cref="MaxDelivered"/> live role facts in delivery order, chosen by
    /// <see cref="Choose"/> over each fact's recorded judgement counts in <c>salience</c>.
    /// </summary>
    public static IReadOnlyList<RoleFact> ReadDelivered(SqliteConnection connection, string role)
    {
        if (ReadCounter.Value is { } counter)
        {
            counter.Value++;
        }

        // Every live fact of the role, not a LIMIT: which five go out depends on the counts.
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.id, f.body, COALESCE(s.confirmations, 0), COALESCE(s.access_count, 0)
              FROM entity e
              JOIN fact f ON f.subject_id = e.id
              LEFT JOIN salience s ON s.fact_id = f.id
             WHERE e.path >= $lo AND e.path < $hi
               AND f.valid_to IS NULL AND f.scope = $scope;
            """;
        BindRange(command, role);
        command.Parameters.AddWithValue("$scope", MemoryScopes.Agent);

        var candidates = new List<RoleCandidate>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            candidates.Add(new RoleCandidate(
                new RoleFact(reader.GetInt64(0), reader.GetString(1)), reader.GetInt32(2), reader.GetInt32(3)));
        }

        return Choose(candidates);
    }

    /// <summary>The in-memory twin of <see cref="ReadDelivered"/> over a catalog already read.</summary>
    /// <param name="judgements">
    /// <c>salience</c>'s <c>(confirmations, access_count)</c> by fact id; a fact with no entry has
    /// zero of both, as it has no row.
    /// </param>
    public static IReadOnlyList<RoleFact> SelectDelivered(
        IReadOnlyList<CannedFact> facts,
        string role,
        IReadOnlyDictionary<long, (int Confirmations, int AccessCount)>? judgements = null)
    {
        var prefix = RolePath(role) + "/";
        var candidates = facts
            .Where(fact => fact.Scope == MemoryScopes.Agent
                && fact.SubjectPath is { } path
                && path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(fact =>
            {
                var id = FactCatalog.TryParseHandle(fact.Id, out var parsed) ? parsed : 0;
                var (confirmations, accessCount) = judgements?.GetValueOrDefault(id) ?? default;
                return new RoleCandidate(new RoleFact(id, fact.Body), confirmations, accessCount);
            })
            .ToList();
        return Choose(candidates);
    }

    /// <summary>
    /// Records a reflection model's unreviewed judgement of a delivered lesson (D106): one upsert
    /// into <c>salience</c>, guarded in the same statement on the fact being live and
    /// <c>scope='agent'</c>. <paramref name="held"/> adds a confirmation; either verdict adds an
    /// access and stamps <c>last_accessed</c>. <c>score</c> is never written. A judgement against
    /// the lesson also makes it due for review now, unless an earlier review date is already set.
    /// Touches no fact body, predicate, validity or supersession (D8).
    /// </summary>
    /// <returns>False, having written nothing, when the id is not a live role fact.</returns>
    public static bool RecordJudgement(
        SqliteConnection connection, SqliteTransaction transaction, long factId, bool held, long now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO salience (fact_id, access_count, last_accessed, confirmations)
            SELECT id, 1, $now, $confirm FROM fact WHERE id = $id AND valid_to IS NULL AND scope = $scope
              ON CONFLICT(fact_id) DO UPDATE SET
                access_count = access_count + 1,
                last_accessed = excluded.last_accessed,
                confirmations = confirmations + excluded.confirmations;
            """;
        command.Parameters.AddWithValue("$id", factId);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$confirm", held ? 1 : 0);
        command.Parameters.AddWithValue("$scope", MemoryScopes.Agent);
        if (command.ExecuteNonQuery() == 0)
        {
            return false;
        }

        if (!held)
        {
            FactReview.Set(connection, transaction, factId, now, now, keepEarlier: true);
        }

        return true;
    }

    /// <summary>A live role fact with its recorded judgement counts.</summary>
    public sealed record RoleCandidate(RoleFact Fact, int Confirmations, int AccessCount);

    /// <summary>
    /// The one delivery rule both summary paths use (D106). Set aside what was judged against at
    /// least twice and never for; take up to <see cref="MaxProven"/> proven lessons by
    /// confirmations; fill to <see cref="MaxDelivered"/> with the newest of the rest; only then
    /// with the set-aside ones, newest first. With no counts at all this is the five highest ids,
    /// which is what every store held before judgements existed.
    /// </summary>
    public static IReadOnlyList<RoleFact> Choose(IReadOnlyList<RoleCandidate> candidates)
    {
        bool SetAside(RoleCandidate c) => c.Confirmations == 0 && c.AccessCount >= 2;

        var proven = candidates
            .Where(c => !SetAside(c) && c.Confirmations >= 1)
            .OrderByDescending(c => c.Confirmations)
            .ThenByDescending(c => c.Fact.Id)
            .Take(MaxProven)
            .ToList();
        var newest = candidates
            .Where(c => !SetAside(c) && !proven.Contains(c))
            .OrderByDescending(c => c.Fact.Id)
            .Take(MaxDelivered - proven.Count)
            .ToList();
        var setAside = candidates
            .Where(SetAside)
            .OrderByDescending(c => c.Fact.Id)
            .Take(MaxDelivered - proven.Count - newest.Count);

        return proven.Concat(newest).Concat(setAside).Select(c => c.Fact).ToList();
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
