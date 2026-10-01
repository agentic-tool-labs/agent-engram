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

    private static SourceRead Read((FileFreshness.Verdict Verdict, string Subject) entry, long limit = Limit) =>
        CodeSourceReader.Read(entry.Verdict, entry.Subject, limit);

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

    [Fact]
    public void LinkOutOfTheCheckout_IsRefused_AndNoOutsideByteIsReturned()
    {
        File.CreateSymbolicLink(Path.Combine(root, "src", "link.cs"), outside);

        var entry = Register("src/link.cs");
        var read = Read(entry);

        Assert.Equal(FileFreshness.State.Fresh, entry.Verdict.State);
        Assert.Equal("outside the repo", read.Reason);
        Assert.Null(read.Content);
    }

    [Fact]
    public void LinkInsideTheCheckout_ReturnsContent()
    {
        File.WriteAllText(Path.Combine(root, "src", "real.cs"), "class Real {}");
        File.CreateSymbolicLink(Path.Combine(root, "src", "alias.cs"), Path.Combine(root, "src", "real.cs"));

        Assert.Equal("class Real {}", Read(Register("src/alias.cs")).Content);
    }

    [Fact]
    public void CheckoutRegisteredThroughASymlinkedParent_ReturnsContent()
    {
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

    [Fact]
    public void MissingFile_SaysSo()
    {
        Assert.Equal("file missing", Read(Register("src/gone.cs")).Reason);
    }
}
