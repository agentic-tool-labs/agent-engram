namespace Engram.Core;

/// <summary>
/// One recall run, from the settings it reads to the digest it returns — shared by the MCP tool
/// and the mod API so neither grows a private copy of the sequence.
/// </summary>
/// <remarks>
/// Each caller keeps its own telemetry kind: this writes nothing, because what a recall means in
/// the log depends on who asked.
/// </remarks>
public static class RecallSearch
{
    /// <param name="budgetTokens">The caller's budget; not positive or null means the configured default.</param>
    /// <param name="sessionExternalId">The host session whose working-memory notes join the ranking.</param>
    /// <param name="pinnedFactIds">Facts the caller has pinned, or null when it has no pins.</param>
    public static RecallPackDetail Run(
        EngramHome home,
        LocalRuntime local,
        string query,
        int? budgetTokens,
        string sessionExternalId,
        IReadOnlySet<long>? pinnedFactIds)
    {
        var config = ConfigFile.Load(home.ConfigPath);
        var settings = RetrievalSettings.Read(config);
        var budget = budgetTokens is > 0 ? budgetTokens.Value : settings.BudgetTokens;

        // One connection, one temporal model, one statement: SQLite ranks and bounds every tier —
        // long-term, current session, prior session — from a single atomic read (D59). Nothing
        // O(corpus) crosses into C# in either direction.
        var now = DateTimeOffset.UtcNow;
        using var connection = EngramDatabase.OpenInitialized(home);

        var currentSessionId = SessionStore.FindSession(connection, sessionExternalId);

        // The same lane `explain` reports, so what it describes is what ran here. It costs nothing
        // when embeddings are off — the factory refuses before any request — and it can never fail
        // this call: every way it can stop comes back as a reason and no embedding, leaving recall
        // exactly as lexical as it was before. The search itself happens inside the ranking
        // statement (RecallRanker), so only the embedding — not a result set — crosses back into C#.
        var vectorQuery = VectorLane.PrepareQuery(
            connection, home, EmbeddingSettings.Read(config), query, Environment.GetEnvironmentVariable, local);

        return RecallRanker.PackWithOutcome(
            connection, query, budget, settings.SeedK, currentSessionId, now, vectorQuery, pinnedFactIds);
    }
}
