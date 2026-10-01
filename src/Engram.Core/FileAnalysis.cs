namespace Engram.Core;

/// <summary>A 1-based, inclusive line range into a file's decoded content.</summary>
/// <remarks>
/// Computed on demand and never written to the store: a stored line number turns every edit above a
/// symbol into a new fact version.
/// </remarks>
public readonly record struct LineSpan(int Start, int End)
{
    /// <summary>
    /// Lines split on <c>'\n'</c>, so a CRLF file numbers like its LF twin; a trailing newline does not
    /// add an empty last line. Never empty.
    /// </summary>
    internal static IReadOnlyList<string> Lines(string content)
    {
        var lines = content.Split('\n');
        return lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    internal static int[] NewlineOffsets(string content)
    {
        var offsets = new List<int>();
        for (var i = content.IndexOf('\n'); i >= 0; i = content.IndexOf('\n', i + 1))
        {
            offsets.Add(i);
        }

        return [.. offsets];
    }

    /// <summary>The 1-based line holding <paramref name="index"/>, given every newline's offset.</summary>
    internal static int LineNumber(int[] newlines, int index)
    {
        var at = Array.BinarySearch(newlines, index);
        return (at < 0 ? ~at : at) + 1;
    }

    /// <summary>The range with trailing blank lines dropped, so a span ends on code.</summary>
    internal static LineSpan Trimmed(IReadOnlyList<string> lines, int start, int end)
    {
        end = Math.Min(end, lines.Count);
        while (end > start && string.IsNullOrWhiteSpace(lines[end - 1]))
        {
            end--;
        }

        return new LineSpan(start, end);
    }
}

/// <summary>What analysing one file yields: the facts the indexer would write, and where each entity sits.</summary>
public sealed record FileAnalysis(IReadOnlyList<CodeCandidate> Candidates, IReadOnlyDictionary<string, LineSpan> Spans)
{
    /// <summary>
    /// The one composition of tier 0 and the optional deeper tier. The indexer and the source reader
    /// both call it, so a fact is attributed to the same lines by the same rule that named it.
    /// </summary>
    public static FileAnalysis Analyze(
        string fileEntityPath,
        string content,
        LanguageDefinition language,
        DeepAnalysis? deep)
    {
        var spans = new Dictionary<string, LineSpan>(StringComparer.Ordinal);
        var candidates = CodeAnalyzer.Analyze(fileEntityPath, content, language, spans);
        if (deep is not null)
        {
            candidates = DeepTier.Merge(fileEntityPath, candidates, deep);
        }

        // A span is only meaningful for an entity the analysis still produces.
        var produced = candidates.Select(c => c.EntityPath).ToHashSet(StringComparer.Ordinal);
        foreach (var path in spans.Keys.Where(p => p != fileEntityPath && !produced.Contains(p)).ToList())
        {
            spans.Remove(path);
        }

        return new FileAnalysis(candidates, spans);
    }
}
