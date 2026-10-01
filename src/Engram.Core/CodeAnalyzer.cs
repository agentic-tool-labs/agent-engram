using System.Text;

namespace Engram.Core;

/// <summary>What one analyzed file wants the store to believe: a subject, and one fact about it.</summary>
/// <remarks>
/// Candidates carry no evidence, validity, or provenance — the pipeline supplies those
/// uniformly (observed, regenerable, evidence = path @ blob) so an analyzer cannot get
/// them wrong, and diffing candidates against live facts stays a comparison of
/// (path, predicate, body) triples.
/// </remarks>
public sealed record CodeCandidate(
    string EntityPath,
    string Kind,
    string DisplayName,
    string Predicate,
    string Body,
    string? Object = null,
    int AnalyzerTier = 0);

/// <summary>
/// Tier-0 analysis (D24): managed, in-core, no dependencies, works on any file. Produces
/// extractive candidates only — impressions of prose, declaration lines, import lists.
/// Deeper structure belongs to the tiers that can actually see it.
/// </summary>
public static class CodeAnalyzer
{
    // 3: imports became one object-bearing fact per module (code-navigation Phase 2).
    // 4: calls extraction + cross-file resolution (code-navigation Phase 3) — the bump is
    // what makes existing stores re-read under the better extractor.
    // 5: member emission widened to every visibility, both tiers (all-members spec) —
    // addressing is unchanged (GrammarVersion stays 2), only which members are observed.
    // 6: derives-from/inherits/implements and contains edges (§2/§8.5 of
    // close-graph-query-gap.md) — new predicates, no addressing change, so this bumps
    // AnalyzerVersion rather than GrammarVersion, following the precedent version 5 set.
    public const int AnalyzerVersion = 6;

    public static IReadOnlyList<CodeCandidate> Analyze(
        string fileEntityPath,
        string content,
        LanguageDefinition language) => Analyze(fileEntityPath, content, language, null);

    /// <summary>
    /// The same analysis, also filling <paramref name="spans"/> with the line range of each entity it
    /// finds. Spans live in a side map rather than on <see cref="CodeCandidate"/>, whose value
    /// equality decides what the indexer writes: a line number there would make every edit above a
    /// symbol look like a changed fact.
    /// </summary>
    internal static IReadOnlyList<CodeCandidate> Analyze(
        string fileEntityPath,
        string content,
        LanguageDefinition language,
        Dictionary<string, LineSpan>? spans)
    {
        var candidates = new List<CodeCandidate>();
        var fileName = fileEntityPath[(fileEntityPath.LastIndexOf('/') + 1)..];
        var lines = spans is null ? null : LineSpan.Lines(content);
        if (spans is not null)
        {
            spans[fileEntityPath] = new LineSpan(1, lines!.Count);
        }

        if (language.DocHeadings)
        {
            AnalyzeDocument(fileEntityPath, fileName, content, candidates, spans, lines);
            return candidates;
        }

        var impression = language.DeclarationPatterns.Count > 0 || language.ImportPatterns.Count > 0
            ? ImpressionExtractor.FromLeadComment(content)
            : ImpressionExtractor.FromProse(content);

        if (impression is not null)
        {
            candidates.Add(new CodeCandidate(fileEntityPath, "file", fileName, "about", impression));
        }

        AddDeclarations(fileEntityPath, content, language, candidates, spans, lines);
        AddImports(fileEntityPath, fileName, content, language, candidates);

        return candidates;
    }

    private static void AddDeclarations(
        string fileEntityPath,
        string content,
        LanguageDefinition language,
        List<CodeCandidate> candidates,
        Dictionary<string, LineSpan>? spans,
        IReadOnlyList<string>? lines)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var starts = spans is null ? null : new List<(string Path, int Start)>();
        var newlines = spans is null ? null : LineSpan.NewlineOffsets(content);

        foreach (var pattern in language.DeclarationPatterns)
        {
            foreach (System.Text.RegularExpressions.Match match in LanguageRegistry.Compiled(pattern).Matches(content))
            {
                var name = match.Groups["name"].Value;
                if (name.Length == 0 || !seen.Add(name))
                {
                    continue;
                }

                var line = LineOf(content, match.Index).Trim();
                var symbolPath = CodePaths.ForSymbol(fileEntityPath, name);
                candidates.Add(new CodeCandidate(
                    symbolPath,
                    "symbol",
                    name,
                    "declared-as",
                    Cap(line)));
                starts?.Add((symbolPath, LineSpan.LineNumber(newlines!, match.Index)));
            }
        }

        if (starts is null)
        {
            return;
        }

        // Regexes find declarations pattern by pattern, so source order is rebuilt before each one is
        // bounded by the next declaration's start.
        starts.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = 0; i < starts.Count; i++)
        {
            var next = i + 1 < starts.Count ? starts[i + 1].Start - 1 : lines!.Count;
            spans![starts[i].Path] = LineSpan.Trimmed(lines!, starts[i].Start, Math.Max(starts[i].Start, next));
        }
    }

    private static void AddImports(
        string fileEntityPath,
        string fileName,
        string content,
        LanguageDefinition language,
        List<CodeCandidate> candidates)
    {
        var modules = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var pattern in language.ImportPatterns)
        {
            foreach (System.Text.RegularExpressions.Match match in LanguageRegistry.Compiled(pattern).Matches(content))
            {
                var module = match.Groups["module"].Value;
                if (module.Length > 0)
                {
                    modules.Add(module);
                }
            }
        }

        foreach (var m in modules)
        {
            candidates.Add(new CodeCandidate(
                fileEntityPath,
                "file",
                fileName,
                "imports",
                Cap("imports " + m),
                Object: m));
        }
    }

    private static void AnalyzeDocument(
        string fileEntityPath,
        string fileName,
        string content,
        List<CodeCandidate> candidates,
        Dictionary<string, LineSpan>? spans,
        IReadOnlyList<string>? sourceLines)
    {
        var lines = content.Split('\n');
        var headings = new List<(int Line, int Level)>();
        var firstHeading = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<(int Level, string Slug)>();
        var sections = new List<(string Fragment, string Heading, StringBuilder Body)>();
        var bodies = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var preamble = new StringBuilder();
        var current = preamble;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex].TrimEnd('\r');
            var level = HeadingLevel(line, out var heading);

            if (level == 0)
            {
                current.AppendLine(line);
                continue;
            }

            while (stack.Count > 0 && stack[^1].Level >= level)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            stack.Add((level, CodePaths.Slug(heading)));

            // Two headings can slug to one fragment; their prose merges under the first,
            // because one address can only hold one section entity.
            var fragment = string.Join('/', stack.ConvertAll(entry => entry.Slug));
            headings.Add((lineIndex + 1, level));
            firstHeading.TryAdd(fragment, headings.Count - 1);
            if (!bodies.TryGetValue(fragment, out var body))
            {
                body = new StringBuilder();
                bodies.Add(fragment, body);
                sections.Add((fragment, heading, body));
            }

            current = body;
        }

        var overview = ImpressionExtractor.FromProse(
            preamble.Length > 0 ? preamble.ToString() : StripHeadings(content));
        if (overview is not null)
        {
            candidates.Add(new CodeCandidate(fileEntityPath, "file", fileName, "about", overview));
        }

        foreach (var (fragment, heading, body) in sections)
        {
            if (spans is not null)
            {
                var index = firstHeading[fragment];
                var (start, level) = headings[index];
                var end = sourceLines!.Count;
                for (var next = index + 1; next < headings.Count; next++)
                {
                    if (headings[next].Level <= level)
                    {
                        end = headings[next].Line - 1;
                        break;
                    }
                }

                spans[CodePaths.ForSection(fileEntityPath, fragment)] = LineSpan.Trimmed(sourceLines!, start, Math.Max(start, end));
            }

            var impression = ImpressionExtractor.FromProse(body.ToString());
            if (impression is not null)
            {
                candidates.Add(new CodeCandidate(
                    CodePaths.ForSection(fileEntityPath, fragment),
                    "section",
                    heading,
                    "about",
                    impression));
            }
        }
    }

    private static int HeadingLevel(string line, out string heading)
    {
        var hashes = 0;
        while (hashes < line.Length && line[hashes] == '#')
        {
            hashes++;
        }

        if (hashes is 0 or > 6 || hashes == line.Length || line[hashes] != ' ')
        {
            heading = string.Empty;
            return 0;
        }

        heading = line[(hashes + 1)..].Trim().TrimEnd('#').TrimEnd();
        return heading.Length == 0 ? 0 : hashes;
    }

    private static string StripHeadings(string content)
    {
        var builder = new StringBuilder(content.Length);
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (HeadingLevel(line, out _) == 0)
            {
                builder.AppendLine(line);
            }
        }

        return builder.ToString();
    }

    private static string LineOf(string content, int index)
    {
        var start = content.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = content.IndexOf('\n', index);
        return end < 0 ? content[start..] : content[start..end];
    }

    internal static string Cap(string text)
    {
        if (TokenEstimator.Estimate(text) <= ImpressionExtractor.MaxTokens)
        {
            return text;
        }

        var limit = ImpressionExtractor.MaxTokens * 4;
        var cut = text.LastIndexOf(' ', Math.Min(text.Length, limit) - 1);
        return (cut > 0 ? text[..cut] : text[..limit]).TrimEnd() + "…";
    }
}
