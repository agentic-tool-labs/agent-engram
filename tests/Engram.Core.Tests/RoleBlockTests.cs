using Engram.Core;

namespace Engram.Core.Tests;

/// <summary>The role block's budget behaviour (D100).</summary>
public class RoleBlockTests
{
    private static PrimerSummary WithRole(params string[] facts) =>
        PrimerSummary.From([]) with { Role = "wrangl:architect", RoleFacts = facts };

    [Fact]
    public void RoleFactsBeyondTheBudget_AreDropped_AndThePrecedenceLineStays()
    {
        var huge = new string('x', 2000);

        var primer = PrimerBuilder.BuildForSubagent(WithRole(huge, "Short lesson."), MemoryPrecedence.EngramFirst, out var delivered);

        Assert.Contains(MemorySettings.PrimerLine(MemoryPrecedence.EngramFirst)!, primer, StringComparison.Ordinal);
        Assert.DoesNotContain(huge, primer, StringComparison.Ordinal);
        Assert.Contains("- Short lesson.", primer, StringComparison.Ordinal);
        Assert.Equal(1, delivered);
    }

    [Fact]
    public void WhenNoRoleFactFits_TheWholeBlockIsOmitted()
    {
        var huge = new string('x', 2000);

        var primer = PrimerBuilder.BuildForSubagent(WithRole(huge), MemoryPrecedence.EngramFirst, out var delivered);

        Assert.Equal(0, delivered);
        Assert.Equal(PrimerBuilder.BuildForSubagent(PrimerSummary.From([]), MemoryPrecedence.EngramFirst), primer);
    }

    [Fact]
    public void TheBlockFollowsThePrecedenceLine_InBothPrimers()
    {
        var summary = WithRole("Short lesson.");
        var line = MemorySettings.PrimerLine(MemoryPrecedence.EngramFirst)!;

        foreach (var primer in new[]
                 {
                     PrimerBuilder.BuildForSubagent(summary, MemoryPrecedence.EngramFirst),
                     PrimerBuilder.Build(summary, MemoryPrecedence.EngramFirst),
                 })
        {
            Assert.True(
                primer.IndexOf(line, StringComparison.Ordinal) < primer.IndexOf("Role memory for wrangl:architect:", StringComparison.Ordinal),
                primer);
            Assert.Contains("Role memory for wrangl:architect:\nYour role memory key: wrangl:architect.\n- Short lesson.", primer, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheKeyLine_NamesNoTool()
    {
        var primer = PrimerBuilder.BuildForSubagent(WithRole("Short lesson."), MemoryPrecedence.Off);

        var block = primer[primer.IndexOf("Role memory for", StringComparison.Ordinal)..];
        Assert.DoesNotContain("engram_", block, StringComparison.Ordinal);
    }
}
