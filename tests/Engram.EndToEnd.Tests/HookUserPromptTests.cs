using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// UserPromptSubmit is the only place a fact the user states in passing can be caught,
/// so these drive the published binary rather than the JIT build — a classifier that
/// works under test and not in the shipped AOT binary would fail silently and forever.
/// </summary>
public class HookUserPromptTests
{
    // The case the feature exists for: no memory keyword anywhere in the sentence.
    [Fact]
    public void CapturesAPersonalStatementAndAsksTheModelToDateIt()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.RunWithStdin(
            home.Root,
            Payload("I went to see a Spiderman movie last Saturday", TypedTranscript(home.Root, "I went to see a Spiderman movie last Saturday")),
            "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        var statement = Assert.Single(ReadCapturedStatements(home.Root));
        Assert.Equal("I went to see a Spiderman movie last Saturday", statement);

        // Bare stdout is discarded on some hook events; the envelope is what actually
        // reaches the model, and its hookEventName has to match the event that produced it.
        var output = JsonNode.Parse(stdout)!;
        var hookOutput = output["hookSpecificOutput"]!;
        Assert.Equal("UserPromptSubmit", hookOutput["hookEventName"]!.GetValue<string>());

        var context = hookOutput["additionalContext"]!.GetValue<string>();
        Assert.Contains("Spiderman", context);
        Assert.Contains("supersedes", context);
    }

    [Fact]
    public void SaysAndStoresNothingForAnOrdinaryWorkingPrompt()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.RunWithStdin(
            home.Root,
            Payload("run the tests and tell me what fails", TypedTranscript(home.Root, "run the tests and tell me what fails")),
            "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
        Assert.Empty(ReadCapturedStatements(home.Root));
    }

    // Sentence granularity is a privacy property, not only a precision one: the working
    // half of a mixed message must never reach disk.
    [Fact]
    public void StoresOnlyTheStatedSentenceFromAMixedMessage()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        EngramProcess.RunWithStdin(
            home.Root,
            Payload("I moved to Seattle in March. Now fix the failing test and push it.", TypedTranscript(home.Root, "I moved to Seattle in March. Now fix the failing test and push it.")),
            "hook", "user-prompt");

        var statement = Assert.Single(ReadCapturedStatements(home.Root));
        Assert.Equal("I moved to Seattle in March.", statement);
        Assert.DoesNotContain("failing test", statement);
    }

    // Same rule as every other hook: an uninitialised home means do nothing, quietly.
    [Fact]
    public void WritesNothingWhenTheHomeWasNeverInitialised()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome(initialize: false);

        var (exitCode, stdout, _) = EngramProcess.RunWithStdin(
            home.Root,
            Payload("I grew up in Fort Collins, Colorado", TypedTranscript(home.Root, "I grew up in Fort Collins, Colorado")),
            "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.False(File.Exists(Path.Combine(home.Root, "engram.db")));
    }

    // The hook writes to the database now, and a store already holding this statement is
    // reason to say nothing rather than to write a second copy. Driving it through two real
    // processes is what makes this meaningful: the check is a query, not in-process state.
    [Fact]
    public void RepeatingAStatementCapturesItOnceAndSaysNothingTheSecondTime()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var transcript = WriteTranscript(
            home.Root,
            UserRecord("typed", "I use a Dvorak keyboard"),
            UserRecord("typed", "I use a Dvorak keyboard."));

        var (_, firstStdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload("I use a Dvorak keyboard", transcript), "hook", "user-prompt");
        var (secondExit, secondStdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload("I use a Dvorak keyboard.", transcript), "hook", "user-prompt");

        Assert.Contains("Dvorak", firstStdout);
        Assert.Equal(0, secondExit);
        Assert.Equal(string.Empty, secondStdout);

        Assert.Single(ReadCapturedStatements(home.Root));
    }

    // The bug this whole file exists to guard against: a cross-session peer message reads as
    // ordinary first-person prose to the classifier, exactly like a real statement would.
    // Ground-truthed against this project's own transcript, 2026-08-09/10 — every real
    // mis-capture carried promptSource "system", every real capture "typed".
    [Fact]
    public void DoesNotCaptureACrossSessionPeerMessage()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        const string prompt = "Another Claude session sent a message: I decided to use SQLite for storage.";
        var transcript = SystemTranscript(home.Root, prompt, origin: "peer");

        var (exitCode, stdout, _) = EngramProcess.RunWithStdin(
            home.Root,
            Payload(prompt, transcript),
            "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Empty(ReadCapturedStatements(home.Root));
    }

    [Fact]
    public void DoesNotCaptureABackgroundTaskNotification()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        const string prompt = "<task-notification>I found the bug in the parser.</task-notification>";
        var transcript = SystemTranscript(home.Root, prompt, origin: "task-notification");

        var (exitCode, stdout, _) = EngramProcess.RunWithStdin(
            home.Root,
            Payload(prompt, transcript),
            "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Empty(ReadCapturedStatements(home.Root));
    }

    // Provenance is undocumented and this hook's own stdin carries none of it (D62's
    // provenance fields live only on the transcript record) — a missing or unreadable
    // transcript_path must fail closed, the same "harvest nothing, never harvest garbage"
    // rule the PostCompact harvester follows.
    [Fact]
    public void DoesNotCaptureWhenTranscriptPathIsMissingFromThePayload()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var payload = JsonSerializer.Serialize(new JsonObject
        {
            ["session_id"] = "e2e-user-prompt",
            ["prompt"] = "I prefer tabs over spaces",
        });

        var (exitCode, stdout, _) = EngramProcess.RunWithStdin(home.Root, payload, "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Empty(ReadCapturedStatements(home.Root));
    }

    [Fact]
    public void DoesNotCaptureWhenTranscriptPathPointsToAMissingFile()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var missingPath = Path.Combine(home.Root, "no-such-transcript.jsonl");

        var (exitCode, stdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload("I prefer tabs over spaces", missingPath), "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Empty(ReadCapturedStatements(home.Root));
    }

    // The defect this guards: Claude Code writes attachment lines after the submission's user
    // record, at the same timestamp, before the hook runs. A provenance read that took the last
    // line saw an attachment, never found "typed", and captured nothing for two months.
    [Fact]
    public void CapturesWhenAttachmentsFollowTheTypedRecord()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        const string prompt = "I went to see a Spiderman movie last Saturday";
        var transcript = WriteTranscript(
            home.Root,
            [UserRecord("typed", "an earlier, unrelated message"), .. QueueOperations(2), UserRecord("typed", prompt), .. Attachments(10)]);

        var (exitCode, stdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload(prompt, transcript), "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Contains("Spiderman", stdout);
        Assert.Equal(prompt, Assert.Single(ReadCapturedStatements(home.Root)));
        Assert.Equal(1, CountUserPromptRecords(home.Root));
    }

    // A one-line transcript is the shape the old rule assumed; it must keep working.
    [Fact]
    public void CapturesWhenTheTypedRecordIsTheLastLine()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        const string prompt = "I prefer tabs over spaces";
        var transcript = WriteTranscript(home.Root, UserRecord("typed", prompt));

        var (_, stdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload(prompt, transcript), "hook", "user-prompt");

        Assert.Contains("tabs", stdout);
        Assert.Single(ReadCapturedStatements(home.Root));
    }

    // The guard against taking the newest typed record: the peer message's own record is the
    // newest promptSource line and says "system", while an earlier typed record says something
    // else. A rule that looked for the newest typed record would vouch for the peer message
    // with it. It does not guard the text binding itself — the peer record is the newest
    // promptSource line, so an unbound newest-line rule rejects it too; that is held by
    // DoesNotCaptureWhenNoRecordMatchesThePromptText.
    [Fact]
    public void DoesNotCaptureAPeerMessageAfterAnEarlierTypedPrompt()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        const string prompt = "Another Claude session sent a message: I decided to use SQLite for storage.";
        var transcript = WriteTranscript(
            home.Root,
            [UserRecord("typed", "an earlier typed message"), UserRecord("system", prompt, origin: "peer"), .. Attachments(3)]);

        var (exitCode, stdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload(prompt, transcript), "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Empty(ReadCapturedStatements(home.Root));
    }

    // Nearest inputs to "typed" that must not match: claude -p, and a value no one has seen.
    [Theory]
    [InlineData("sdk")]
    [InlineData("bridge")]
    [InlineData("Typed")]
    public void DoesNotCaptureAnyProvenanceOtherThanTyped(string promptSource)
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        const string prompt = "I prefer tabs over spaces";
        var transcript = WriteTranscript(home.Root, [UserRecord(promptSource, prompt), .. Attachments(2)]);

        var (_, stdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload(prompt, transcript), "hook", "user-prompt");

        Assert.Equal(string.Empty, stdout);
        Assert.Empty(ReadCapturedStatements(home.Root));
    }

    // The submission's record is not written yet; an earlier typed record with other text must
    // not stand in for it.
    [Fact]
    public void DoesNotCaptureWhenNoRecordMatchesThePromptText()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var transcript = WriteTranscript(
            home.Root, [UserRecord("typed", "I prefer spaces over tabs"), .. Attachments(2)]);

        var (_, stdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload("I prefer tabs over spaces", transcript), "hook", "user-prompt");

        Assert.Equal(string.Empty, stdout);
        Assert.Empty(ReadCapturedStatements(home.Root));
    }

    // A pasted block or file mention can make the stdin prompt differ from the recorded text.
    // One differing character in the middle is the nearest case; it fails closed.
    [Fact]
    public void DoesNotCaptureWhenTheTextDiffersInTheMiddle()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var transcript = WriteTranscript(home.Root, UserRecord("typed", "I prefer tabs over [Pasted text #1] spaces"));

        var (_, stdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload("I prefer tabs over spaces", transcript), "hook", "user-prompt");

        Assert.Equal(string.Empty, stdout);
        Assert.Empty(ReadCapturedStatements(home.Root));
    }

    [Fact]
    public void CapturesWhenTheContentIsBlocksWhoseTextBlocksJoinToThePrompt()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var transcript = WriteTranscript(
            home.Root,
            [
                UserRecord(
                    "typed",
                    new JsonArray
                    {
                        new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64" } },
                        new JsonObject { ["type"] = "text", ["text"] = "I prefer tabs " },
                        new JsonObject { ["type"] = "text", ["text"] = "over spaces" },
                    }),
                .. Attachments(2),
            ]);

        var (_, stdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload("I prefer tabs over spaces", transcript), "hook", "user-prompt");

        Assert.Contains("tabs", stdout);
        Assert.Single(ReadCapturedStatements(home.Root));
    }

    [Fact]
    public void CapturesWhenThePromptDiffersOnlyBySurroundingWhitespace()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var transcript = WriteTranscript(home.Root, UserRecord("typed", "  I prefer tabs over spaces\n"));

        var (_, stdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload("I prefer tabs over spaces\n\n", transcript), "hook", "user-prompt");

        Assert.Contains("tabs", stdout);
        Assert.Single(ReadCapturedStatements(home.Root));
    }

    [Fact]
    public void DoesNotThrowOnMalformedTranscriptLines()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var transcript = WriteTranscript(
            home.Root, "{\"promptSource\": \"typed\", truncated", "not json at all");

        var (exitCode, stdout, stderr) = EngramProcess.RunWithStdin(
            home.Root, Payload("I prefer tabs over spaces", transcript), "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
        Assert.Empty(ReadCapturedStatements(home.Root));
    }

    // The 16-candidate bound: the 16th-newest submission record still counts, the 17th does
    // not, which is what separates the bound from "none".
    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void StopsLookingAfterSixteenSubmissionRecords(int totalRecords, bool captured)
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        const string prompt = "I prefer tabs over spaces";
        var lines = new List<string> { UserRecord("typed", prompt) };
        for (var i = 1; i < totalRecords; i++)
        {
            lines.Add(UserRecord("typed", $"newer message {i}"));
        }

        var transcript = WriteTranscript(home.Root, lines.ToArray());

        var (_, stdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload(prompt, transcript), "hook", "user-prompt");

        Assert.Equal(captured, stdout.Contains("tabs"));
        Assert.Equal(captured ? 1 : 0, ReadCapturedStatements(home.Root).Count);
    }

    // A session's first prompt is followed by instructions and listings measured at up to ~361 KB,
    // which pushes its record out of the first 262,144-byte window. The second window finds it.
    [Fact]
    public void CapturesWhenTheTypedRecordStartsBeyondTheFirstWindow()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        const string prompt = "I prefer tabs over spaces";
        var transcript = WriteTranscript(
            home.Root, [UserRecord("typed", prompt), .. PaddedAttachments(30, 10_000)]);
        Assert.InRange(new FileInfo(transcript).Length, 300_000, 1_000_000);

        var (_, stdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload(prompt, transcript), "hook", "user-prompt");

        Assert.Contains("tabs", stdout);
        Assert.Single(ReadCapturedStatements(home.Root));
    }

    // The first window starts inside the typed record's line, so it begins with a fragment of
    // JSON. Parsing that fragment would fail and end the walk as "not typed"; discarding it
    // lets the walk escalate and find the whole record.
    [Fact]
    public void CapturesWhenTheTypedRecordStraddlesTheFirstWindow()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        const string prompt = "I prefer tabs over spaces";
        var typed = UserRecord("typed", prompt);
        var earlier = UserRecord("typed", "an earlier message");

        // The window starts one byte into the typed line, so what it holds of that line still
        // contains the promptSource key but is not valid JSON. The bytes after the line are
        // "\n" + trailer + "\n".
        var trailer = PaddedAttachment(262_143 - Encoding.UTF8.GetByteCount(typed));
        var transcript = WriteTranscript(home.Root, earlier, typed, trailer);

        var length = new FileInfo(transcript).Length;
        var typedStart = Encoding.UTF8.GetByteCount(earlier) + 1;
        var windowStart = length - 262_144;
        Assert.Equal(typedStart + 1, windowStart);

        var (_, stdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload(prompt, transcript), "hook", "user-prompt");

        Assert.Contains("tabs", stdout);
        Assert.Single(ReadCapturedStatements(home.Root));
    }

    // Nothing past 1,048,576 bytes is examined, and exceeding it is a quiet miss.
    [Fact]
    public void DoesNotCaptureWhenTheTypedRecordStartsBeyondTheSecondWindow()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        const string prompt = "I prefer tabs over spaces";
        var transcript = WriteTranscript(
            home.Root, [UserRecord("typed", prompt), .. PaddedAttachments(110, 10_000)]);
        Assert.True(new FileInfo(transcript).Length > 1_048_576 + 10_000);

        var (exitCode, stdout, stderr) = EngramProcess.RunWithStdin(
            home.Root, Payload(prompt, transcript), "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
        Assert.Empty(ReadCapturedStatements(home.Root));
    }

    // A complete malformed record in the first window is a decision, not a reason to read more:
    // the typed record behind it, reachable only by escalating, must stay uncaptured.
    [Fact]
    public void DoesNotEscalatePastAMalformedRecordInTheFirstWindow()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        const string prompt = "I prefer tabs over spaces";
        var transcript = WriteTranscript(
            home.Root,
            [
                UserRecord("typed", prompt),
                .. PaddedAttachments(30, 10_000),
                "{\"promptSource\": \"typed\", broken",
            ]);

        var (_, stdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload(prompt, transcript), "hook", "user-prompt");

        Assert.Equal(string.Empty, stdout);
        Assert.Empty(ReadCapturedStatements(home.Root));
    }

    // Nothing for the classifier to find means the transcript is never opened. A FIFO makes the
    // open observable: opening it for reading blocks until a writer appears, so a hook that
    // opened it would hang until the process helper's bound kills it and throws. A directory or
    // a missing file would fail the open quietly and look identical to not opening at all.
    [Fact]
    public void DoesNotOpenTheTranscriptWhenTheClassifierFindsNothing()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "mkfifo is not available on Windows.");

        using var home = new TestHome();
        var fifo = Path.Combine(home.Root, "transcript.fifo");
        using (var mkfifo = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { fifo } })!)
        {
            mkfifo.WaitForExit();
            Assert.Equal(0, mkfifo.ExitCode);
        }

        var (exitCode, stdout, stderr) = EngramProcess.RunWithStdin(
            home.Root, Payload("run the tests", fifo), "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
    }

    private static string Payload(string prompt, string transcriptPath) =>
        JsonSerializer.Serialize(new JsonObject
        {
            ["session_id"] = "e2e-user-prompt",
            ["prompt"] = prompt,
            ["transcript_path"] = transcriptPath,
        });

    // The record's keys as observed in a real interactive transcript; only the submission's
    // user record carries promptSource.
    private static string UserRecord(string promptSource, JsonNode content, string? origin = null)
    {
        var record = new JsonObject
        {
            ["type"] = "user",
            ["promptSource"] = promptSource,
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
        };

        if (origin is not null)
        {
            record["origin"] = new JsonObject { ["kind"] = origin };
        }

        return JsonSerializer.Serialize(record);
    }

    private static string UserRecord(string promptSource, string text, string? origin = null) =>
        UserRecord(promptSource, JsonValue.Create(text)!, origin);

    // The attachment.type values seen after a typed record in a real transcript.
    private static readonly string[] AttachmentTypes =
    [
        "environment", "model", "output_style_instructions", "deferred_tools_delta",
        "mcp_instructions_delta", "skill_listing", "auto_mode", "hook_additional_context",
        "date_change", "todo_reminder",
    ];

    private static string[] Attachments(int count) =>
        Enumerable.Range(0, count)
            .Select(i => JsonSerializer.Serialize(new JsonObject
            {
                ["type"] = "attachment",
                ["attachment"] = new JsonObject { ["type"] = AttachmentTypes[i % AttachmentTypes.Length] },
            }))
            .ToArray();

    // An attachment line whose UTF-8 length is exactly totalBytes, without a promptSource key.
    private static string PaddedAttachment(int totalBytes)
    {
        var bare = JsonSerializer.Serialize(new JsonObject { ["type"] = "attachment", ["padding"] = "" });
        return JsonSerializer.Serialize(new JsonObject
        {
            ["type"] = "attachment",
            ["padding"] = new string('x', totalBytes - bare.Length),
        });
    }

    private static string[] PaddedAttachments(int count, int bytesEach) =>
        Enumerable.Range(0, count).Select(_ => PaddedAttachment(bytesEach)).ToArray();

    private static string[] QueueOperations(int count) =>
        Enumerable.Range(0, count)
            .Select(_ => JsonSerializer.Serialize(new JsonObject { ["type"] = "queue-operation", ["operation"] = "enqueue" }))
            .ToArray();

    private static string WriteTranscript(string root, params string[] lines)
    {
        var path = Path.Combine(root, "transcript.jsonl");
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        return path;
    }

    private static string TypedTranscript(string root, string prompt) =>
        WriteTranscript(root, UserRecord("typed", prompt, origin: "human"));

    private static string SystemTranscript(string root, string prompt, string origin) =>
        WriteTranscript(root, UserRecord("system", prompt, origin));

    private static int CountUserPromptRecords(string root) =>
        File.Exists(Path.Combine(root, "telemetry.jsonl"))
            ? File.ReadAllLines(Path.Combine(root, "telemetry.jsonl")).Count(l => l.Contains("\"user-prompt\""))
            : 0;

    // Opened read-only, and through the provider rather than Engram.Core's own open routine:
    // a tier-3 test that asserts using the code under test stops saying anything about the
    // binary that ships.
    private static IReadOnlyList<string> ReadCapturedStatements(string root)
    {
        var databasePath = Path.Combine(root, "engram.db");
        if (!File.Exists(databasePath))
        {
            return [];
        }

        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
        connection.Open();

        // Keyed on the path, not on scope: the seed corpus is scoped 'user' too, so a scope
        // filter would report thirty-eight shipped facts as things this hook captured.
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT body FROM fact WHERE path LIKE '/user/%' AND valid_to IS NULL ORDER BY id;";

        var statements = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            statements.Add(reader.GetString(0));
        }

        return statements;
    }
}
