using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Declaration line ranges through the real tree-sitter grammars. Environment-gated like the other
/// grammar tests: <c>ENGRAM_TEST_TREE_SITTER_DIR</c> must point at the compiled libraries.
/// </summary>
public class TreeSitterSpanTests
{
    private const string FilePath = "/projects/p/code/r/src/sample.ts";

    // Line 1 carries multi-byte characters: a char count that is mistaken for a byte offset (or the
    // reverse) shifts every span below it.
    private static readonly string[] Lines =
    [
        "// héllo — ünïcödé ✓ ✓ ✓ ✓ ✓",                // 1
        "export function greet(name: string) {",         // 2
        "  return `hi ${name}`;",                        // 3
        "}",                                             // 4
        "// trailing note",                              // 5
        "",                                              // 6
        "export class Box {",                            // 7
        "  open(): void {",                              // 8
        "    // ça va — ok",                             // 9
        "  }",                                           // 10
        "}",                                             // 11
        "",                                              // 12
        "export const twice = (n: number) => {",         // 13
        "  return n * 2;",                               // 14
        "};",                                            // 15
    ];

    private static string? GrammarDir()
    {
        var dir = Environment.GetEnvironmentVariable("ENGRAM_TEST_TREE_SITTER_DIR");
        return dir is { Length: > 0 } && File.Exists(Path.Combine(dir, TreeSitter.CoreLibraryFile))
            ? dir
            : null;
    }

    private static LanguageDefinition TypeScript() =>
        LanguageRegistry.All.Single(language => language.Id == "typescript");

    private static Dictionary<string, LineSpan?> DeepSpans(string content)
    {
        var dir = GrammarDir();
        Assert.SkipWhen(dir is null, "ENGRAM_TEST_TREE_SITTER_DIR does not point at the compiled grammars.");

        using var runtime = TreeSitter.TryCreate(dir!, []);
        Assert.NotNull(runtime);
        var analysis = runtime.Analyze(TypeScript(), "sample.ts", content);
        Assert.NotNull(analysis);
        return DeepTier.Fragments(analysis.Symbols).ToDictionary(f => f.Fragment, f => f.Symbol.Span);
    }

    [Fact]
    public void TierOne_SpansAreTheDeclarationNodesLines_ThroughMultiByteText()
    {
        var spans = DeepSpans(string.Join("\n", Lines) + "\n");

        Assert.Equal(new LineSpan(2, 4), spans["greet"]);
        Assert.Equal(new LineSpan(7, 11), spans["Box"]);
        Assert.Equal(new LineSpan(8, 10), spans["Box/open"]);
        Assert.Equal(new LineSpan(13, 15), spans["twice"]);
    }

    [Fact]
    public void TierOne_CrlfFile_NumbersLikeItsLfTwin()
    {
        var lf = DeepSpans(string.Join("\n", Lines) + "\n");
        var crlf = DeepSpans(string.Join("\r\n", Lines) + "\r\n");

        Assert.Equal(lf.OrderBy(kv => kv.Key), crlf.OrderBy(kv => kv.Key));
    }

    [Fact]
    public void DeepSpan_BeatsTierZeros_WhereTheyDisagree()
    {
        var dir = GrammarDir();
        Assert.SkipWhen(dir is null, "ENGRAM_TEST_TREE_SITTER_DIR does not point at the compiled grammars.");
        using var runtime = TreeSitter.TryCreate(dir!, []);
        Assert.NotNull(runtime);
        var content = string.Join("\n", Lines) + "\n";
        var deep = runtime.Analyze(TypeScript(), "sample.ts", content);

        var tierZero = FileAnalysis.Analyze(FilePath, content, TypeScript(), null);
        var merged = FileAnalysis.Analyze(FilePath, content, TypeScript(), deep);
        var greet = CodePaths.ForSymbol(FilePath, "greet");

        // The regex bounds greet by the next declaration, so the trailing comment is inside it.
        Assert.Equal(new LineSpan(2, 5), tierZero.Spans[greet]);
        Assert.Equal(new LineSpan(2, 4), merged.Spans[greet]);
    }
}
