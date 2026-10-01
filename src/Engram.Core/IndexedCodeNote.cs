using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// What <c>engram_expand</c> says about a fact the code indexer wrote. Such a fact is a ~60-token
/// gist of one symbol, file or section, so expanding it has nothing further to reveal — the useful
/// answer is where the real thing lives and how to get its structure.
/// </summary>
public static class IndexedCodeNote
{
    /// <summary>
    /// Only an indexer-written fact is a gist. A <c>code</c>-scope fact recorded through revise is
    /// authored truth (<c>Regenerable</c> false) and must not be described as one.
    /// </summary>
    private static bool IsIndexed(StoredFact fact) => fact is { Scope: "code", Regenerable: true };

    public static string? LocationOf(StoredFact fact) =>
        IsIndexed(fact) ? CodePaths.LocationText(fact.SubjectPath) : null;

    /// <summary>Null unless <paramref name="fact"/> is an indexed code fact with a location.</summary>
    public static string? Build(SqliteConnection connection, StoredFact fact)
    {
        if (!IsIndexed(fact) || CodePaths.LocationOf(fact.SubjectPath) is not var (repo, relative))
        {
            return null;
        }

        var note = new System.Text.StringBuilder();
        note.Append("Indexed code: ").Append(CodePaths.LocationText(repo, relative))
            .Append(" (").Append(fact.SubjectPath).Append(")\n");

        var freshness = FileFreshness.Check(connection, fact.SubjectPath);
        if (FreshnessLine(freshness) is { } line)
        {
            note.Append(line).Append('\n');
        }

        // Point at the details view only when it can read something: sending the caller to a call
        // that can only fail is the same mistake as telling it to read a file that is gone.
        note.Append(freshness.File is not null && freshness.State != FileFreshness.State.Missing
            ? "Engram keeps only this indexed gist (~60 tokens); the details view reads the current source."
            : "Engram keeps only this indexed gist (~60 tokens), not the source — "
                + "there is nothing more to expand.");

        if (StructureHint(connection, fact, repo, relative) is { } hint)
        {
            note.Append('\n').Append(hint);
        }

        return note.ToString();
    }

    /// <summary>
    /// What the <c>details</c> view appends to an indexed code fact: its location, the structure hint,
    /// and the current source of what the fact describes, read from disk now. Null for any other fact.
    /// </summary>
    /// <remarks>
    /// The source is located by re-running the analysis that named the fact, so the lines are
    /// attributed by the same rule. When that cannot place the entity the whole file is shown with the
    /// reason in the header, never a guess at a range.
    /// </remarks>
    public static string? BuildDetails(
        SqliteConnection connection,
        EngramHome home,
        StoredFact fact,
        Func<string, string?> environment)
    {
        if (!IsIndexed(fact)
            || CodePaths.LocationOf(fact.SubjectPath) is not var (repo, relative)
            || CodePaths.SplitRepoPath(fact.SubjectPath) is not var (repoPath, relativePath))
        {
            return null;
        }

        var note = new System.Text.StringBuilder();
        note.Append("Indexed code: ").Append(CodePaths.LocationText(repo, relative))
            .Append(" (").Append(fact.SubjectPath).Append(")\n");
        if (StructureHint(connection, fact, repo, relative) is { } hint)
        {
            note.Append(hint).Append('\n');
        }

        var freshness = FileFreshness.Check(connection, fact.SubjectPath);
        var maxFileBytes = IndexingSettings.Read(ConfigFile.Load(home.ConfigPath)).MaxFileBytes;
        var read = CodeSourceReader.Read(freshness, fact.SubjectPath, maxFileBytes);

        if (read.Content is not { } content)
        {
            if (FreshnessLine(freshness) is { } line)
            {
                note.Append(line).Append('\n');
            }

            return note.Append("Source unavailable: ").Append(read.Reason).Append('.').ToString();
        }

        var language = LanguageRegistry.Resolve(relativePath);
        var fileEntity = CodePaths.ForFile(repoPath, relativePath);
        var deep = DeepAnalysisFor(home, environment, language, relativePath, content);
        var analysis = FileAnalysis.Analyze(fileEntity, content, language, deep);
        var lines = LineSpan.Lines(content);

        string? fallback = null;
        if (!analysis.Spans.TryGetValue(fact.SubjectPath, out var span))
        {
            span = new LineSpan(1, lines.Count);

            // At a tier above 0 an entity the deeper pass produced without a range, or a pass that could
            // not run at all, is the analyzer's limit; one the pass ran and did not find is gone.
            var produced = analysis.Candidates.Any(c => c.EntityPath == fact.SubjectPath);
            fallback = language.Tier >= 1 && (deep is not { Error: null } || produced)
                ? "analyzer unavailable"
                : "not in the current file";
        }

        note.Append("Source: ").Append(freshness.File)
            .Append(" · lines ").Append(span.Start).Append('–').Append(span.End).Append(" of ").Append(lines.Count);
        if (freshness.State == FileFreshness.State.Stale)
        {
            note.Append(" · changed since indexed");
        }

        if (fallback is not null)
        {
            note.Append(" · ").Append(fallback).Append(", whole file");
        }

        for (var i = span.Start - 1; i < span.End; i++)
        {
            note.Append('\n').Append(lines[i].TrimEnd('\r'));
        }

        return note.ToString();
    }

    /// <summary>
    /// A file that is gone is reported as where it was: "read it" would send the caller to a tool
    /// call that can only fail.
    /// </summary>
    private static string? FreshnessLine(FileFreshness.Verdict freshness) =>
        freshness.File is null
            ? null
            : freshness.State == FileFreshness.State.Missing
                ? $"Was at {freshness.File} ({freshness.Label})."
                : $"Read it at {freshness.File}{(freshness.IsWorthReporting ? $" ({freshness.Label})" : string.Empty)}.";

    // The entity's own kind decides: '#' introduces symbols and markdown sections alike.
    private static string? StructureHint(SqliteConnection connection, StoredFact fact, string repo, string relative) =>
        KindOf(connection, fact.SubjectId) switch
        {
            "symbol" => $"Structure: engram_navigate \"{fact.SubjectName}\" defined_at | members | callers, repo \"{repo}\".",
            "file" => $"Structure: engram_navigate \"{relative}\" imports, repo \"{repo}\".",
            _ => null,
        };

    /// <summary>
    /// The deeper tier's view of one file, from the same toolchain, located the same way, as the
    /// indexer's own run. Null means it did not run or did not answer, and the caller says so.
    /// </summary>
    private static DeepAnalysis? DeepAnalysisFor(
        EngramHome home,
        Func<string, string?> environment,
        LanguageDefinition language,
        string relativePath,
        string content)
    {
        switch (language.Tier)
        {
            case 1:
                if (TreeSitter.Locate(environment, home) is not { } directory)
                {
                    return null;
                }

                using (var runtime = TreeSitter.TryCreate(directory, []))
                {
                    return runtime?.Analyze(language, relativePath, content);
                }

            case 2:
                if (RoslynSidecar.Locate(environment) is not { } sidecar)
                {
                    return null;
                }

                var results = RoslynSidecar.Analyze(sidecar, [(relativePath, content)], SidecarTimeout);
                return results is not null && results.TryGetValue(relativePath, out var analysis) ? analysis : null;

            default:
                return null;
        }
    }

    // A source read is one interactive call, so a hung sidecar is cut off rather than waited on.
    private static readonly TimeSpan SidecarTimeout = TimeSpan.FromSeconds(5);

    private static string? KindOf(SqliteConnection connection, long entityId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind FROM entity WHERE id = $id;";
        command.Parameters.AddWithValue("$id", entityId);
        return command.ExecuteScalar() as string;
    }
}
