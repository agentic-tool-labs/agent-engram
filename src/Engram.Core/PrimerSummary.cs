using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// The three things a primer is built from: how many long-term facts are believed, how they
/// break down by topic, and enough of the catalog's front to pick examples from.
/// </summary>
/// <remarks>
/// <para>These are raw aggregates, deliberately unordered and unselected. Every ordering and
/// selection rule — count-descending then key-ordinal, <c>MaxClusters</c>, the <c>+N more</c>
/// tail, the preferred scope order, the fill-from-the-front — stays in
/// <see cref="PrimerBuilder"/>, because those rules must have exactly one implementation. A
/// summary that arrived pre-sorted would move them into the reader and give the in-memory path
/// and the SQL path a rule each to disagree about.</para>
///
/// <para><see cref="From"/> and <see cref="Read"/> are required to produce primers that are
/// equal byte for byte; <c>PrimerSummaryEquivalenceTests</c> is what holds that.</para>
/// </remarks>
/// <param name="FactCount">Live non-session facts in the store.</param>
/// <param name="TopicCounts">Topic display name to the number of live facts under it.</param>
/// <param name="ExampleCandidates">
/// A superset of everything <see cref="PrimerBuilder"/> can choose as an example, in catalog
/// order (ascending fact id).
/// </param>
/// <param name="Directives">
/// Live directives (<see cref="DirectiveFacts"/>), oldest first — delivered in full and
/// unconditionally, never selected from or dropped the way <paramref name="ExampleCandidates"/>
/// is (D-1).
/// </param>
/// <param name="ReviewDueCount">
/// Live facts whose <c>fact_review</c> date has passed (docs/memory-expansion/04-lifecycle-spec.md).
/// Reuses this record's one-query read path rather than a new hook or query (D46); consumed by
/// <c>doctor</c>, not rendered into the primer text itself.
/// </param>
/// <param name="Role">
/// The role key a primer is built for (the hook payload's <c>agent_type</c>), or null when the
/// payload carried none (D100).
/// </param>
/// <param name="RoleFacts">
/// Up to <see cref="RoleNotes.MaxDelivered"/> live role facts in delivery order; empty without a
/// role.
/// </param>
public sealed record PrimerSummary(
    int FactCount,
    IReadOnlyDictionary<string, int> TopicCounts,
    IReadOnlyList<CannedFact> ExampleCandidates,
    IReadOnlyList<string> Directives,
    int ReviewDueCount = 0,
    string? Role = null,
    IReadOnlyList<RoleFact>? RoleFacts = null)
{
    /// <summary>
    /// Counts a catalog already in memory — the shape every existing caller and test hands over.
    /// </summary>
    public static PrimerSummary From(IReadOnlyList<CannedFact> facts, string? role = null)
    {
        // Role memory reaches only its role's block (D100), as Read's exclusion does.
        var rolePrefix = RoleNotes.Root + "/";
        var counted = facts
            .Where(fact => fact.SubjectPath is not { } path || !path.StartsWith(rolePrefix, StringComparison.Ordinal))
            .ToList();
        var topicCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var fact in counted)
        {
            topicCounts[fact.Topic] = topicCounts.GetValueOrDefault(fact.Topic) + 1;
        }

        return new PrimerSummary(
            counted.Count, topicCounts, counted, Directives: [],
            Role: RoleNotes.IsAddressable(role) ? role : null,
            RoleFacts: RoleNotes.IsAddressable(role) ? RoleNotes.SelectDelivered(facts, role!) : []);
    }

    /// <summary>
    /// Reads the same aggregates from the store without materialising the corpus.
    /// </summary>
    /// <remarks>
    /// Three statements, none of which is O(facts) in transferred bytes: a histogram over
    /// distinct subject paths, one row per topic entity, and at most a handful of candidate
    /// facts. The histogram groups by path rather than by topic because
    /// <see cref="FactCatalog.TopicOf"/> is the one implementation of what a topic is —
    /// its segment splitting and topic-node resolution would diverge from a SQL copy the
    /// first time either side was tuned.
    /// </remarks>
    public static PrimerSummary Read(SqliteConnection connection, DateTimeOffset now, string? role = null)
    {
        var topicNames = FactStore.ReadEntityNames(connection, CannedFactSeeder.TopicKind);
        var sessionPrefix = SessionFacts.Root + "/";
        var directivePrefix = DirectiveFacts.Root + "/";

        var factCount = 0;
        var topicCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = TopicHistogramSql;
            BindExclusions(command, sessionPrefix, directivePrefix);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var count = reader.GetInt32(1);
                var topic = FactCatalog.TopicOf(reader.GetString(0), topicNames);
                topicCounts[topic] = topicCounts.GetValueOrDefault(topic) + count;
                factCount += count;
            }
        }

        var candidates = new List<CannedFact>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = ExampleCandidatesSql;
            BindExclusions(command, sessionPrefix, directivePrefix);
            command.Parameters.AddWithValue("$limit", PrimerBuilder.MaxExampleFacts);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add(FactCatalog.ToCannedFact(
                    FactStore.ReadStoredFact(reader), now, topicNames, reader.GetInt32(16)));
            }
        }

        var directives = DirectiveFacts.ReadLive(connection).Select(d => d.Body).ToList();
        var reviewDueCount = FactReview.CountDue(connection, now.ToUnixTimeSeconds());

        // The one extra read, an indexed path range, and only when the payload named a role.
        var roleFacts = RoleNotes.IsAddressable(role) ? RoleNotes.ReadDelivered(connection, role!) : [];

        return new PrimerSummary(
            factCount, topicCounts, candidates, directives, reviewDueCount,
            Role: RoleNotes.IsAddressable(role) ? role : null,
            RoleFacts: roleFacts);
    }

    /// <remarks>
    /// <c>substr</c> rather than <c>LIKE</c>: the prefix is a constant today but need not stay
    /// one, and <c>LIKE</c> would give <c>_</c> and <c>%</c> wildcard meaning inside a value.
    /// <c>entity.path</c> declares no collation, so SQLite compares it BINARY — which is what
    /// the <see cref="StringComparison.Ordinal"/> exclusion in
    /// <see cref="FactCatalog.ReadLongTerm(SqliteConnection, DateTimeOffset)"/> does. That
    /// equivalence is required; do not add a COLLATE. Directives are excluded here too, by the
    /// same mechanism rather than a second one beside it: they are their own delivery channel
    /// (D-1), so they must not also inflate <see cref="PrimerSummary.FactCount"/>,
    /// <see cref="PrimerSummary.TopicCounts"/>, or appear a second time as an "Examples:" line.
    /// Role memory likewise: it reaches only its own role's block (D100), never a count or example.
    /// </remarks>
    private static void BindExclusions(SqliteCommand command, string sessionPrefix, string directivePrefix)
    {
        command.Parameters.AddWithValue("$plen", sessionPrefix.Length);
        command.Parameters.AddWithValue("$prefix", sessionPrefix);
        command.Parameters.AddWithValue("$dplen", directivePrefix.Length);
        command.Parameters.AddWithValue("$dprefix", directivePrefix);
        command.Parameters.AddWithValue("$aplen", RoleNotes.Root.Length + 1);
        command.Parameters.AddWithValue("$aprefix", RoleNotes.Root + "/");
        command.Parameters.AddWithValue("$tool", FactStore.ToolScope);
    }

    // Counts per subject_id over the two live partial indexes first, then joins entity once per
    // subject rather than once per live fact. ux_fact_live and ux_fact_edge_live partition the
    // live facts exactly (object_id IS NULL / IS NOT NULL), so the two arms together count every
    // live fact once; a subject holding both kinds appears in both arms, which is what the outer
    // SUM merges. entity.path is UNIQUE, so grouping by subject_id and by e.path give identical
    // groups — the same argument ExampleCandidatesSql makes for its version count. Measured on a
    // 621,904-live-fact snapshot: 305 ms for the per-fact join grouped on path, 85 ms for this,
    // identical 184,659 rows by EXCEPT both ways.
    //
    // Tool-scope facts (D87) are taken out by a third, negative arm rather than a scope filter on
    // the first two: those arms are answered from the partial indexes alone, and a `scope` test
    // there would read the table once per live fact. The negative arm reads only the tool rows,
    // through ix_fact_scope, and HAVING drops a subject whose every live fact was a tool record —
    // with no tool rows it yields nothing, so the result is unchanged.
    private const string TopicHistogramSql =
        """
        WITH c(sid, n) AS (
          SELECT subject_id, SUM(n)
            FROM (SELECT subject_id, COUNT(*) AS n
                    FROM fact INDEXED BY ux_fact_live
                   WHERE valid_to IS NULL AND object_id IS NULL
                   GROUP BY subject_id
                  UNION ALL
                  SELECT subject_id, COUNT(*) AS n
                    FROM fact INDEXED BY ux_fact_edge_live
                   WHERE valid_to IS NULL AND object_id IS NOT NULL
                   GROUP BY subject_id
                  UNION ALL
                  SELECT subject_id, -COUNT(*) AS n
                    FROM fact INDEXED BY ix_fact_scope
                   WHERE valid_to IS NULL AND scope = $tool
                   GROUP BY subject_id)
           GROUP BY subject_id
          HAVING SUM(n) > 0)
        SELECT e.path, c.n
          FROM c
          JOIN entity e ON e.id = c.sid
         WHERE substr(e.path, 1, $plen) <> $prefix
           AND substr(e.path, 1, $dplen) <> $dprefix
           AND substr(e.path, 1, $aplen) <> $aprefix;
        """;

    // The lowest-id live non-session fact per scope, unioned with the first $limit live
    // non-session facts overall. That superset is provably everything PrimerBuilder.TopFacts can
    // reach, for any $limit rather than just for two. It takes one scope-first per distinct
    // scope, in preferred order, and a scope-first is that scope's lowest id because catalog
    // order is `ORDER BY f.id` — so the first arm covers step one whole. It reaches step two only
    // when fewer than $limit scopes exist, and then fills from the front skipping what it already
    // holds; holding k of them, it needs $limit - k more and in the worst case they sit at
    // positions k+1 through $limit, so the front $limit rows are exactly enough and never more.
    //
    // The version count is keyed on subject_id rather than on e.path, which does NOT contradict
    // D57. D57 is about addressing — the catalog-wide FactStore.VersionCounts groups by path so
    // that the number it advertises describes the same thread History will walk, and History
    // addresses a thread by path. entity.path is UNIQUE, so path and id are bijective and the
    // two groupings return identical numbers; here the row already carries both, and subject_id
    // is what ix_fact_thread indexes. Do not "fix" this back to a path join.
    //
    // The first arm finds each scope-first without grouping every live fact. `scopes` walks the
    // distinct live scopes as a skip over ix_fact_scope — one seek per scope, and live scopes are
    // few — and for each one the probe walks that scope's live facts in id order (ix_fact_scope
    // entries are ordered by rowid within a scope) and stops at the first that passes the
    // exclusions: exactly MIN(id) over that scope's qualifying facts, so the proof above is
    // unchanged. A scope with no qualifying fact yields NULL, which IN never matches, where the
    // grouped form produced no row; the selected set is the same. Measured on a 621,904-live-fact
    // snapshot: 141 ms grouped, 10 ms probed — nearly all of it the 'session' scope, whose live
    // facts all sit under the session prefix, so its probe walks them all and finds none. The
    // second arm is unchanged: it already stops after $limit qualifying rows (0.07 ms there).
    //
    // SQLite rejects ORDER BY ... LIMIT directly inside a compound-SELECT operand, which is why
    // the second arm is wrapped in a subquery.
    private const string ExampleCandidatesSql =
        $"""
        WITH RECURSIVE scopes(s) AS (
          SELECT (SELECT MIN(scope) FROM fact INDEXED BY ix_fact_scope WHERE valid_to IS NULL)
          UNION ALL
          SELECT (SELECT MIN(scope) FROM fact INDEXED BY ix_fact_scope
                   WHERE valid_to IS NULL AND scope > s)
            FROM scopes
           WHERE s IS NOT NULL)
        SELECT {FactStore.FactColumns},
               (SELECT COUNT(*) FROM fact fv
                 WHERE fv.subject_id = f.subject_id AND fv.predicate = f.predicate)
          FROM fact f
          JOIN entity e ON e.id = f.subject_id
         WHERE f.id IN (SELECT (SELECT sf.id
                                  FROM fact sf INDEXED BY ix_fact_scope
                                  JOIN entity se ON se.id = sf.subject_id
                                 WHERE sf.scope = s AND sf.valid_to IS NULL
                                   AND substr(se.path, 1, $plen) <> $prefix
                                   AND substr(se.path, 1, $dplen) <> $dprefix
                                   AND substr(se.path, 1, $aplen) <> $aprefix
                                 ORDER BY sf.id
                                 LIMIT 1)
                          FROM scopes
                         WHERE s IS NOT NULL AND s <> $tool
                        UNION
                        SELECT id
                          FROM (SELECT nf.id
                                  FROM fact nf
                                  JOIN entity ne ON ne.id = nf.subject_id
                                 WHERE nf.valid_to IS NULL
                                   AND nf.scope <> $tool
                                   AND substr(ne.path, 1, $plen) <> $prefix
                                   AND substr(ne.path, 1, $dplen) <> $dprefix
                                   AND substr(ne.path, 1, $aplen) <> $aprefix
                                 ORDER BY nf.id
                                 LIMIT $limit))
         ORDER BY f.id;
        """;
}
