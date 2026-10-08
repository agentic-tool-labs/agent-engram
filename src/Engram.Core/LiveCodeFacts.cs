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
    public const string UnderFilePredicate =
        "(fact.path = $p OR (substr(fact.path, 1, $len) = $p AND substr(fact.path, $len + 1, 1) = '#'))";

    public static void BindFile(SqliteCommand command, string filePath)
    {
        command.Parameters.AddWithValue("$p", filePath);
        command.Parameters.AddWithValue("$len", filePath.Length);
    }
}
