using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// Tier 3. The published binary, serving real HTTP: proves the source-generated JSON works under
/// Native AOT and that each guard in front of the mod API rejects what it is there to reject.
/// </summary>
public class ModApiE2ETests
{
    private const string Session = "cc-e2e-session";
    private const string UniqueWord = "zorblax";

    [Fact]
    public async Task AllSevenOps_ReturnTheirJsonShapes_AndWriteOnlyModCallRecords()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        using var server = StartedServer.Begin(home);
        using var http = new HttpClient();

        var remember = await Post(http, server.Port, "remember", new JsonObject
        {
            ["session_id"] = Session,
            ["statement"] = $"The {UniqueWord} service retries uploads twice.",
            ["evidence"] = "an e2e test",
        });
        Assert.Equal(HttpStatusCode.OK, remember.Status);
        var handle = (string)remember.Body["handle"]!;
        Assert.True((bool)remember.Body["created"]!);

        var fact = await Post(http, server.Port, "fact", new JsonObject { ["fact_id"] = handle });
        Assert.Equal(HttpStatusCode.OK, fact.Status);
        Assert.Contains(UniqueWord, (string)fact.Body["body"]!, StringComparison.Ordinal);
        Assert.True((bool)fact.Body["live"]!);

        var history = await Post(http, server.Port, "history", new JsonObject { ["fact_id"] = handle });
        Assert.Equal(HttpStatusCode.OK, history.Status);
        Assert.Single(history.Body["versions"]!.AsArray());

        var recall = await Post(http, server.Port, "recall", new JsonObject
        {
            ["session_id"] = Session,
            ["query"] = UniqueWord,
            ["mode"] = "shadow",
        });
        Assert.Equal(HttpStatusCode.OK, recall.Status);
        Assert.Contains(handle, (string)recall.Body["text"]!, StringComparison.Ordinal);
        Assert.Contains((string)recall.Body["coverage"]!, new[] { "high", "partial", "none" });
        Assert.True((int)recall.Body["fact_count"]! >= 1);

        var captures = await Post(http, server.Port, "captures", new JsonObject { ["session_id"] = Session, ["since"] = 0 });
        Assert.Equal(HttpStatusCode.OK, captures.Status);
        Assert.Empty(captures.Body["captures"]!.AsArray());

        var pathFacts = await Post(http, server.Port, "path-facts", new JsonObject { ["path"] = Path.Combine(home.Root, "not-a-repo", "x.cs") });
        Assert.Equal(HttpStatusCode.OK, pathFacts.Status);
        Assert.Null(pathFacts.Body["entity_path"]);
        Assert.Empty(pathFacts.Body["facts"]!.AsArray());

        var forget = await Post(http, server.Port, "forget", new JsonObject { ["session_id"] = Session, ["fact_id"] = handle });
        Assert.Equal(HttpStatusCode.OK, forget.Status);
        Assert.True((bool)forget.Body["retracted"]!);

        Assert.Equal(3, KindCount(home, "mod-call"));
        Assert.Equal(0, KindCount(home, "recall"));
        Assert.Equal(0, KindCount(home, "remember"));
        Assert.Equal(0, KindCount(home, "session-open"));
    }

    [Fact]
    public async Task Unknown_Op_Is404Json()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        using var server = StartedServer.Begin(home);
        using var http = new HttpClient();

        var result = await Post(http, server.Port, "foo", new JsonObject());

        Assert.Equal(HttpStatusCode.NotFound, result.Status);
        Assert.Equal("not_found", (string)result.Body["error"]!);
    }

    [Theory]
    [InlineData("http://evil.example.com")]
    [InlineData("http://localhost:7433")]
    [InlineData("http://127.0.0.1:7433")]
    [InlineData("null")]
    public async Task AnyOriginHeader_Is403_OnModApiHealthAndMcp(string origin)
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        using var server = StartedServer.Begin(home);
        using var http = new HttpClient();

        foreach (var (method, path) in new[] { (HttpMethod.Post, "/mod/v1/captures"), (HttpMethod.Get, "/health"), (HttpMethod.Post, "/") })
        {
            using var request = JsonRequest(server.Port, method, path, """{"mod":"lens","session_id":"s","since":0}""");
            request.Headers.Add("Origin", origin);

            var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Equal(0, KindCount(home, "mod-call"));
    }

    [Theory]
    [InlineData("evil.example", HttpStatusCode.Forbidden)]
    [InlineData("evil.example:7433", HttpStatusCode.Forbidden)]
    [InlineData("127.0.0.1.evil.example", HttpStatusCode.Forbidden)]
    [InlineData("localhost", HttpStatusCode.OK)]
    [InlineData("[::1]", HttpStatusCode.OK)]
    [InlineData("127.0.0.1", HttpStatusCode.OK)]
    public async Task HostHeader_IsCheckedOnEveryRoute(string host, HttpStatusCode expected)
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        using var server = StartedServer.Begin(home);
        using var http = new HttpClient();

        foreach (var (method, path, body) in new[]
        {
            (HttpMethod.Get, "/health", null),
            (HttpMethod.Post, "/mod/v1/captures", """{"mod":"lens","session_id":"s","since":0}"""),
        })
        {
            using var request = JsonRequest(server.Port, method, path, body);
            request.Headers.Host = host;
            request.Headers.Add("X-Engram-Mod", "lens");

            var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(expected, response.StatusCode);
        }

        using var mcp = JsonRequest(server.Port, HttpMethod.Post, "/", """{"jsonrpc":"2.0","id":1,"method":"ping"}""");
        mcp.Headers.Host = host;
        mcp.Headers.Accept.ParseAdd("application/json");
        mcp.Headers.Accept.ParseAdd("text/event-stream");
        var mcpResponse = await http.SendAsync(mcp, TestContext.Current.CancellationToken);
        Assert.Equal(expected == HttpStatusCode.Forbidden, mcpResponse.StatusCode == HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Mcp_StillConnectsAndListsTools_AfterTheHostCheck()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        using var server = StartedServer.Begin(home);
        var cancellationToken = TestContext.Current.CancellationToken;

        using var client = new HttpMcpClient(server.Port);
        await client.InitializeAsync(cancellationToken);
        await client.ListToolsAsync(cancellationToken);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData(null)]
    public async Task NonJsonContentType_Is415_AndWritesNothing(string? contentType)
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        using var server = StartedServer.Begin(home);
        using var http = new HttpClient();

        var result = await Post(http, server.Port, "remember", RememberBody(), contentType: contentType);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, result.Status);
        Assert.Equal("unsupported_media_type", (string)result.Body["error"]!);
        await AssertNothingWasStored(http, server);
    }

    [Theory]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("Application/JSON")]
    public async Task JsonContentTypeWithParametersOrOtherCasing_IsAccepted(string contentType)
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        using var server = StartedServer.Begin(home);
        using var http = new HttpClient();

        var result = await Post(http, server.Port, "captures", new JsonObject { ["session_id"] = Session, ["since"] = 0 }, contentType: contentType);

        Assert.Equal(HttpStatusCode.OK, result.Status);
    }

    [Fact]
    public async Task MissingModHeader_Is400_AndWritesNothing()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        using var server = StartedServer.Begin(home);
        using var http = new HttpClient();

        var result = await Post(http, server.Port, "remember", RememberBody(), mod: null);

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        await AssertNothingWasStored(http, server);
    }

    [Theory]
    [InlineData("Lens")]
    [InlineData("lens toasts")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task MalformedModHeader_Is400(string header)
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        using var server = StartedServer.Begin(home);
        using var http = new HttpClient();

        var result = await Post(http, server.Port, "captures", new JsonObject { ["mod"] = header, ["session_id"] = Session, ["since"] = 0 }, mod: header);

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
    }

    [Fact]
    public async Task HeaderThatDiffersFromTheBodyMod_Is400_AndWritesNothing()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        using var server = StartedServer.Begin(home);
        using var http = new HttpClient();

        var body = RememberBody();
        body["mod"] = "toasts";
        var result = await Post(http, server.Port, "remember", body, mod: "lens");

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        await AssertNothingWasStored(http, server);
    }

    [Theory]
    [InlineData(65536, HttpStatusCode.OK)]
    [InlineData(65537, HttpStatusCode.RequestEntityTooLarge)]
    public async Task BodySize_IsBoundedAt64KiB(int bytes, HttpStatusCode expected)
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        using var server = StartedServer.Begin(home);
        using var http = new HttpClient();

        const string prefix = """{"mod":"lens","session_id":"s","since":0,"pad":"{0}"}""";
        var overhead = Encoding.UTF8.GetByteCount(prefix.Replace("{0}", string.Empty, StringComparison.Ordinal));
        var json = prefix.Replace("{0}", new string('p', bytes - overhead), StringComparison.Ordinal);
        Assert.Equal(bytes, Encoding.UTF8.GetByteCount(json));

        using var request = JsonRequest(server.Port, HttpMethod.Post, "/mod/v1/captures", json);
        request.Headers.Add("X-Engram-Mod", "lens");
        var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task NonPostMethod_Is405WithAnEmptyBody(string method)
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        using var server = StartedServer.Begin(home);
        using var http = new HttpClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{server.Port}/mod/v1/recall");
        request.Headers.Add("X-Engram-Mod", "lens");
        var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Preflight_GetsNoCorsAllowHeaders()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        using var server = StartedServer.Begin(home);
        using var http = new HttpClient();

        foreach (var withOrigin in new[] { false, true })
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, $"http://127.0.0.1:{server.Port}/mod/v1/remember");
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "x-engram-mod, content-type");
            if (withOrigin)
            {
                request.Headers.Add("Origin", "http://evil.example.com");
            }

            var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.False(response.IsSuccessStatusCode);
            Assert.DoesNotContain(response.Headers, h => h.Key.StartsWith("Access-Control-Allow", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task QueryWithBreaksAndAFakeRecord_WritesExactlyOneModCallLine()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        using var server = StartedServer.Begin(home);
        using var http = new HttpClient();
        var logBefore = ReadLog(home);

        var result = await Post(http, server.Port, "recall", new JsonObject
        {
            ["session_id"] = Session,
            ["query"] = "transaction\n{\"kind\":\"recall\",\"session_id\":\"x\"}\r\u2028tail",
        });

        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal(1, KindCount(home, "mod-call"));
        Assert.Equal(0, KindCount(home, "recall"));
        Assert.Equal(logBefore, ReadLog(home));
    }

    private static string ReadLog(TestHome home)
    {
        var path = Path.Combine(home.Root, "engram.log");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static JsonObject RememberBody() => new()
    {
        ["session_id"] = Session,
        ["statement"] = $"The {UniqueWord} cache is flushed nightly.",
        ["evidence"] = "an e2e test",
    };

    private static async Task AssertNothingWasStored(HttpClient http, StartedServer server)
    {
        var recall = await Post(http, server.Port, "recall", new JsonObject { ["session_id"] = "other-session", ["query"] = UniqueWord });

        Assert.DoesNotContain($"The {UniqueWord} cache", (string)recall.Body["text"]!, StringComparison.Ordinal);
    }

    private static HttpRequestMessage JsonRequest(int port, HttpMethod method, string path, string? body)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{port}{path}");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static async Task<(HttpStatusCode Status, JsonNode Body)> Post(
        HttpClient http,
        int port,
        string op,
        JsonObject body,
        string? mod = "lens",
        string? contentType = "application/json")
    {
        if (mod is not null)
        {
            body["mod"] ??= mod;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mod/v1/{op}")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString())),
        };
        request.Content.Headers.ContentType = contentType is null ? null : MediaTypeHeaderValue.Parse(contentType);
        if (mod is not null)
        {
            request.Headers.Add("X-Engram-Mod", mod);
        }

        var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, JsonNode.Parse(text) ?? new JsonObject());
    }

    private static int KindCount(TestHome home, string kind)
    {
        var path = Path.Combine(home.Root, "telemetry.jsonl");
        return File.Exists(path)
            ? File.ReadAllLines(path)
                .Count(line => JsonDocument.Parse(line).RootElement.GetProperty("kind").GetString() == kind)
            : 0;
    }

    private sealed class StartedServer : IDisposable
    {
        private readonly TestHome _home;

        private StartedServer(TestHome home, int port)
        {
            _home = home;
            Port = port;
        }

        public int Port { get; }

        public static StartedServer Begin(TestHome home)
        {
            var port = FreeTcpPort.Next();
            var (exit, _, stderr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
            Assert.True(exit == 0, $"start failed: {stderr}");
            return new StartedServer(home, port);
        }

        public void Dispose() => EngramProcess.Run(_home.Root, "stop");
    }
}
