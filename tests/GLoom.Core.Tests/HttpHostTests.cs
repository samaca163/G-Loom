using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using GLoom.Mcp.Host;
using GLoom.Mcp.Protocol;
using Xunit;

namespace GLoom.Core.Tests;

/// <summary>The listener as a client sees it - real sockets, no Rhino.</summary>
public class HttpHostTests : IDisposable
{
    private const string Token = "test-token-0123456789-0123456789-0123456789";
    private readonly McpHttpHost _host;
    private readonly HttpClient _client = new();
    private AgentAccess _access = AgentAccess.ReadOnly;

    public HttpHostTests()
    {
        var d = new McpDispatcher("0.3.0-test", "instructions");
        d.Register(new McpTool("gloom_echo", "Echoes.", Schema.Object().String("text", "t").Build(), ToolAccess.Read,
            (a, _) => ToolResult.Text("echo:" + Args.String(a, "text"))));
        _host = McpHttpHost.Start(d, Token, () => _access, 27500, 20);
    }

    public void Dispose()
    {
        _host.Dispose();
        _client.Dispose();
    }

    private Task<HttpResponseMessage> Post(string json, string? token = Token, string? origin = null, string? accept = "application/json, text/event-stream")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _host.Url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin is not null) req.Headers.TryAddWithoutValidation("Origin", origin);
        if (accept is not null) req.Headers.TryAddWithoutValidation("Accept", accept);
        req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-11-25");
        return _client.SendAsync(req);
    }

    [Fact]
    public async Task The_handshake_and_a_tool_call_work_end_to_end()
    {
        var init = await Post("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""");
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        Assert.Equal("application/json", init.Content.Headers.ContentType!.MediaType);
        var body = (JsonObject)JsonNode.Parse(await init.Content.ReadAsStringAsync())!;
        Assert.Equal("gloom", body["result"]!["serverInfo"]!["name"]!.GetValue<string>());

        var initialized = await Post("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.Equal(HttpStatusCode.Accepted, initialized.StatusCode);

        var call = await Post("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"gloom_echo","arguments":{"text":"hi"}}}""");
        var result = (JsonObject)JsonNode.Parse(await call.Content.ReadAsStringAsync())!;
        Assert.Equal("echo:hi", result["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_missing_or_wrong_token_is_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", token: null)).StatusCode);
        var wrong = await Post("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", token: "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Contains("Bearer", wrong.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task A_foreign_origin_is_403_even_with_the_right_token_and_a_local_one_passes()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", origin: "http://evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", origin: "http://localhost:3000")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", origin: "http://127.0.0.1")).StatusCode);
    }

    [Fact]
    public async Task Only_post_is_served()
    {
        var get = new HttpRequestMessage(HttpMethod.Get, _host.Url);
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var res = await _client.SendAsync(get);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
        Assert.Contains("POST", res.Content.Headers.Allow);
    }

    [Fact]
    public async Task An_accept_header_that_takes_neither_json_nor_sse_is_406()
    {
        Assert.Equal(HttpStatusCode.NotAcceptable, (await Post("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", accept: "text/html")).StatusCode);
    }

    [Fact]
    public async Task Access_changes_apply_to_the_next_request_without_a_restart()
    {
        _access = AgentAccess.Off;
        var call = await Post("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"gloom_echo","arguments":{"text":"hi"}}}""");
        var result = (JsonObject)JsonNode.Parse(await call.Content.ReadAsStringAsync())!;
        Assert.True(result["result"]!["isError"]!.GetValue<bool>());
        _access = AgentAccess.ReadOnly;
    }

    [Fact]
    public async Task Oversized_bodies_are_refused_whether_or_not_they_declare_a_length()
    {
        var big = new byte[5 * 1024 * 1024];
        Array.Fill(big, (byte)'x');

        var declared = new HttpRequestMessage(HttpMethod.Post, _host.Url) { Content = new ByteArrayContent(big) };
        declared.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await SendTolerant(declared)) ?? HttpStatusCode.RequestEntityTooLarge);

        var chunked = new HttpRequestMessage(HttpMethod.Post, _host.Url) { Content = new StreamContent(new MemoryStream(big)) };
        chunked.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        chunked.Headers.TransferEncodingChunked = true;
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await SendTolerant(chunked)) ?? HttpStatusCode.RequestEntityTooLarge);
    }

    // A server that stops reading mid-body may reset the connection before the client
    // has read the status; either outcome proves the body was not buffered.
    private async Task<HttpStatusCode?> SendTolerant(HttpRequestMessage req)
    {
        try { return (await _client.SendAsync(req)).StatusCode; }
        catch (HttpRequestException) { return null; }
        catch (IOException) { return null; }
    }

    [Fact]
    public async Task Both_loopback_families_answer()
    {
        foreach (var host in new[] { "localhost", "127.0.0.1" })
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"http://{host}:{_host.Port}/mcp")
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            string outcome;
            try { outcome = (await _client.SendAsync(req)).StatusCode.ToString(); }
            catch (HttpRequestException ex) { outcome = "exception: " + ex.Message; }
            // Windows http.sys matches the localhost prefix by Host header only.
            if (OperatingSystem.IsWindows() && host != "localhost") continue;
            Assert.True(outcome == "OK", $"{host}: {outcome}");
        }
    }

    [Fact]
    public void A_second_host_takes_the_next_port()
    {
        using var second = McpHttpHost.Start(new McpDispatcher("x", ""), Token, () => AgentAccess.ReadOnly, _host.Port, 5);
        Assert.Equal(_host.Port + 1, second.Port);
    }

    [Fact]
    public void Origin_rules()
    {
        Assert.True(McpHttpHost.IsLocalOrigin(null));
        Assert.True(McpHttpHost.IsLocalOrigin("http://localhost"));
        Assert.True(McpHttpHost.IsLocalOrigin("https://127.0.0.1:8443"));
        Assert.True(McpHttpHost.IsLocalOrigin("http://[::1]:5"));
        Assert.False(McpHttpHost.IsLocalOrigin("http://localhost.evil.com"));
        Assert.False(McpHttpHost.IsLocalOrigin("null"));
        Assert.False(McpHttpHost.IsLocalOrigin("garbage"));
    }
}
