using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// The test-only digest of authored truth the backup spec's I1 names: every row that nothing
/// regenerates, with fact ids replaced by their key tuple (subject path, predicate, body,
/// valid_from) so a store rebuilt by replay, where every id differs, still compares equal.
/// </summary>
/// <remarks>
/// Kept as rows per part rather than one hash, so a failure names the table and the row that
/// moved instead of two unequal hex strings.
/// </remarks>
internal static class AuthoredDigest
{
    private static string KeyOf(string idColumn) =>
        $"(SELECT json_array(ke.path, k.predicate, k.body, k.valid_from) FROM fact k "
        + $"JOIN entity ke ON ke.id = k.subject_id WHERE k.id = {idColumn})";

    private static string FactColumns(bool journalSubset) =>
        journalSubset
            // What a journal line carries: no denormalized path, no analyzer tier.
            ? $"se.path, se.kind, f.predicate, f.body, oe.path, oe.kind, f.scope, f.learned_via, "
              + $"f.regenerable, f.evidence, f.valid_from, f.valid_to, {KeyOf("f.superseded_by")}, f.created_at, f.details"
            : $"se.path, se.kind, f.predicate, f.body, oe.path, oe.kind, f.path, f.scope, f.learned_via, "
              + $"f.regenerable, f.evidence, f.valid_from, f.valid_to, {KeyOf("f.superseded_by")}, f.created_at, "
              + "f.details, f.analyzer_tier";

    private static string Facts(string where, bool journalSubset) =>
        $"SELECT json_array({FactColumns(journalSubset)}) FROM fact f "
        + "JOIN entity se ON se.id = f.subject_id LEFT JOIN entity oe ON oe.id = f.object_id "
        + $"WHERE {where}";

    /// <summary>Every part, by name. <paramref name="journalSubset"/> keeps only what a journal carries.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Read(SqliteConnection connection, bool journalSubset = false)
    {
        var parts = new Dictionary<string, string>
        {
            ["facts"] = Facts("f.regenerable = 0", journalSubset),
            ["supersession"] = journalSubset
                // Replay links a supersession only through superseded_by, so a forget row (no
                // new fact) is not rebuilt by any replay, full or authored: a pre-existing limit of
                // replay, outside this item, and kept out of the journalled subset rather than hidden.
                ? $"SELECT json_array({KeyOf("s.old_fact_id")}, {KeyOf("s.new_fact_id")}, s.reason) FROM supersession s "
                  + "WHERE s.new_fact_id IS NOT NULL "
                  + "AND EXISTS (SELECT 1 FROM fact x WHERE x.id IN (s.old_fact_id, s.new_fact_id) AND x.regenerable = 0)"
                : $"SELECT json_array({KeyOf("s.old_fact_id")}, {KeyOf("s.new_fact_id")}, s.reason, s.evidence, s.created_at) "
                  + "FROM supersession s "
                  + "WHERE EXISTS (SELECT 1 FROM fact x WHERE x.id IN (s.old_fact_id, s.new_fact_id) AND x.regenerable = 0)",
            ["relations"] =
                $"SELECT json_array({KeyOf("r.fact_id")}, {KeyOf("r.related_id")}, r.relation, r.reason, r.judged_at) FROM fact_relation r",
            ["reviews"] = $"SELECT json_array({KeyOf("v.fact_id")}, v.review_after, v.set_at) FROM fact_review v",
            ["sync"] = $"SELECT json_array({KeyOf("q.fact_id")}, q.requested_at) FROM fact_sync_request q",
        };

        if (!journalSubset)
        {
            parts["enrollment"] =
                "SELECT json_array(identity, state, source, last_root, decided_at, last_full_scan_at) FROM repo_enrollment";
            parts["aliases"] =
                "SELECT json_array(e.path, a.alias, a.kind, a.created_at) FROM entity_alias a JOIN entity e ON e.id = a.entity_id";
            parts["sessions"] =
                "SELECT json_array(id, external_id, host, repo_path, started_at, ended_at, digest) FROM session";
            parts["liveRegenerable"] = Facts("f.regenerable = 1 AND f.valid_to IS NULL", journalSubset: false);
            parts["registry"] =
                "SELECT json_array(repo_path, identity, disk_path, detached_at, created_at, last_scan_suppressed_reason) FROM repo_registry";
            parts["fileState"] =
                "SELECT json_array(repo_path, path, blob_sha, lang, indexed_at) FROM file_state";
        }

        var result = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (name, sql) in parts)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql + ";";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                rows.Add(reader.GetString(0));
            }

            rows.Sort(StringComparer.Ordinal);
            result[name] = rows;
        }

        return result;
    }

    /// <summary>One SHA-256 over every part, for reports.</summary>
    public static string Hash(IReadOnlyDictionary<string, IReadOnlyList<string>> digest) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', digest.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key + "\n" + string.Join('\n', p.Value))))));

    public static void Equal(
        IReadOnlyDictionary<string, IReadOnlyList<string>> expected,
        IReadOnlyDictionary<string, IReadOnlyList<string>> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, rows) in expected)
        {
            Assert.True(rows.SequenceEqual(actual[name]), $"part '{name}' differs: expected {rows.Count} rows, got {actual[name].Count}");
        }
    }
}
