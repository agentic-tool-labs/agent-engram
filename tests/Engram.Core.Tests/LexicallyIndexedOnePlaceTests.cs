using System.Text.RegularExpressions;

namespace Engram.Core.Tests;

/// <summary>
/// Holds D102: which facts the lexical, overlap and vector lanes index is written once, in
/// <see cref="EngramDatabase.RegenerableEdgeFor"/>, and every statement takes it from there. Two
/// hand copies disagreed once already — repair's detector lacked the exclusion and reported
/// 360,391 phantom missing rows — so a copy in <c>src/</c> fails here. The schema triggers spell
/// it with <c>new.</c>/<c>old.</c> and are held to the constant by
/// <c>StoreRepairerTests.LexicallyIndexed_MatchesTheConditionInEveryFtsTrigger</c> instead.
/// </summary>
public class LexicallyIndexedOnePlaceTests
{
    private static readonly Regex Predicate = new(
        @"NOT\s*\(\s*(?:(\w+)\.)?regenerable\s+IS\s+1\s+AND\s+(?:(\w+)\.)?object_id\s+IS\s+NOT\s+NULL\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void NoSourceFile_HandCopiesTheLexicallyIndexedPredicate()
    {
        var srcRoot = Path.Combine(FindRepoRoot(), "src");
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            var segments = Path.GetRelativePath(srcRoot, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Contains("obj") || segments.Contains("bin"))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (Match match in Predicate.Matches(text))
            {
                if (IsTriggerSpelling(match))
                {
                    continue;
                }

                var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                violations.Add($"{Path.GetRelativePath(srcRoot, file)}:{line}: {match.Value}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Take the lane-eligibility condition from EngramDatabase.LexicallyIndexed or "
                + "LexicallyIndexedFor(alias) rather than writing it out (D102):\n  "
                + string.Join("\n  ", violations));
    }

    /// <summary>
    /// The pattern recognizes what the definition produces, so a reworded definition cannot leave
    /// the guard matching a spelling nothing uses any more.
    /// </summary>
    [Fact]
    public void Pattern_MatchesEveryFormTheDefinitionProduces()
    {
        Assert.Matches(Predicate, EngramDatabase.LexicallyIndexed);
        Assert.Matches(Predicate, EngramDatabase.LexicallyIndexedFor("f"));
        Assert.Matches(Predicate, EngramDatabase.LexicallyIndexedFor("fact"));
        Assert.Matches(Predicate, "NOT (f.regenerable IS 1\n   AND f.object_id IS NOT NULL)");
    }

    [Theory]
    [InlineData("new.")]
    [InlineData("old.")]
    public void TriggerSpelling_IsExempt(string prefix)
    {
        var match = Predicate.Match($"NOT ({prefix}regenerable IS 1 AND {prefix}object_id IS NOT NULL)");

        Assert.True(match.Success);
        Assert.True(IsTriggerSpelling(match));
        Assert.False(IsTriggerSpelling(Predicate.Match(EngramDatabase.LexicallyIndexedFor("f"))));
    }

    [Theory]
    [InlineData("f.regenerable IS 1 AND f.object_id IS NOT NULL")]
    [InlineData("regenerable IS 1 AND +object_id IS NOT NULL")]
    [InlineData("NOT (regenerable IS 1 AND object_id IS NULL)")]
    public void NearMiss_DoesNotMatch(string text) => Assert.DoesNotMatch(Predicate, text);

    private static bool IsTriggerSpelling(Match match) =>
        match.Groups[1].Value is "new" or "old" && match.Groups[2].Value == match.Groups[1].Value;

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Engram.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate repo root (Engram.sln) by walking up from AppContext.BaseDirectory.");
    }
}
