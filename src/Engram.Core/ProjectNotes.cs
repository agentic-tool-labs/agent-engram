using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>Why a <c>scope:"project"</c> write was kept as a session note (D98).</summary>
public enum ProjectUnresolved
{
    NoDir,
    BadDir,
    NotCheckout,
    GitUnknown,
    NotIndexed,
}

/// <summary>
/// Where a <c>project_dir</c> led: a project slug, or the reason there is none. <c>Root</c> is
/// the checkout root once one was found, for the notice.
/// </summary>
public sealed record ProjectResolution(string? Project, string? Root, ProjectUnresolved? Reason);

/// <summary>
/// Project notes: a durable, project-scoped write the model asks for with <c>scope:"project"</c>
/// (D98). Addressed <c>/projects/&lt;project&gt;/notes/&lt;fp64&gt;</c>, because that namespace
/// accumulates across every session and a 32-bit leaf would lose writes to collisions there.
/// </summary>
public static partial class ProjectNotes
{
    public const int FingerprintBytes = 8;

    public static string PathFor(string project, string statement) =>
        "/projects/" + project + "/notes/" + FactStore.Fingerprint(statement, FingerprintBytes);

    /// <summary>The closed predicate set, first match wins: <c>warns</c>, <c>decided</c>, else <c>holds</c>.</summary>
    public static string PredicateFor(string statement) =>
        WarnsCue().IsMatch(statement) ? "warns"
        : DecidedCue().IsMatch(statement) ? "decided"
        : "holds";

    [GeneratedRegex(@"\b(never|do not|don'?t|must not|trap|gotcha|breaks)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WarnsCue();

    [GeneratedRegex(@"\b(decided|chose|rejected)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DecidedCue();

    /// <summary>
    /// Resolves the call's own directory to a project through the resolvers indexing already
    /// uses. Enrollment is not consulted: a live registry row is what knows the project slug.
    /// </summary>
    /// <remarks>
    /// <see cref="ProjectUnresolved.GitUnknown"/> is not produced yet: <see cref="CodeIndexer.ResolveIdentity"/>
    /// falls back to the root path when git cannot answer, which then finds no row and reads as
    /// <see cref="ProjectUnresolved.NotIndexed"/>.
    /// </remarks>
    public static ProjectResolution Resolve(SqliteConnection connection, string? projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir))
        {
            return new(null, null, ProjectUnresolved.NoDir);
        }

        if (!Path.IsPathFullyQualified(projectDir) || !Directory.Exists(projectDir))
        {
            return new(null, null, ProjectUnresolved.BadDir);
        }

        if (RepoEnrollment.FindCheckoutRoot(projectDir) is null)
        {
            return new(null, null, ProjectUnresolved.NotCheckout);
        }

        var root = CodeIndexer.ResolveRoot(projectDir);
        var identity = CodeIndexer.ResolveIdentity(root);

        if (CodeIndexer.LiveRepoPath(connection, identity) is not { } repoPath)
        {
            return new(null, root, ProjectUnresolved.NotIndexed);
        }

        // /projects/<project>/code/<repo>
        var segments = repoPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2
            ? new(segments[1], root, null)
            : new(null, root, ProjectUnresolved.NotIndexed);
    }

    /// <summary>The telemetry value for a reason: a closed set of strings, never a count.</summary>
    public static string Code(ProjectUnresolved reason) => reason switch
    {
        ProjectUnresolved.NoDir => "no-dir",
        ProjectUnresolved.BadDir => "bad-dir",
        ProjectUnresolved.NotCheckout => "not-checkout",
        ProjectUnresolved.GitUnknown => "git-unknown",
        ProjectUnresolved.NotIndexed => "not-indexed",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    /// <summary>The one-line notice a degraded write carries.</summary>
    public static string Notice(ProjectResolution resolution, string? projectDir) => resolution.Reason switch
    {
        ProjectUnresolved.NoDir =>
            "no project_dir given — kept as a session note; pass your working directory to file it under the project",
        ProjectUnresolved.BadDir => "project_dir is not an existing absolute directory — kept as a session note",
        ProjectUnresolved.NotCheckout => $"{projectDir} is not in a git checkout — kept as a session note",
        ProjectUnresolved.GitUnknown => $"could not ask git about {resolution.Root} — kept as a session note",
        ProjectUnresolved.NotIndexed => $"{resolution.Root} is not indexed by Engram — kept as a session note",
        _ => throw new ArgumentOutOfRangeException(nameof(resolution)),
    };

    /// <summary>
    /// Writes a project note through <see cref="SessionFacts.AppendAt"/>, the one note-append
    /// routine. <c>session_id</c> is the writing session's row, exactly as for a session note.
    /// </summary>
    public static (long FactId, bool IsRepeat) Append(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionExternalId,
        string project,
        string statement,
        string? subject,
        string? evidence,
        DateTimeOffset now,
        string? details = null,
        bool sync = false)
    {
        var sessionId = SessionStore.EnsureSession(connection, transaction, sessionExternalId, now);

        return SessionFacts.AppendAt(
            connection,
            transaction,
            sessionId,
            PathFor(project, statement),
            PredicateFor(statement),
            MemoryScopes.Project,
            statement,
            subject,
            evidence,
            now,
            details,
            sync,
            reason: "re-recorded under the project");
    }
}
