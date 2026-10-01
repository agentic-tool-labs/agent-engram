using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// The path an indexed fact names comes from a store row, so reading it back is a trust boundary:
/// each refusal has a test that only that refusal can fail.
/// </summary>
public sealed class CodeSourceReaderTests : IDisposable
{
    private const string RepoPath = "/projects/p/code/r";
    private const long Limit = 1_000_000;

    private readonly SandboxHome sandbox = new();
    private readonly SqliteConnection connection;
    private readonly string root;
    private readonly string outside;

    public CodeSourceReaderTests()
    {
        connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var parent = Path.Combine(sandbox.Home.Root, "checkouts");
        root = Path.Combine(parent, "repo");
        outside = Path.Combine(parent, "outside.txt");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(outside, "SECRET-OUTSIDE-BYTES");
    }

    public void Dispose()
    {
        connection.Dispose();
        sandbox.Dispose();
    }

    private (FileFreshness.Verdict Verdict, string Subject) Register(string relative, string? diskPath = null)
    {
        Execute(
            "INSERT OR IGNORE INTO repo_registry (repo_path, identity, disk_path, created_at) VALUES ($repo, 'id', $disk, 0);",
            ("$repo", RepoPath), ("$disk", diskPath ?? root));
        Execute(
            "INSERT INTO file_state (repo_path, path, blob_sha, indexed_at) VALUES ($repo, $path, 'x', 4102444800);",
            ("$repo", RepoPath), ("$path", relative));
        var subject = $"{RepoPath}/{relative}";
        return (FileFreshness.Check(connection, subject), subject);
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    // Creating a link needs a privilege some Windows runners lack. A skip is only for that: the probe
    // makes a real link, so wherever links work every test below still runs.
    private void RequireSymlinks()
    {
        var probe = Path.Combine(Path.GetDirectoryName(root)!, "link-probe");
        try
        {
            File.CreateSymbolicLink(probe, outside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("this environment cannot create symbolic links");
            return;
        }

        File.Delete(probe);
    }

    // Relative link targets are spelled with the platform's separator; Windows rejects a forward
    // slash in a directory link's target.
    private static string Target(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    private static SourceRead Read((FileFreshness.Verdict Verdict, string Subject) entry, long limit = Limit) =>
        CodeSourceReader.Read(entry.Verdict, limit);

    [Fact]
    public void RegularFile_ReturnsItsText_WithTheByteOrderMarkStripped()
    {
        File.WriteAllBytes(Path.Combine(root, "src", "a.cs"), [0xEF, 0xBB, 0xBF, .. "class A {}"u8.ToArray()]);

        var read = Read(Register("src/a.cs"));

        Assert.Equal("class A {}", read.Content);
        Assert.Null(read.Reason);
    }

    [Fact]
    public void ClimbingPath_ResolvesToNothing_SoNoViewCanPrintIt()
    {
        var entry = Register("src/../../outside.txt");

        Assert.Equal(FileFreshness.State.Unknown, entry.Verdict.State);
        Assert.Null(entry.Verdict.File);
        Assert.Equal("location unknown", Read(entry).Reason);
    }

    // Content stays null, so no byte of whatever the link leads to can reach the output.
    private static void AssertRefusedAsLink(SourceRead read)
    {
        Assert.Equal("symlinked path", read.Reason);
        Assert.Null(read.Content);
    }

    [Fact]
    public void FileLinkedToSomethingOutsideTheCheckout_IsRefused()
    {
        RequireSymlinks();
        File.CreateSymbolicLink(Path.Combine(root, "src", "link.cs"), outside);

        var entry = Register("src/link.cs");

        Assert.Equal(FileFreshness.State.Fresh, entry.Verdict.State);
        AssertRefusedAsLink(Read(entry));
    }

    [Fact]
    public void FileLinkedToSomethingInsideTheCheckout_IsRefusedToo_ByDesign()
    {
        RequireSymlinks();
        File.WriteAllText(Path.Combine(root, "README.md"), "inside");
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.CreateSymbolicLink(Path.Combine(root, "docs", "readme.md"), Target("../README.md"));

        AssertRefusedAsLink(Read(Register("docs/readme.md")));
    }

    [Fact]
    public void DirectoryLinkedOutsideTheCheckout_RefusesAFileBeneathIt()
    {
        RequireSymlinks();
        var elsewhere = Path.Combine(Path.GetDirectoryName(root)!, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "x.md"), "SECRET-VIA-DIRECTORY");
        Directory.CreateSymbolicLink(Path.Combine(root, "D"), elsewhere);

        AssertRefusedAsLink(Read(Register("D/x.md")));
    }

    // D -> <outside>/a/b and f.md -> D/../x.md: folding the dot-dot as text before following D lands
    // inside the checkout, while the OS opens <outside>/a/x.md.
    [Fact]
    public void ADotDotInsideALinkTarget_CannotReachAFileOutside()
    {
        RequireSymlinks();
        var elsewhere = Path.Combine(Path.GetDirectoryName(root)!, "dotdot");
        Directory.CreateDirectory(Path.Combine(elsewhere, "a", "b"));
        File.WriteAllText(Path.Combine(elsewhere, "a", "x.md"), "SECRET-VIA-DOTDOT");
        Directory.CreateSymbolicLink(Path.Combine(root, "D"), Path.Combine(elsewhere, "a", "b"));
        File.CreateSymbolicLink(Path.Combine(root, "f.md"), Target("D/../x.md"));

        AssertRefusedAsLink(Read(Register("f.md")));
    }

    [Fact]
    public void ALinkCycle_IsRefusedPromptly_WithoutAHangOrAnException()
    {
        RequireSymlinks();
        File.CreateSymbolicLink(Path.Combine(root, Target("cyc-a")), Target("cyc-b"));
        File.CreateSymbolicLink(Path.Combine(root, Target("cyc-b")), Target("cyc-a"));

        var read = Read(Register("cyc-a/x.md"));

        AssertRefusedAsLink(read);
    }

    [Fact]
    public void ADanglingLinkAtTheFilePath_IsMissing_AndNothingIsRead()
    {
        RequireSymlinks();
        File.CreateSymbolicLink(Path.Combine(root, "src", "dangling.cs"), Target("nowhere.cs"));

        var entry = Register("src/dangling.cs");

        Assert.Equal(FileFreshness.State.Missing, entry.Verdict.State);
        Assert.NotNull(entry.Verdict.File);
        AssertRefusedAsLink(Read(entry));
    }

    [Fact]
    public void AnAbsentIntermediateDirectory_IsMissing_NotALink()
    {
        var entry = Register("gone/x.md");

        Assert.Equal("file missing", Read(entry).Reason);
    }

    [Fact]
    public void CheckoutRegisteredThroughASymlinkedParent_ReturnsContent()
    {
        RequireSymlinks();
        File.WriteAllText(Path.Combine(root, "src", "a.cs"), "class A {}");
        var viaLink = Path.Combine(sandbox.Home.Root, "via-link");
        Directory.CreateSymbolicLink(viaLink, Path.GetDirectoryName(root)!);

        var read = Read(Register("src/a.cs", Path.Combine(viaLink, "repo")));

        Assert.Equal("class A {}", read.Content);
    }

    [Fact]
    public void DirectoryAtTheFilePath_IsNotARegularFile()
    {
        Directory.CreateDirectory(Path.Combine(root, "src", "dir.cs"));

        Assert.Equal("not a regular file", Read(Register("src/dir.cs")).Reason);
    }

    [Fact]
    public void FileOverTheConfiguredLimit_IsRefused_WithTheLimitNamed()
    {
        File.WriteAllText(Path.Combine(root, "src", "big.cs"), new string('x', 50));

        Assert.Equal("over 10 bytes", Read(Register("src/big.cs"), limit: 10).Reason);
    }

    [Fact]
    public void NulInTheHead_IsBinary()
    {
        File.WriteAllBytes(Path.Combine(root, "src", "blob.cs"), [.. "class A"u8.ToArray(), 0, 1, 2]);

        Assert.Equal("binary", Read(Register("src/blob.cs")).Reason);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void UnreadableFile_ReportsUnreadable_WithoutThrowing()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "UnixFileMode is not enforced on Windows.");
        var file = Path.Combine(root, "src", "locked.cs");
        File.WriteAllText(file, "class L {}");
        File.SetUnixFileMode(file, UnixFileMode.None);
        try
        {
            Assert.Equal("unreadable", Read(Register("src/locked.cs")).Reason);
        }
        finally
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // Li -> L(i+1)/x, so resolving one link leaves a path that still runs through the next: each costs
    // a recursion rather than collapsing into one final-target lookup.
    private string LinkChain(int links, string end)
    {
        var deepest = end;
        for (var i = 0; i < links - 1; i++)
        {
            deepest = Path.Combine(deepest, "x");
        }

        Directory.CreateDirectory(deepest);
        File.WriteAllText(Path.Combine(deepest, "secret.txt"), "CHAIN-BYTES");
        for (var i = 0; i < links; i++)
        {
            Directory.CreateSymbolicLink(
                Path.Combine(root, $"L{i}"),
                i == links - 1 ? end : Target($"L{i + 1}/x"));
        }

        return "L0/secret.txt";
    }

    [Fact]
    public void TenNestedDirectoryLinks_AreRefused()
    {
        RequireSymlinks();
        var entry = Register(LinkChain(10, Path.Combine(Path.GetDirectoryName(root)!, "chained")));

        AssertRefusedAsLink(Read(entry));
    }

    [Fact]
    public void MissingFile_SaysSo()
    {
        Assert.Equal("file missing", Read(Register("src/gone.cs")).Reason);
    }
}
