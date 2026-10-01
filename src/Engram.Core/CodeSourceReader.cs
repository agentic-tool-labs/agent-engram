using System.Text;

namespace Engram.Core;

/// <summary>What reading one indexed file produced: its decoded text, or the reason there is none.</summary>
public readonly record struct SourceRead(string? Content, string? Reason);

/// <summary>
/// Reads the live file behind an indexed code fact, refusing anything the indexer itself would not
/// have read. The path comes from a store row, so it is untrusted: containment is checked by spelling
/// where <see cref="FileFreshness.Check"/> resolves it, and again here against what is on disk.
/// </summary>
public static class CodeSourceReader
{
    public static SourceRead Read(FileFreshness.Verdict verdict, string subjectPath, long maxFileBytes) =>
        Read(verdict, subjectPath, maxFileBytes, OpenRead);

    /// <summary>The stream is a seam so a test can prove how much of it is ever consumed.</summary>
    internal static SourceRead Read(
        FileFreshness.Verdict verdict,
        string subjectPath,
        long maxFileBytes,
        Func<string, Stream> open)
    {
        if (verdict.File is not { } file
            || verdict.State == FileFreshness.State.Unknown
            || CodePaths.SplitRepoPath(subjectPath) is not var (_, relative))
        {
            return new SourceRead(null, "location unknown");
        }

        // A directory at the indexed path reads as Missing to File.Exists, but it is there.
        if (verdict.State == FileFreshness.State.Missing && !Directory.Exists(file))
        {
            return new SourceRead(null, "file missing");
        }

        try
        {
            // The file path is the root with the relative path appended, so the root is what remains
            // once that suffix is removed; the verdict does not carry it separately.
            var root = file[..(file.Length - relative.Length)].TrimEnd(Path.DirectorySeparatorChar);
            if (!PathContainment.IsPhysicallyWithin(root.Length == 0 ? Path.DirectorySeparatorChar.ToString() : root, file))
            {
                return new SourceRead(null, "outside the repo");
            }

            // Only a directory is refused by type: a FIFO reports the same attributes as a regular file
            // here, so it cannot be told apart. git cannot track one and the indexer's own read has the
            // same exposure.
            if (Directory.Exists(file))
            {
                return new SourceRead(null, "not a regular file");
            }

            if (new FileInfo(file).Length > maxFileBytes)
            {
                return new SourceRead(null, $"over {maxFileBytes} bytes");
            }

            // One byte past the limit is enough to know the file outgrew its stat, so that is as much as
            // is ever pulled, and in chunks so a large configured limit does not allocate up front.
            using var stream = open(file);
            var content = new MemoryStream();
            var chunk = new byte[81920];
            while (content.Length <= maxFileBytes)
            {
                var want = (int)Math.Min(chunk.Length, maxFileBytes + 1 - content.Length);
                var got = stream.Read(chunk, 0, want);
                if (got == 0)
                {
                    break;
                }

                content.Write(chunk, 0, got);
            }

            if (content.Length > maxFileBytes)
            {
                return new SourceRead(null, $"over {maxFileBytes} bytes");
            }

            var buffer = content.GetBuffer();
            var count = (int)content.Length;
            var head = buffer.AsSpan(0, Math.Min(count, IndexingSettings.HeadBytes));
            if (IndexFilter.Classify(head, int.MaxValue).Reason == SkipReason.Binary)
            {
                return new SourceRead(null, "binary");
            }

            // The decoding File.ReadAllText applies, so lines are numbered the way the analyzer saw them.
            using var reader = new StreamReader(new MemoryStream(buffer, 0, count), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return new SourceRead(reader.ReadToEnd(), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new SourceRead(null, "unreadable");
        }
    }

    private static Stream OpenRead(string file) =>
        new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
}
