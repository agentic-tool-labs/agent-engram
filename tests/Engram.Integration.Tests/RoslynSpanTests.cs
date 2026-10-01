using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Declaration line ranges through the real Roslyn sidecar. The test project's ProjectReference
/// builds the sidecar, so a missing binary is broken wiring and fails rather than skips.
/// </summary>
public class RoslynSpanTests
{
    private static readonly string[] Lines =
    [
        "using System;",                                   // 1
        "",                                                // 2
        "namespace Demo;",                                 // 3
        "",                                                // 4
        "public class Outer",                              // 5
        "{",                                               // 6
        "    /// <summary>Allman method.</summary>",       // 7
        "    public void Allman(int x)",                   // 8
        "    {",                                           // 9
        "        Console.WriteLine(x);",                   // 10
        "    }",                                           // 11
        "",                                                // 12
        "    [Obsolete(\"old\")]",                         // 13
        "    [System.Diagnostics.Conditional(\"DEBUG\")]", // 14
        "    public void Attributed()",                    // 15
        "    {",                                           // 16
        "    }",                                           // 17
        "",                                                // 18
        "    public int Expr(int x) => x * 2;",            // 19
        "",                                                // 20
        "    public class Inner",                          // 21
        "    {",                                           // 22
        "        public void M()",                         // 23
        "        {",                                       // 24
        "        }",                                       // 25
        "    }",                                           // 26
        "",                                                // 27
        "    public void Over(int a)",                     // 28
        "    {",                                           // 29
        "    }",                                           // 30
        "",                                                // 31
        "    public void Over(string a)",                  // 32
        "    {",                                           // 33
        "    }",                                           // 34
        "}",                                               // 35
    ];

    private static string SidecarBinary()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "engram-schema.sql")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        var name = OperatingSystem.IsWindows() ? "engram-roslyn.exe" : "engram-roslyn";
        var configurations = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? new[] { "Release", "Debug" }
            : ["Debug", "Release"];

        foreach (var configuration in configurations)
        {
            var candidate = Path.Combine(
                dir.FullName, "src", "Engram.Sidecar.Roslyn", "bin", configuration, "net10.0", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Fail("engram-roslyn is not built; the ProjectReference in this test project should have built it");
        return null!;
    }

    private static Dictionary<string, LineSpan?> Spans(string content)
    {
        var results = RoslynSidecar.Analyze(SidecarBinary(), [("Sample.cs", content)], TimeSpan.FromSeconds(30));
        Assert.NotNull(results);
        var analysis = results["Sample.cs"];
        Assert.Null(analysis.Error);
        return DeepTier.Fragments(analysis.Symbols).ToDictionary(f => f.Fragment, f => f.Symbol.Span);
    }

    [Fact]
    public void TierTwo_SpansStartAtTheDeclaration_NotItsDocComment_AndIncludeAttributes()
    {
        var spans = Spans(string.Join("\n", Lines) + "\n");

        Assert.Equal(new LineSpan(5, 35), spans["Outer"]);
        Assert.Equal(new LineSpan(8, 11), spans["Outer/Allman"]);
        Assert.Equal(new LineSpan(13, 17), spans["Outer/Attributed"]);
        Assert.Equal(new LineSpan(19, 19), spans["Outer/Expr"]);
    }

    [Fact]
    public void TierTwo_NestedTypeMembers_AndCollidingOverloads_EachGetTheirOwnSpan()
    {
        var spans = Spans(string.Join("\n", Lines) + "\n");

        Assert.Equal(new LineSpan(23, 25), spans["Outer/Inner/M"]);
        Assert.Equal(new LineSpan(28, 30), spans["Outer/Over(int a)"]);
        Assert.Equal(new LineSpan(32, 34), spans["Outer/Over(string a)"]);
    }

    [Fact]
    public void TierTwo_CrlfFile_NumbersLikeItsLfTwin()
    {
        var lf = Spans(string.Join("\n", Lines) + "\n");
        var crlf = Spans(string.Join("\r\n", Lines) + "\r\n");

        Assert.Equal(lf.OrderBy(kv => kv.Key), crlf.OrderBy(kv => kv.Key));
    }
}
