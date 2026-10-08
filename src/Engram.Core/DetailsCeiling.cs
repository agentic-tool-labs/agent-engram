namespace Engram.Core;

/// <summary>
/// The hard cap on a fact's <c>details</c> (D64), checked by every writer that accepts one.
/// </summary>
public static class DetailsCeiling
{
    public const int MaxTokens = 2000;

    /// <summary>The refusal text for details over the cap, or null when they fit or are absent.</summary>
    public static string? Error(string? details)
    {
        if (details is null)
        {
            return null;
        }

        var tokens = TokenEstimator.Estimate(details);
        return tokens > MaxTokens
            ? $"details is ~{tokens} tokens against the 2,000-token ceiling — a memory that large is a "
                + "document; store where to find it (evidence, a path) rather than its contents. Nothing "
                + "was stored."
            : null;
    }
}
