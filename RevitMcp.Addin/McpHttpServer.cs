using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMcp;

/// <summary>
/// Minimal localhost HTTP API (HttpListener, 127.0.0.1 only, no auth — local dev bridge).
/// Routes:
///   GET  /ping            -> Revit version info
///   POST /open  {path, detach?, audit?}
///   POST /close {save?}
///   GET  /doc             -> active document info + counts
///   GET  /rooms?limit=&offset=
///   GET  /levels
///   POST /run   {command} -> run HeatLoss plugin command (settings|modeling|calculations|main|reports|tools|about|snapshot)
///   GET  /commands        -> list HeatLoss command keys
/// </summary>
internal class McpHttpServer
{
    private readonly int _port;
    private readonly McpHandler _handler;
    private HttpListener _listener;
    private Thread _thread;
    private volatile bool _running;

    public McpHttpServer(int port, McpHandler handler)
    {
        _port = port;
        _handler = handler;
    }

    public void Start()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add("http://127.0.0.1:" + _port + "/");
        _listener.Start();
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "RevitMcp-Http" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch (Exception) { }
        try { _listener?.Close(); } catch (Exception) { }
    }

    private void Loop()
    {
        while (_running)
        {
            HttpListenerContext ctx = null;
            try
            {
                ctx = _listener.GetContext();
            }
            catch (Exception)
            {
                return; // listener stopped
            }
            try
            {
                Handle(ctx);
            }
            catch (Exception ex)
            {
                McpLog.Write("Handle failed: " + ex.Message);
                try { WriteJson(ctx, 500, Ok(false, null, ex.Message)); } catch (Exception) { }
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var path = (req.Url.AbsolutePath ?? "/").TrimEnd('/');
        if (path.Length == 0) path = "/";
        McpLog.Write(req.HttpMethod + " " + path);

        switch (req.HttpMethod + " " + path)
        {
            case "GET /ping":
                WriteJson(ctx, 200, Ok(true, _handler.Run(app => McpOps.Ping(app), 60000), null));
                return;
            case "GET /doc":
                WriteJson(ctx, 200, Ok(true, _handler.Run(app => McpOps.DocInfo(app)), null));
                return;
            case "GET /rooms":
                {
                    var q = ParseQuery(req.Url.Query);
                    var limit = GetInt(q, "limit", 50);
                    var offset = GetInt(q, "offset", 0);
                    WriteJson(ctx, 200, Ok(true, _handler.Run(app => McpOps.Rooms(app, limit, offset)), null));
                    return;
                }
            case "GET /levels":
                WriteJson(ctx, 200, Ok(true, _handler.Run(app => McpOps.Levels(app)), null));
                return;
            case "GET /commands":
                WriteJson(ctx, 200, Ok(true, McpOps.ListHeatLossCommands(), null));
                return;
            case "POST /open":
                {
                    var body = ReadBody(req);
                    var p = body.Value<string>("path") ?? "";
                    var detach = body.Value<bool?>("detach") ?? false;
                    var audit = body.Value<bool?>("audit") ?? false;
                    var data = _handler.Run(app => McpOps.Open(app, p, detach, audit), 600000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /close":
                {
                    var body = ReadBody(req);
                    var save = body.Value<bool?>("save") ?? false;
                    var data = _handler.Run(app => McpOps.Close(app, save), 180000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /run":
                {
                    var body = ReadBody(req);
                    var command = body.Value<string>("command") ?? "calculations";
                    var data = _handler.Run(app => McpOps.RunHeatLoss(app, command), 300000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            default:
                WriteJson(ctx, 404, Ok(false, null, "Unknown route: " + req.HttpMethod + " " + path));
                return;
        }
    }

    private static Dictionary<string, object> Ok(bool ok, object data, string error)
    {
        return new Dictionary<string, object>
        {
            { "ok", ok },
            { "data", data },
            { "error", error },
        };
    }

    private static JObject ReadBody(HttpListenerRequest req)
    {
        if (!req.HasEntityBody) return new JObject();
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
        {
            var text = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text)) return new JObject();
            return JObject.Parse(text);
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return d;
        var q = query.TrimStart('?');
        foreach (var part in q.Split('&'))
        {
            if (part.Length == 0) continue;
            var kv = part.Split(new[] { '=' }, 2);
            var k = Uri.UnescapeDataString(kv[0]);
            var v = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            d[k] = v;
        }
        return d;
    }

    private static int GetInt(Dictionary<string, string> q, string key, int def)
    {
        string v;
        int n;
        if (q.TryGetValue(key, out v) && int.TryParse(v, out n)) return n;
        return def;
    }

    private static void WriteJson(HttpListenerContext ctx, int status, object payload)
    {
        var json = JsonConvert.SerializeObject(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }
}
