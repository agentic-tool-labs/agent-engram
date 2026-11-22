using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>Where a scoped remember landed, and why when it did not land where it was asked to.</summary>
public sealed record ScopedWrite(long FactId, bool IsRepeat, ProjectResolution? Resolution, RoleWrite? RoleOutcome)
{
    /// <summary>The scope actually written when it is not <c>session</c>; null for a session note.</summary>
    public string? WrittenScope => Resolution?.Project is not null
        ? MemoryScopes.Project
        : RoleOutcome is RoleWrite.Written or RoleWrite.Repeat ? MemoryScopes.Agent : null;
}

/// <summary>
/// The one routing of a remember by <c>scope</c> — session, project (D98) or agent (D100) — shared
/// by <c>engram_remember</c> and <c>/mod/v1/remember</c> (D105). Every failure keeps the statement
/// as a session note; <see cref="Notices"/> says why, in the same words for both callers.
/// </summary>
public static class ScopedRemember
{
    /// <summary>
    /// Resolves a project scope's directory. Call it before taking the write lock: it can run git,
    /// and nothing else should wait on that (D98).
    /// </summary>
    public static ProjectResolution? Resolve(SqliteConnection connection, string? scope, string? projectDir) =>
        scope == MemoryScopes.Project ? ProjectNotes.Resolve(connection, projectDir) : null;

    public static ScopedWrite Append(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionExternalId,
        string? scope,
        ProjectResolution? resolution,
        string? agent,
        string statement,
        string? subject,
        string? evidence,
        DateTimeOffset now,
        string? details = null,
        bool sync = false)
    {
        if (scope == MemoryScopes.Agent && RoleNotes.IsAddressable(agent))
        {
            var (roleFactId, outcome) = RoleNotes.Append(
                connection, transaction, sessionExternalId, agent!, statement, subject, evidence, now, details, sync);
            return new ScopedWrite(roleFactId, outcome == RoleWrite.Repeat, resolution, outcome);
        }

        var (factId, isRepeat) = resolution?.Project is { } project
            ? ProjectNotes.Append(
                connection, transaction, sessionExternalId, project, statement, subject, evidence, now, details, sync)
            : SessionFacts.Append(
                connection, transaction, sessionExternalId, statement, subject, evidence, agent, now, details, sync);
        return new ScopedWrite(factId, isRepeat, resolution, RoleOutcome: null);
    }

    /// <summary>Why a scoped write was kept as a session note, one line per reason; empty when it landed.</summary>
    public static IReadOnlyList<string> Notices(ScopedWrite write, string? scope, string? projectDir, string? agent)
    {
        var notices = new List<string>();
        if (write.Resolution is { Reason: not null } resolution)
        {
            notices.Add(ProjectNotes.Notice(resolution, projectDir));
        }

        if (scope == MemoryScopes.Agent && write.RoleOutcome is null)
        {
            notices.Add("no usable agent key — kept as a session note; pass your role key from the primer as agent");
        }
        else if (write.RoleOutcome == RoleWrite.Capped)
        {
            notices.Add($"role memory is for lessons every future {agent} needs; kept as a session note");
        }

        return notices;
    }
}
