using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// A code fact's gist says nothing about which file it describes, so the recall line carries
/// <c>&lt;repo&gt;:&lt;rel&gt;</c> — and because the estimate is taken on the finished line, the
/// budget pays for it.
/// </summary>
public class RecallCodeLocationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static void WriteCode(SqliteConnection connection, string path, string body) =>
        FactStore.Remember(
            connection,
            new FactWrite(path, "symbol", "declared-as", body, "code", "observed", Regenerable: true),
            T0);

    private static RecallPackResult Pack(SqliteConnection connection, string query, int budget) =>
        RecallRanker.Pack(
            connection, query, budget, RetrievalSettings.DefaultSeedK,
            currentSessionId: null, T0, VectorLaneQuery.Stopped(VectorLaneState.Off, "off"));

    [Fact]
    public void Pack_CodeFact_LineCarriesLocation()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        WriteCode(connection, "/projects/p/code/r/src/kestrel.cs#Bind", "Bind(port) — kestrel listener binder.");

        var text = Pack(connection, "kestrel listener binder", RetrievalSettings.DefaultBudgetTokens).Text;

        Assert.Contains("(code · r:src/kestrel.cs · 0d", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_CodeFactsWithLongPaths_NeverExceedBudget()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        for (var i = 0; i < 40; i++)
        {
            WriteCode(
                connection,
                $"/projects/p/code/some-long-repo-name/src/main/java/com/acme/billing/invoice/module{i}/InvoiceServiceImplementation{i}.java#Run",
                $"Run{i}() — kestrel invoice runner number {i}.");
        }

        foreach (var budget in new[] { 250, 500 })
        {
            var result = Pack(connection, "kestrel invoice runner", budget);

            Assert.Contains("some-long-repo-name:", result.Text, StringComparison.Ordinal);
            // The header and footer are framing the budget has never charged; the lines are what it packs.
            var lines = result.Text.Split('\n').Where(l => l.StartsWith("[f", StringComparison.Ordinal)).ToList();
            var spent = lines.Sum(TokenEstimator.Estimate);

            Assert.NotEmpty(lines);
            Assert.True(spent <= budget, $"budget {budget}: {lines.Count} lines cost {spent}");
        }
    }
}
