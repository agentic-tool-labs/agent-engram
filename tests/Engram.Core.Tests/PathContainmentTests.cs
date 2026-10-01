using Engram.Core;

namespace Engram.Core.Tests;

public class PathContainmentTests
{
    [Fact]
    public void ALinkFreeFileOutsideTheRoot_IsRefusedByTheWalkItself()
    {
        var parent = Path.Combine(Path.GetTempPath(), "engram-contain-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "repo");
        var sibling = Path.Combine(parent, "sibling");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);
        var outside = Path.Combine(sibling, "x.md");
        File.WriteAllText(outside, "not in the repo");
        try
        {
            Assert.True(PathContainment.HasLinkBelow(root, outside));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}
