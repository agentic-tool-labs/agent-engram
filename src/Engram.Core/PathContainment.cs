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
    /// Whether any segment of <paramref name="path"/> below <paramref name="root"/>, the last one
    /// included, is a symlink. Nothing is resolved or followed: with no empty, <c>.</c> or <c>..</c>
    /// segment (see <see cref="IsSafeRelative"/>) and no link below the root, the path the OS opens is
    /// the path that was checked. The root itself is trusted, so a checkout under a linked directory
    /// is not refused. A segment that does not exist ends the walk, since nothing beneath it can be a link.
    /// </summary>
    public static bool HasLinkBelow(string root, string path)
    {
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);

            // LinkTarget is readlink: set for any link whatever it points at, even nothing. Attributes
            // is not usable here, it reads as every flag set for a path that does not exist.
            if (new FileInfo(current).LinkTarget is not null)
            {
                return true;
            }

            if (!File.Exists(current) && !Directory.Exists(current))
            {
                return false;
            }
        }

        return false;
    }
}
