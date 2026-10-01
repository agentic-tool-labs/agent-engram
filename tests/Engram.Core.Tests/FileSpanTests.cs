using Engram.Core;

namespace Engram.Core.Tests;

public class FileSpanTests
{
    private const string FilePath = "/projects/p/code/r/src/Sample.cs";
    private const string DocPath = "/projects/p/code/r/docs/guide.md";

    private static LanguageDefinition CSharp() =>
        LanguageRegistry.All.Single(language => language.Id == "csharp");

    private static LanguageDefinition Markdown() =>
        LanguageRegistry.All.Single(language => language.DocHeadings);

    private static string Join(string newline, params string[] lines) => string.Join(newline, lines);

    private static readonly string[] CSharpLines =
    [
        "using System;",        // 1
        "",                     // 2
        "public class Alpha",   // 3
        "{",                    // 4
        "}",                    // 5
        "",                     // 6
        "",                     // 7
        "public enum Beta { X }", // 8
        "public interface Gamma", // 9
        "{",                    // 10
        "}",                    // 11
    ];

    [Fact]
    public void TierZeroDeclarations_RunToTheNextDeclaration_WithTrailingBlanksDropped()
    {
        var analysis = FileAnalysis.Analyze(FilePath, Join("\n", CSharpLines) + "\n", CSharp(), null);

        Assert.Equal(new LineSpan(3, 5), analysis.Spans[CodePaths.ForSymbol(FilePath, "Alpha")]);
        Assert.Equal(new LineSpan(8, 8), analysis.Spans[CodePaths.ForSymbol(FilePath, "Beta")]);
        Assert.Equal(new LineSpan(9, 11), analysis.Spans[CodePaths.ForSymbol(FilePath, "Gamma")]);
    }

    [Fact]
    public void TierZeroLastDeclaration_RunsToTheEndOfTheFile_DroppingTrailingBlankLines()
    {
        var content = Join("\n", CSharpLines) + "\n\n\n";

        var analysis = FileAnalysis.Analyze(FilePath, content, CSharp(), null);

        Assert.Equal(new LineSpan(9, 11), analysis.Spans[CodePaths.ForSymbol(FilePath, "Gamma")]);
    }

    private static readonly string[] MarkdownLines =
    [
        "# Title",                                     // 1
        "This guide explains how the widget works.",   // 2
        "",                                            // 3
        "## One",                                      // 4
        "The first section describes assembly steps.", // 5
        "",                                            // 6
        "### Deep",                                    // 7
        "The nested section covers torque limits.",    // 8
        "",                                            // 9
        "## Two",                                      // 10
        "The second section covers maintenance.",      // 11
        "",                                            // 12
        "## One",                                      // 13
        "A duplicate heading merges under the first.", // 14
    ];

    [Fact]
    public void Sections_ParentIncludesChildren_SiblingEndsTheSpan_DuplicateTakesTheFirstRange()
    {
        var analysis = FileAnalysis.Analyze(DocPath, Join("\n", MarkdownLines), Markdown(), null);

        LineSpan Of(string fragment) => analysis.Spans[CodePaths.ForSection(DocPath, fragment)];

        Assert.Equal(new LineSpan(1, 14), Of("title"));
        Assert.Equal(new LineSpan(4, 8), Of("title/one"));
        Assert.Equal(new LineSpan(7, 8), Of("title/one/deep"));
        Assert.Equal(new LineSpan(10, 11), Of("title/two"));
    }

    [Fact]
    public void FileSpan_IsOneToN_AndATrailingNewlineAddsNoLine()
    {
        var withNewline = FileAnalysis.Analyze(FilePath, "public class A\n{\n}\n", CSharp(), null);
        var without = FileAnalysis.Analyze(FilePath, "public class A\n{\n}", CSharp(), null);
        var blankTail = FileAnalysis.Analyze(FilePath, "public class A\n{\n}\n\n", CSharp(), null);

        Assert.Equal(new LineSpan(1, 3), withNewline.Spans[FilePath]);
        Assert.Equal(new LineSpan(1, 3), without.Spans[FilePath]);
        Assert.Equal(new LineSpan(1, 4), blankTail.Spans[FilePath]);
    }

    [Fact]
    public void CrlfFile_NumbersLikeItsLfTwin()
    {
        var lf = FileAnalysis.Analyze(FilePath, Join("\n", CSharpLines) + "\n", CSharp(), null);
        var crlf = FileAnalysis.Analyze(FilePath, Join("\r\n", CSharpLines) + "\r\n", CSharp(), null);

        Assert.Equal(lf.Spans.OrderBy(kv => kv.Key), crlf.Spans.OrderBy(kv => kv.Key));
    }

    [Fact]
    public void ShiftingTheFile_ChangesNoCandidate_SoSpansCannotReachAWrite()
    {
        var content = Join("\n", CSharpLines) + "\n";
        var shifted = "\n\n\n\n\n" + content;

        var before = FileAnalysis.Analyze(FilePath, content, CSharp(), null).Candidates;
        var after = FileAnalysis.Analyze(FilePath, shifted, CSharp(), null).Candidates;

        Assert.Equal(before, after);
    }
}
