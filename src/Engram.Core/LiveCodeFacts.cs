using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// The one definition of "facts whose subject is this file or a symbol or section inside it".
/// </summary>
/// <remarks>
/// A file's entity path is the prefix of everything extracted from it, joined by <c>#</c>, so the
/// match is the exact path or the path followed immediately by <c>#</c> — a bare prefix test would
/// also claim <c>a.cs</c>'s neighbour <c>a.csx</c>. The indexer reconciles against this set and the
/// mod API's <c>path-facts</c> reads it, and two copies would disagree about a file's contents the
/// first time one is tuned.
/// </remarks>
public static class LiveCodeFacts
{
    /// <summary>A SQL predicate over <c>fact.path</c>; bind it with <see cref="BindFile"/>.</summary>
    /// <remarks>
    /// "Starts with <c>$p#</c>" is written as the half-open range <c>[$p#, $p$)</c>, <c>$</c> being
    /// the code point after <c>#</c>, so SQLite answers both arms from <c>ix_fact_path</c> as a
    /// MULTI-INDEX OR. The <c>substr</c> form it replaces scanned every live fact once per file:
    /// 0.10 s a file, 95–99% of a full index run (D95). The range is exact only because
    /// <c>fact.path</c> declares no collation and so compares BINARY, byte by byte over UTF-8; a
    /// <c>COLLATE</c> on the column or here would change what the bounds admit.
    /// </remarks>
    public const string UnderFilePredicate =
        "(fact.path = $p OR (fact.path >= $p || '#' AND fact.path < $p || '$'))";

    public static void BindFile(SqliteCommand command, string filePath) =>
        command.Parameters.AddWithValue("$p", filePath);
}
