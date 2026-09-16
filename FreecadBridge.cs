using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIOrchestrator.API;

/// <summary>Outcome of one code-execution round-trip to the FreeCAD bridge.</summary>
internal readonly record struct ExecResult(
    bool Success,
    JsonElement? Result,
    string Stdout,
    string Stderr,
    string? ErrorType,
    string? ErrorTraceback);

/// <summary>
/// Pure-.NET JSON-RPC 2.0 client for the FreeCAD Robust MCP Bridge (newline-delimited JSON over a
/// TCP socket). The bridge runs inside a live FreeCAD process and exposes a single
/// <c>execute(code)</c> primitive plus <c>ping</c>; every CAD operation is a Python snippet sent
/// through <see cref="Execute"/>. The connection is opened lazily on first use and reused; a broken
/// socket is transparently reconnected once before the call is failed.
/// </summary>
internal sealed class FreecadBridge : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly int _defaultTimeoutMs;
    private readonly object _gate = new();

    private TcpClient? _client;
    private NetworkStream? _stream;
    private StreamReader? _reader;

    public FreecadBridge()
    {
        _host = Environment.GetEnvironmentVariable("FREECAD_HOST") ?? "127.0.0.1";
        _port = ParseInt(Environment.GetEnvironmentVariable("FREECAD_PORT"), 9876);
        _defaultTimeoutMs = ParseInt(Environment.GetEnvironmentVariable("FREECAD_TIMEOUT_MS"), 30000);
    }

    public string Host => _host;
    public int Port => _port;

    /// <summary>Send Python <paramref name="code"/> to FreeCAD and return the normalized result.</summary>
    public ExecResult Execute(string code, int? timeoutMs = null)
    {
        var timeout = timeoutMs ?? _defaultTimeoutMs;
        lock (_gate)
        {
            try
            {
                return SendExecute(code, timeout);
            }
            catch (IOException) { return ReconnectAnd(code, timeout); }
            catch (SocketException) { return ReconnectAnd(code, timeout); }
        }
    }

    /// <summary>Round-trip a ping. Returns latency in ms, or -1 when unreachable.</summary>
    public double Ping()
    {
        lock (_gate)
        {
            var start = DateTime.UtcNow;
            try
            {
                var resp = RoundTrip("{\"jsonrpc\":\"2.0\",\"id\":\"ping\",\"method\":\"ping\",\"params\":{}}", _defaultTimeoutMs);
                using (resp)
                {
                    if (resp.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("pong", out var p) && p.GetBoolean())
                        return (DateTime.UtcNow - start).TotalMilliseconds;
                }
                return -1;
            }
            catch { return -1; }
        }
    }

    private ExecResult ReconnectAnd(string code, int timeout)
    {
        CloseSocket();
        try { return SendExecute(code, timeout); }
        catch (Exception ex)
        {
            return new ExecResult(false, null, "", $"FreeCAD bridge unreachable at {_host}:{_port} — {ex.Message}", "ConnectionError", null);
        }
    }

    private ExecResult SendExecute(string code, int timeout)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Guid.NewGuid().ToString(),
            ["method"] = "execute",
            ["params"] = new JsonObject { ["code"] = code, ["timeout_ms"] = timeout }
        };
        JsonDocument resp;
        try { resp = RoundTrip(payload.ToJsonString(), timeout); }
        catch (Exception ex)
        {
            return new ExecResult(false, null, "", $"FreeCAD bridge unreachable at {_host}:{_port} — {ex.Message}", "ConnectionError", null);
        }

        using (resp)
        {
            var root = resp.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "unknown RPC error";
                return new ExecResult(false, null, "", msg ?? "RPC error", "RpcError", null);
            }
            var res = root.GetProperty("result");
            bool success = res.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            JsonElement? result = res.TryGetProperty("result", out var rv) && rv.ValueKind != JsonValueKind.Null ? rv.Clone() : null;
            string stdout = res.TryGetProperty("stdout", out var so) ? so.GetString() ?? "" : "";
            string stderr = res.TryGetProperty("stderr", out var se) ? se.GetString() ?? "" : "";
            string? etype = res.TryGetProperty("error_type", out var et) && et.ValueKind != JsonValueKind.Null ? et.GetString() : null;
            string? etb = res.TryGetProperty("error_traceback", out var tb) && tb.ValueKind != JsonValueKind.Null ? tb.GetString() : null;
            return new ExecResult(success, result, stdout, stderr, etype, etb);
        }
    }

    private JsonDocument RoundTrip(string requestJson, int timeoutMs)
    {
        EnsureConnected(timeoutMs);
        var bytes = Encoding.UTF8.GetBytes(requestJson + "\n");
        _stream!.Write(bytes, 0, bytes.Length);
        _stream.Flush();
        var line = _reader!.ReadLine() ?? throw new IOException("Connection closed by bridge.");
        return JsonDocument.Parse(line);
    }

    private void EnsureConnected(int timeoutMs)
    {
        if (_client is { Connected: true } && _stream != null && _reader != null)
        {
            _stream.ReadTimeout = timeoutMs;
            _stream.WriteTimeout = timeoutMs;
            return;
        }
        CloseSocket();
        var client = new TcpClient { NoDelay = true };
        client.Connect(_host, _port);
        var stream = client.GetStream();
        stream.ReadTimeout = timeoutMs;
        stream.WriteTimeout = timeoutMs;
        _client = client;
        _stream = stream;
        _reader = new StreamReader(stream, new UTF8Encoding(false));
    }

    private void CloseSocket()
    {
        try { _reader?.Dispose(); } catch { }
        try { _stream?.Dispose(); } catch { }
        try { _client?.Close(); } catch { }
        _reader = null;
        _stream = null;
        _client = null;
    }

    public void Dispose() => CloseSocket();

    private static int ParseInt(string? v, int fallback) => int.TryParse(v, out var n) && n > 0 ? n : fallback;
}
