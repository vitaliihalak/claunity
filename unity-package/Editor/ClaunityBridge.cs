using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Claunity
{

/// <summary>
/// ClaunityBridge — lightweight HTTP server on port 8766.
/// Receives tool calls from the Python MCP server and executes them
/// on the Unity main thread via ClaunityActionExecutor.
/// </summary>
[InitializeOnLoad]
public static class ClaunityBridge
{
    private const int Port = 8766;

    private static HttpListener          _listener;
    private static Thread                _thread;
    private static volatile bool         _running;

    // Pending request from background thread → main thread
    private static volatile bool         _hasPending;
    private static string                _pendingTool;
    private static string                _pendingInput;
    private static string                _pendingResult;
    private static readonly object       _lock = new object();
    private static readonly ManualResetEventSlim _resultSignal = new ManualResetEventSlim(false);

    static ClaunityBridge()
    {
        Start();
        EditorApplication.update      += Tick;
        EditorApplication.quitting    += Stop;
        AssemblyReloadEvents.beforeAssemblyReload += Stop;
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    private static void Start()
    {
        if (_running) return;
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _running = true;

            _thread = new Thread(ListenLoop) { IsBackground = true, Name = "ClaunityBridge" };
            _thread.Start();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ClaunityBridge] Could not start on port {Port}: {e.Message}");
        }
    }

    private static void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
    }

    // ── Background listener thread ─────────────────────────────────────────────

    private static void ListenLoop()
    {
        while (_running)
        {
            HttpListenerContext ctx = null;
            try { ctx = _listener.GetContext(); }
            catch { break; }

            if (ctx == null) continue;

            // Only POST /execute is supported
            if (ctx.Request.HttpMethod != "POST" || ctx.Request.Url.AbsolutePath != "/execute")
            {
                Respond(ctx, 404, "{\"error\":\"not found\"}");
                continue;
            }

            string body;
            using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = sr.ReadToEnd();

            // Parse {"tool":"...", "input":{...}}
            string tool  = ExtractString(body, "tool");
            string input = ExtractObject(body, "input");

            if (string.IsNullOrEmpty(tool))
            {
                Respond(ctx, 400, "{\"error\":\"missing tool\"}");
                continue;
            }

            // Hand off to main thread and wait for result
            lock (_lock)
            {
                _pendingTool  = tool;
                _pendingInput = input ?? "{}";
                _hasPending   = true;
                _resultSignal.Reset();
            }

            // Wait until main thread has processed it (timeout 30s)
            _resultSignal.Wait(TimeSpan.FromSeconds(30));

            string result;
            lock (_lock) { result = _pendingResult ?? "✗ timeout"; }

            Respond(ctx, 200, $"{{\"result\":{JsonString(result)}}}");
        }
    }

    // ── Main thread tick ───────────────────────────────────────────────────────

    private static void Tick()
    {
        if (!_hasPending) return;

        string tool, input;
        lock (_lock)
        {
            if (!_hasPending) return;
            tool  = _pendingTool;
            input = _pendingInput;
            _hasPending = false;
        }

        string result;
        try
        {
            // Build ActionPayload: inject "type" field into input JSON
            string withType = input.Length > 2
                ? "{\"type\":\"" + tool + "\"," + input.Substring(1)
                : "{\"type\":\"" + tool + "\"}";
            var payload = JsonUtility.FromJson<ActionPayload>(withType);
            ClaunityActionExecutor.ClearAbsentOptionalVectors(payload, withType);
            result = payload != null
                ? ClaunityActionExecutor.Execute(payload)
                : "✗ failed to parse action";
        }
        catch (Exception e)
        {
            result = $"✗ {e.Message}";
        }

        lock (_lock)
        {
            _pendingResult = result;
        }
        _resultSignal.Set();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static void Respond(HttpListenerContext ctx, int status, string json)
    {
        try
        {
            ctx.Response.StatusCode  = status;
            ctx.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }
        catch { }
    }

    /// <summary>Extract a simple string value from JSON without a full parser.</summary>
    private static string ExtractString(string json, string key)
    {
        var search = $"\"{key}\"";
        int ki = json.IndexOf(search, StringComparison.Ordinal);
        if (ki < 0) return null;
        int ci = json.IndexOf(':', ki + search.Length);
        if (ci < 0) return null;
        int qi = json.IndexOf('"', ci + 1);
        if (qi < 0) return null;
        int qe = json.IndexOf('"', qi + 1);
        if (qe < 0) return null;
        return json.Substring(qi + 1, qe - qi - 1);
    }

    /// <summary>Extract a JSON object value for the given key.</summary>
    private static string ExtractObject(string json, string key)
    {
        var search = $"\"{key}\"";
        int ki = json.IndexOf(search, StringComparison.Ordinal);
        if (ki < 0) return "{}";
        int ci = json.IndexOf(':', ki + search.Length);
        if (ci < 0) return "{}";
        int ob = json.IndexOf('{', ci + 1);
        if (ob < 0) return "{}";
        int depth = 0, end = -1;
        for (int i = ob; i < json.Length; i++)
        {
            if (json[i] == '{') depth++;
            else if (json[i] == '}') { depth--; if (depth == 0) { end = i; break; } }
        }
        return end >= 0 ? json.Substring(ob, end - ob + 1) : "{}";
    }

    /// <summary>Escape a string for use as a JSON string value.</summary>
    private static string JsonString(string s)
    {
        if (s == null) return "\"\"";
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                        .Replace("\n", "\\n").Replace("\r", "\\r")
                        .Replace("\t", "\\t") + "\"";
    }
}

}