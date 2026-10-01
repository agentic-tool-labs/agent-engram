namespace Engram.Integration.Tests;

/// <summary>
/// The test csproj's ProjectReference builds the sidecar, so absence here is broken wiring, not a
/// configuration — it fails rather than skips.
/// </summary>
internal static class SidecarLocator
{
    public static string Binary()
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
}
