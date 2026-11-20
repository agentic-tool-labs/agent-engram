using Engram.Core;

namespace Engram.Core.Tests;

/// <summary>P4 addressing and predicate rules (D98).</summary>
public class ProjectNotesTests
{
    [Fact]
    public void Fingerprint_ByDefault_IsTheSameEightHexAsBefore()
    {
        Assert.Equal("afe60d0f", FactStore.Fingerprint("I use Dvorak."));
        Assert.Equal("afe60d0f", FactStore.Fingerprint("i use dvorak"));
    }

    [Fact]
    public void Fingerprint_AtEightBytes_ExtendsTheSameHash()
    {
        Assert.Equal("afe60d0f5858a25e", FactStore.Fingerprint("I use Dvorak.", ProjectNotes.FingerprintBytes));
    }

    [Fact]
    public void ProjectPath_UsesTheSixteenHexLeaf()
    {
        Assert.Equal("/projects/acme/notes/afe60d0f5858a25e", ProjectNotes.PathFor("acme", "I use Dvorak."));
    }

    [Theory]
    [InlineData("Never call X from the hook", "warns")]
    [InlineData("Do not open the database in file-touched", "warns")]
    [InlineData("Don't widen NoWarn", "warns")]
    [InlineData("dont widen NoWarn", "warns")]
    [InlineData("Don’t widen NoWarn", "warns")]
    [InlineData("Do-not widen NoWarn", "warns")]
    [InlineData("You must not push to main", "warns")]
    [InlineData("This is a trap", "warns")]
    [InlineData("GOTCHA: pooling hides the extension", "warns")]
    [InlineData("Renaming it breaks the build", "warns")]
    [InlineData("We chose WAL mode", "decided")]
    [InlineData("We decided on BEGIN IMMEDIATE", "decided")]
    [InlineData("The envelope was rejected", "decided")]
    [InlineData("We chose never to retry", "warns")]
    public void Predicate_FollowsTheCueOrder(string statement, string expected) =>
        Assert.Equal(expected, ProjectNotes.PredicateFor(statement));

    [Theory]
    [InlineData("Neverland is a fixture name")]
    [InlineData("The breakset is computed lazily")]
    [InlineData("Breakfast runs at eight")]
    [InlineData("A choosy parser")]
    [InlineData("The decider runs first")]
    [InlineData("We kept WAL mode")]
    [InlineData("Donut charts are drawn in SVG")]
    public void Predicate_WithoutAWholeWordCue_IsHolds(string statement) =>
        Assert.Equal("holds", ProjectNotes.PredicateFor(statement));

    [Fact]
    public void UnresolvedCodes_AreTheClosedSet()
    {
        Assert.Equal(
            ["no-dir", "bad-dir", "not-checkout", "git-unknown", "not-indexed"],
            Enum.GetValues<ProjectUnresolved>().Select(ProjectNotes.Code));
    }

    [Fact]
    public void WritableScopes_AreSessionProjectAgent_FromTheRegistry()
    {
        Assert.Equal([MemoryScopes.Session, MemoryScopes.Project, MemoryScopes.Agent], MemoryScopes.Writable);
        Assert.All(MemoryScopes.Writable, scope => Assert.Contains(scope, MemoryScopes.All));
    }
}
