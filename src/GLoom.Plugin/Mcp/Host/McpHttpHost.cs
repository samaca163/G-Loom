using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GLoom.Mcp.Protocol;
using GLoom.Mcp.State;

namespace GLoom.Mcp.Host;

/// <summary>
/// The Streamable-HTTP face of the dispatcher on System.Net.HttpListener - in the box on
/// every .NET the plugin can be hosted by, so the .gha stays a single file. One POST, one
/// JSON response; no SSE, no sessions. The localhost prefix (not 127.0.0.1) is what lets a
/// non-elevated user bind on Windows without a URL ACL, and http.sys still serves loopback
/// only. Origin is checked because the spec makes it a MUST against DNS rebinding.
/// Off Windows the managed listener binds one address per prefix host and "localhost"
/// resolves to a single family (::1 on a stock Mac), so 127.0.0.1 is registered as well,
/// with a fallback to the bare prefix where that cannot bind. A bracketed [::1] prefix is
/// rejected by that listener ("invalid port"), and "*" / "+" would bind every interface.
/// </summary>
public sealed class McpHttpHost : IDisposable
{
    private const int MaxBodyBytes = 4 * 1024 * 1024;

    private readonly HttpListener _listener;
    private readonly McpDispatcher _dispatcher;
    private readonly string _token;
    private readonly Func<AgentAccess> _access;
    private readonly CancellationTokenSource _stop = new();

    public int Port { get; }
    public string Url => $"http://localhost:{Port}/mcp";

    private McpHttpHost(HttpListener listener, int port, McpDispatcher dispatcher, string token, Func<AgentAccess> access)
    {
        _listener = listener;
        Port = port;
        _dispatcher = dispatcher;
        _token = token;
        _access = access;
    }

    /// <summary>Binds the first free port from <paramref name="firstPort"/>; throws with the
    /// last bind error when none of <paramref name="tries"/> ports could be taken.</summary>
    public static McpHttpHost Start(McpDispatcher dispatcher, string token, Func<AgentAccess> access, int firstPort, int tries)
    {
        Exception? last = null;
        for (var port = firstPort; port < firstPort + tries; port++)
        {
            foreach (var prefixes in PrefixSets(port))
            {
                var listener = new HttpListener();
                foreach (var p in prefixes) listener.Prefixes.Add(p);
                try
                {
                    listener.Start();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    last = ex;
                    try { listener.Close(); } catch { }
                    continue;
                }
                var host = new McpHttpHost(listener, port, dispatcher, token, access);
                _ = Task.Run(host.AcceptLoop);
                return host;
            }
        }
        throw new InvalidOperationException(
            $"No free port in {firstPort}-{firstPort + tries - 1}: {last?.Message ?? "unknown error"}");
    }

    private static IEnumerable<string[]> PrefixSets(int port)
    {
        var bare = $"http://localhost:{port}/mcp/";
        if (!OperatingSystem.IsWindows())
            yield return new[] { bare, $"http://127.0.0.1:{port}/mcp/" };
        yield return new[] { bare };
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (_stop.IsCancellationRequested) { return; }
            catch (HttpListenerException) { continue; }
            catch (ObjectDisposedException) { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            if (!IsLocalOrigin(req.Headers["Origin"]))
            {
                Write(res, 403, JsonRpc.Serialize(JsonRpc.Error(null, JsonRpcErrors.InvalidRequest, "Origin not allowed.")));
                return;
            }
            if (req.HttpMethod != "POST")
            {
                res.AddHeader("Allow", "POST");
                Write(res, 405, null);
                return;
            }
            if (!McpToken.Matches(req.Headers["Authorization"], _token))
            {
                res.AddHeader("WWW-Authenticate", "Bearer realm=\"gloom\"");
                Write(res, 401, JsonRpc.Serialize(JsonRpc.Error(null, JsonRpcErrors.InvalidRequest, "Missing or wrong bearer token.")));
                return;
            }
            var accept = req.Headers["Accept"];
            if (!string.IsNullOrEmpty(accept)
                && !accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
                && !accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)
                && !accept.Contains("*/*", StringComparison.Ordinal))
            {
                Write(res, 406, null);
                return;
            }
            // ContentLength64 is -1 for a chunked body, so the cap is enforced while reading
            // rather than trusting the header; the connection is not kept alive after a 413
            // so the listener does not drain the rest into memory either.
            if (req.ContentLength64 > MaxBodyBytes)
            {
                res.KeepAlive = false;
                Write(res, 413, null);
                return;
            }
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int n;
            while ((n = req.InputStream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + n > MaxBodyBytes)
                {
                    res.KeepAlive = false;
                    Write(res, 413, null);
                    return;
                }
                buffer.Write(chunk, 0, n);
            }
            var body = (req.ContentEncoding ?? Encoding.UTF8).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);

            var result = _dispatcher.Handle(body, new DispatchContext(
                _access(), req.Headers["MCP-Protocol-Version"], _stop.Token));
            Write(res, result.HttpStatus, result.Body);
        }
        catch (Exception ex)
        {
            try { Write(res, 500, JsonRpc.Serialize(JsonRpc.Error(null, JsonRpcErrors.Internal, ex.Message))); }
            catch { }
        }
    }

    private static void Write(HttpListenerResponse res, int status, string? body)
    {
        res.StatusCode = status;
        if (body is null)
        {
            res.ContentLength64 = 0;
            res.Close();
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(body);
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.Close();
    }

    public static bool IsLocalOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        return uri.Host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}
