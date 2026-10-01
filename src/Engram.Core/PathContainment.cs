namespace Engram.Core;

/// <summary>
/// Whether a path stays inside the directory it was resolved under. Two questions, kept apart because
/// they fail differently: a spelling that climbs out (<c>..</c>) is decided from the string alone,
/// while a symlink that leads out can only be seen on disk.
/// </summary>
public static class PathContainment
{
    /// <summary>
    /// A relative path that names something below its root by spelling alone: not rooted, and no
    /// empty, <c>.</c> or <c>..</c> segment.
    /// </summary>
    public static bool IsSafeRelative(string relative)
    {
        if (relative.Length == 0 || Path.IsPathRooted(relative))
        {
            return false;
        }

        foreach (var segment in relative.Split('/', Path.DirectorySeparatorChar))
        {
            if (segment is "" or "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="path"/> lies inside <paramref name="root"/>, comparing the paths as spelled.</summary>
    public static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="path"/> lies inside <paramref name="root"/> once every symlink in both —
    /// the final component included, since the file itself may be a link — has been followed. A root
    /// that sits under a symlinked directory resolves the same way the file does, so it still passes.
    /// Fails closed: a link that cannot be inspected, or a chain too long to follow, counts as outside.
    /// </summary>
    public static bool IsPhysicallyWithin(string root, string path) =>
        PathCanonicalizer.TryCanonical(root) is { } resolvedRoot
        && PathCanonicalizer.TryCanonical(path) is { } resolvedPath
        && IsWithin(resolvedRoot, resolvedPath);
}
