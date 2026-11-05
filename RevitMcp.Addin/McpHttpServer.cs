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
            case "POST /create_level":
                {
                    var body = ReadBody(req);
                    var name = body.Value<string>("name") ?? "Level";
                    var elev = body.Value<double?>("elevation_mm") ?? 0;
                    var data = _handler.Run(app => McpOps.CreateLevel(app, name, elev), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /wall_types":
                WriteJson(ctx, 200, Ok(true, _handler.Run(app => McpOps.ListWallTypes(app)), null));
                return;
            case "POST /create_wall_type":
                {
                    var body = ReadBody(req);
                    var name = body.Value<string>("name") ?? "WallType";
                    var layers = body["layers"] != null ? body["layers"].ToObject<List<Dictionary<string, object>>>() : null;
                    var data = _handler.Run(app => McpOps.CreateWallType(app, name, layers), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /create_wall":
                {
                    var body = ReadBody(req);
                    var typeId = body.Value<string>("type_id") ?? body.Value<string>("type") ?? "";
                    var baseLevel = body.Value<string>("base_level") ?? "";
                    var topLevel = body.Value<string>("top_level") ?? "";
                    var start = body["start_mm"] != null ? body["start_mm"].ToObject<double[]>() : new[] { 0.0, 0.0 };
                    var end = body["end_mm"] != null ? body["end_mm"].ToObject<double[]>() : new[] { 1000.0, 0.0 };
                    var flip = body.Value<bool?>("flip") ?? false;
                    var data = _handler.Run(app => McpOps.CreateWall(app, typeId, baseLevel, topLevel, start, end, flip), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /cut_opening":
                {
                    var body = ReadBody(req);
                    var host = body.Value<long>("host_wall_id");
                    var w = body.Value<double?>("width_mm") ?? 1000;
                    var h = body.Value<double?>("height_mm") ?? 1000;
                    var sill = body.Value<double?>("sill_mm") ?? 900;
                    var pos = body.Value<double?>("position_mm") ?? 0;
                    var data = _handler.Run(app => McpOps.CutOpening(app, host, w, h, sill, pos), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /place_family":
                {
                    var body = ReadBody(req);
                    var fam = body.Value<string>("family") ?? "";
                    var type = body.Value<string>("type") ?? "";
                    long? host = body.Value<long?>("host_wall_id");
                    var loc = body["location_mm"] != null ? body["location_mm"].ToObject<double[]>() : new[] { 0.0, 0.0, 0.0 };
                    var rot = body.Value<double?>("rotation_deg") ?? 0;
                    Dictionary<string, object> pars = null;
                    if (body["parameters"] != null) pars = body["parameters"].ToObject<Dictionary<string, object>>();
                    var data = _handler.Run(app => McpOps.PlaceFamily(app, fam, type, host, loc, rot, pars), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /load_family":
                {
                    var body = ReadBody(req);
                    var rfaPath = body.Value<string>("rfa_path") ?? "";
                    var data = _handler.Run(app => McpOps.LoadFamily(app, rfaPath), 300000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /element":
                {
                    var q = ParseQuery(req.Url.Query);
                    long id;
                    if (!q.TryGetValue("id", out var ids) || !long.TryParse(ids, out id)) throw new ArgumentException("id required");
                    WriteJson(ctx, 200, Ok(true, _handler.Run(app => McpOps.GetElement(app, id)), null));
                    return;
                }
            case "GET /query":
                {
                    var q = ParseQuery(req.Url.Query);
                    var cat = q.TryGetValue("category", out var c) ? c : "OST_Walls";
                    var level = q.TryGetValue("level", out var l) ? l : "";
                    var limit = GetInt(q, "limit", 500);
                    WriteJson(ctx, 200, Ok(true, _handler.Run(app => McpOps.QueryElements(app, cat, level, limit)), null));
                    return;
                }
            case "POST /delete":
                {
                    var body = ReadBody(req);
                    var ids = body["ids"] != null ? body["ids"].ToObject<List<long>>() : new List<long>();
                    var data = _handler.Run(app => McpOps.DeleteElements(app, ids), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /schedules":
                WriteJson(ctx, 200, Ok(true, _handler.Run(app => McpOps.ListSchedules(app)), null));
                return;
            case "GET /schedule":
                {
                    var q = ParseQuery(req.Url.Query);
                    long id;
                    if (!q.TryGetValue("id", out var ids) || !long.TryParse(ids, out id)) throw new ArgumentException("id required");
                    WriteJson(ctx, 200, Ok(true, _handler.Run(app => McpOps.GetSchedule(app, id)), null));
                    return;
                }
            case "POST /duplicate_schedule":
                {
                    var body = ReadBody(req);
                    var src = body.Value<long>("source_id");
                    var name = body.Value<string>("new_name") ?? "Schedule Copy";
                    var data = _handler.Run(app => McpOps.DuplicateSchedule(app, src, name), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /export_schedule":
                {
                    var body = ReadBody(req);
                    var id = body.Value<long>("schedule_id");
                    var outPath = body.Value<string>("path");
                    var data = _handler.Run(app => McpOps.ExportSchedule(app, id, outPath), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
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
            case "POST /create_room":
                {
                    var body = ReadBody(req);
                    var levelId = body.Value<long>("level_id");
                    var loc = body["location_mm"] != null ? body["location_mm"].ToObject<double[]>() : new[] { 0.0, 0.0 };
                    var number = body.Value<string>("number") ?? "";
                    var name = body.Value<string>("name") ?? "";
                    var data = _handler.Run(app => McpOps.CreateRoom(app, levelId, loc, number, name), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }

            case "POST /create_grid":
                {
                    var body = ReadBody(req);
                    var name = body.Value<string>("name") ?? "";
                    var start = body["start_mm"] != null ? body["start_mm"].ToObject<double[]>() : new[] { 0.0, 0.0 };
                    var end = body["end_mm"] != null ? body["end_mm"].ToObject<double[]>() : new[] { 0.0, 0.0 };
                    var data = _handler.Run(app => McpOps.CreateGrid(app, name, start, end), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /grids":
                {
                    var data = _handler.Run(app => McpOps.ListGrids(app), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /create_floor":
                {
                    var body = ReadBody(req);
                    var levelId = body.Value<long>("level_id");
                    var boundary = body["boundary_mm"] != null ? body["boundary_mm"].ToObject<double[][]>() : null;
                    var floorType = body.Value<string>("floor_type") ?? "";
                    var data = _handler.Run(app => McpOps.CreateFloor(app, levelId, boundary, floorType), 180000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /floors":
                {
                    var data = _handler.Run(app => McpOps.ListFloors(app), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /create_ceiling":
                {
                    var body = ReadBody(req);
                    var levelId = body.Value<long>("level_id");
                    var boundary = body["boundary_mm"] != null ? body["boundary_mm"].ToObject<double[][]>() : null;
                    var ceilingType = body.Value<string>("ceiling_type") ?? "";
                    var data = _handler.Run(app => McpOps.CreateCeiling(app, levelId, boundary, ceilingType), 180000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /ceilings":
                {
                    var data = _handler.Run(app => McpOps.ListCeilings(app), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /create_roof":
                {
                    var body = ReadBody(req);
                    var levelId = body.Value<long>("level_id");
                    var boundary = body["boundary_mm"] != null ? body["boundary_mm"].ToObject<double[][]>() : null;
                    var roofType = body.Value<string>("roof_type") ?? "";
                    var data = _handler.Run(app => McpOps.CreateRoof(app, levelId, boundary, roofType), 180000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /roofs":
                {
                    var data = _handler.Run(app => McpOps.ListRoofs(app), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /move_element":
                {
                    var body = ReadBody(req);
                    var id = body.Value<long>("element_id");
                    var delta = body["delta_mm"] != null ? body["delta_mm"].ToObject<double[]>() : new[] { 0.0, 0.0, 0.0 };
                    var data = _handler.Run(app => McpOps.MoveElement(app, id, delta), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /rotate_element":
                {
                    var body = ReadBody(req);
                    var id = body.Value<long>("element_id");
                    var center = body["center_mm"] != null ? body["center_mm"].ToObject<double[]>() : new[] { 0.0, 0.0, 0.0 };
                    var angle = body.Value<double>("angle_deg");
                    var data = _handler.Run(app => McpOps.RotateElement(app, id, center, angle), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /copy_element":
                {
                    var body = ReadBody(req);
                    var id = body.Value<long>("element_id");
                    var delta = body["delta_mm"] != null ? body["delta_mm"].ToObject<double[]>() : new[] { 0.0, 0.0, 0.0 };
                    var data = _handler.Run(app => McpOps.CopyElement(app, id, delta), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /mirror_element":
                {
                    var body = ReadBody(req);
                    var id = body.Value<long>("element_id");
                    var axis = body["axis_mm"] != null ? body["axis_mm"].ToObject<double[][]>() : null;
                    var data = _handler.Run(app => McpOps.MirrorElement(app, id, axis), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /measure_distance":
                {
                    var q = ParseQuery(req.Url.Query);
                    var id1 = GetInt(q, "id1", 0);
                    var id2 = GetInt(q, "id2", 0);
                    var data = _handler.Run(app => McpOps.MeasureDistance(app, id1, id2), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /bounding_box":
                {
                    var q = ParseQuery(req.Url.Query);
                    var id = GetInt(q, "id", 0);
                    var data = _handler.Run(app => McpOps.GetBoundingBox(app, id), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /categories":
                {
                    var data = _handler.Run(app => McpOps.ListCategories(app), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /create_view_3d":
                {
                    var body = ReadBody(req);
                    var name = body.Value<string>("name") ?? "";
                    var data = _handler.Run(app => McpOps.CreateView3D(app, name), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /set_active_view":
                {
                    var body = ReadBody(req);
                    var view = body.Value<string>("view") ?? "";
                    var data = _handler.Run(app => McpOps.SetActiveView(app, view), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }

            case "GET /families":
                {
                    var q = ParseQuery(req.Url.Query);
                    var category = q.TryGetValue("category", out var c) ? c : "";
                    var data = _handler.Run(app => McpOps.ListFamilies(app, category), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /instance_params":
                {
                    var q = ParseQuery(req.Url.Query);
                    var id = GetInt(q, "id", 0);
                    var data = _handler.Run(app => McpOps.GetInstanceParams(app, id), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /set_instance_param":
                {
                    var body = ReadBody(req);
                    var id = body.Value<long>("instance_id");
                    var name = body.Value<string>("param_name") ?? "";
                    var value = body["value"];
                    object v = null;
                    if (value != null)
                    {
                        if (value.Type == JTokenType.Float || value.Type == JTokenType.Integer) v = value.Value<double>();
                        else if (value.Type == JTokenType.Boolean) v = value.Value<bool>();
                        else v = value.ToString();
                    }
                    var data = _handler.Run(app => McpOps.SetInstanceParam(app, id, name, v), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /create_schedule":
                {
                    var body = ReadBody(req);
                    var cat = body.Value<int>("category_id");
                    var name = body.Value<string>("name") ?? "";
                    var fields = body["fields"] != null ? body["fields"].ToObject<List<string>>() : new List<string>();
                    var data = _handler.Run(app => McpOps.CreateSchedule(app, cat, name, fields, null, null, null), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /modify_schedule":
                {
                    var body = ReadBody(req);
                    var id = body.Value<long>("schedule_id");
                    var fields = body["fields"] != null ? body["fields"].ToObject<List<string>>() : null;
                    var data = _handler.Run(app => McpOps.ModifySchedule(app, id, fields, null, null, null), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /refresh_schedule":
                {
                    var body = ReadBody(req);
                    long? id = body["schedule_id"] != null ? body.Value<long>("schedule_id") : (long?)null;
                    var data = _handler.Run(app => McpOps.RefreshSchedule(app, id), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /backup_document":
                {
                    var body = ReadBody(req);
                    var backupPath = body.Value<string>("path");
                    var data = _handler.Run(app => McpOps.BackupDocument(app, backupPath), 300000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /dry_run":
                {
                    var body = ReadBody(req);
                    var op = body.Value<string>("operation") ?? "";
                    var args = body["args"] != null ? body["args"].ToObject<Dictionary<string, object>>() : new Dictionary<string, object>();
                    var data = _handler.Run(app => McpOps.DryRun(app, op, args), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /undo_last":
                {
                    var data = _handler.Run(app => McpOps.UndoLast(app), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }

            case "GET /pipe_types":
                {
                    var data = _handler.Run(app => McpOps.ListPipeTypes(app), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /piping_systems":
                {
                    var data = _handler.Run(app => McpOps.ListPipingSystems(app), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /create_pipe":
                {
                    var body = ReadBody(req);
                    var systemType = body.Value<string>("system_type") ?? "";
                    var pipeType = body.Value<string>("pipe_type") ?? "";
                    var levelId = body.Value<long>("level_id");
                    var start = body["start_mm"] != null ? body["start_mm"].ToObject<double[]>() : new[] { 0.0, 0.0, 0.0 };
                    var end = body["end_mm"] != null ? body["end_mm"].ToObject<double[]>() : new[] { 0.0, 0.0, 0.0 };
                    var diameter = body.Value<double>("diameter_mm");
                    var data = _handler.Run(app => McpOps.CreatePipe(app, systemType, pipeType, levelId, start, end, diameter), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /connect_pipes":
                {
                    var body = ReadBody(req);
                    var id1 = body.Value<long>("id1");
                    var id2 = body.Value<long>("id2");
                    var data = _handler.Run(app => McpOps.ConnectPipes(app, id1, id2), 120000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /duct_types":
                {
                    var data = _handler.Run(app => McpOps.ListDuctTypes(app), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /mechanical_systems":
                {
                    var data = _handler.Run(app => McpOps.ListMechanicalSystems(app), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "POST /create_duct":
                {
                    var body = ReadBody(req);
                    var systemType = body.Value<string>("system_type") ?? "";
                    var ductType = body.Value<string>("duct_type") ?? "";
                    var levelId = body.Value<long>("level_id");
                    var start = body["start_mm"] != null ? body["start_mm"].ToObject<double[]>() : new[] { 0.0, 0.0, 0.0 };
                    var end = body["end_mm"] != null ? body["end_mm"].ToObject<double[]>() : new[] { 0.0, 0.0, 0.0 };
                    var width = body.Value<double>("width_mm");
                    var height = body.Value<double>("height_mm");
                    var data = _handler.Run(app => McpOps.CreateDuct(app, systemType, ductType, levelId, start, end, width, height), 120000);
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
