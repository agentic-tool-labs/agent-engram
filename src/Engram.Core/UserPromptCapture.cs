namespace Engram.Core;

/// <summary>
/// Stores what the user stated in a prompt and says what to tell the model about it.
/// </summary>
/// <remarks>
/// The only place every message the user sends passes through. Everything else depends on the model
/// choosing to call <c>engram_remember</c>, so a fact stated in passing is lost unless something that
/// is not the model writes it down. The caller decides the prompt is the person's own: the capture mod
/// reads the engine's origin stamp (D83).
/// </remarks>
public static class UserPromptCapture
{
    /// <returns>Context for the model about what was stored, or null when nothing was.</returns>
    public static string? Run(EngramHome home, string prompt, string sessionId)
    {
        var candidates = UserStatementClassifier.Classify(prompt);
        if (candidates.Count == 0)
        {
            return null;
        }

        var stored = new List<string>(candidates.Count);

        try
        {
            using var connection = EngramDatabase.OpenInitialized(home);
            var now = DateTimeOffset.UtcNow;

            foreach (var candidate in candidates)
            {
                var topic = candidate.Kind == UserFactKind.Instruction
                    ? UserFactTopic.Instruction
                    : UserFactTopic.AboutYou;

                // Null means the store already holds this statement, so there is nothing new
                // to announce. Listing it anyway would ask the model to rewrite a capture
                // that may already have been rewritten.
                if (UserFacts.Capture(connection, topic, candidate.Text, sessionId, now) is { } factId)
                {
                    stored.Add($"[{FactCatalog.HandleFor(factId)}] {candidate.Text}");
                }
            }
        }
        catch
        {
            // A capture that cannot be written is not worth failing a prompt over. Whatever
            // landed before the failure is still reported: a partial capture the model can
            // improve beats discarding the ones that succeeded.
        }

        if (stored.Count == 0)
        {
            return null;
        }

        // Recorded here rather than above the guard, so the event means a fact was written. Every
        // prompt reaches this and most carry nothing to capture; announcing those would make the kind
        // a proxy for "the user typed". Never mod-call, which counts calls a mod chose to make (D56, D76).
        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTimeOffset.UtcNow.ToString("o"),
            SessionId: sessionId,
            Kind: TelemetryEventKind.UserPrompt));

        return "Engram captured what the user just stated, verbatim, as durable user-scoped memory:\n"
            + string.Join('\n', stored)
            + "\n\nIf any of those would not stand on its own when read months from now — a relative "
            + "date like \"last Saturday\", an unresolved \"it\" or \"that\" — call engram_remember with "
            + "a rewritten, self-contained version and pass the bracketed id as `supersedes` so the raw "
            + "capture is closed rather than duplicated. If a capture already reads fine on its own, do "
            + "nothing. Do not mention any of this to the user or thank them for the information; it is "
            + "bookkeeping, not a topic.";
    }
}
