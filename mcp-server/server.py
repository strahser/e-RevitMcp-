# -*- coding: utf-8 -*-
"""Revit MCP server (stdio) for OpenCode.

Bridges OpenCode -> localhost HTTP bridge hosted inside Revit by
RevitMcp.Addin (E:\\ПлагиныРевит\\RevitMcp\\RevitMcp.Addin).

Stdio JSON-RPC (MCP 2024-11-05), pure stdlib, no external deps
(same transport as DSH opencode-runner, proven with OpenCode).

Tools:
  revit_ping            - check Revit + addin are alive
  revit_open_document   - open an .rvt (optionally launch Revit if down)
  revit_close_document  - close active document
  revit_doc_info        - title/path/counts of active document
  revit_list_rooms      - rooms + MEP spaces (id/number/name/level/area/volume)
  revit_list_levels     - levels (name/elevation)
  revit_commands        - list HeatLoss plugin command keys
  revit_run_heatloss    - run HeatLoss plugin command (calculations/modeling/...)

Usage: python -X utf8 server.py
Env: REVIT_MCP_URL (default http://127.0.0.1:17871), REVIT_EXE (override),
     REVIT_DEFAULT_MODEL (override default .rvt path)
"""
import json
import os
import subprocess
import sys
import time
import urllib.parse
import urllib.request

PROTOCOL_VERSION = "2024-11-05"
SERVER_NAME = "revit-mcp"
SERVER_VERSION = "0.1.0"

BASE_URL = os.environ.get("REVIT_MCP_URL", "http://127.0.0.1:17871").rstrip("/")

REVIT_CANDIDATES = [
    r"C:\Program Files\Autodesk\Revit 2024\Revit.exe",
    r"C:\Program Files\Autodesk\Revit 2025\Revit.exe",
]
if os.environ.get("REVIT_EXE"):
    REVIT_CANDIDATES.insert(0, os.environ["REVIT_EXE"])

DEFAULT_MODEL = os.environ.get(
    "REVIT_DEFAULT_MODEL",
    r"E:\ПлагиныРевит\Данные теплотехнический расчет\newBuilding\HvackFinal.rvt",
)


class RevitDown(RuntimeError):
    pass


def _api(method, path, body=None, timeout=180):
    url = BASE_URL + path
    data = None
    headers = {"Accept": "application/json"}
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json; charset=utf-8"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            payload = json.loads(resp.read().decode("utf-8"))
    except OSError as ex:
        raise RevitDown(
            "Revit bridge unreachable at %s (%s). Start Revit 2024 with the "
            "RevitMcp addin, or call revit_open_document with "
            "launch_if_needed=true." % (BASE_URL, ex)
        )
    if not payload.get("ok"):
        raise RuntimeError("Revit bridge error: %s" % payload.get("error"))
    return payload.get("data")


def _ping_ok():
    try:
        _api("GET", "/ping", timeout=10)
        return True
    except Exception:
        return False


def _launch_revit(path):
    exe = next((c for c in REVIT_CANDIDATES if os.path.isfile(c)), None)
    if exe is None:
        raise RuntimeError("Revit.exe not found (checked: %s)" % "; ".join(REVIT_CANDIDATES))
    if not os.path.isfile(path):
        raise RuntimeError("Model file not found: %s" % path)
    # Non-blocking: Revit is single-instance per version, file opens in it.
    subprocess.Popen([exe, path])


TOOLS = [
    {"name": "revit_ping",
     "description": "Check that Revit 2024 + RevitMcp addin bridge is alive. Returns Revit version info.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "revit_open_document",
     "description": ("Open an .rvt model in Revit. Launches Revit 2024 with the file if the bridge "
                     "is down and launch_if_needed=true (waits up to wait_sec). Default path is HvackFinal.rvt."),
     "inputSchema": {"type": "object", "properties": {
         "path": {"type": "string", "description": "Full path to .rvt file"},
         "detach_from_central": {"type": "boolean", "description": "Detach from central (default false)"},
         "audit": {"type": "boolean", "description": "Audit on open (default false)"},
         "launch_if_needed": {"type": "boolean", "description": "Launch Revit if bridge is down (default true)"},
         "wait_sec": {"type": "integer", "description": "Wait for bridge after launch, sec (default 300)"}},
         "required": []}},
    {"name": "revit_close_document",
     "description": "Close the active Revit document (save=false discards changes).",
     "inputSchema": {"type": "object", "properties": {
         "save": {"type": "boolean", "description": "Save before closing (default false)"}}}},
    {"name": "revit_doc_info",
     "description": "Active document info: title, path, isModified, room/space/level counts.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "revit_list_rooms",
     "description": "List rooms + MEP spaces: id/number/name/level/area_ft2/volume_ft3 (paged).",
     "inputSchema": {"type": "object", "properties": {
         "limit": {"type": "integer", "description": "Page size 1..500 (default 50)"},
         "offset": {"type": "integer", "description": "Offset (default 0)"}}}},
    {"name": "revit_list_levels",
     "description": "List levels: name + elevation_ft.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "revit_commands",
     "description": "List HeatLoss plugin command keys available to revit_run_heatloss.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "revit_run_heatloss",
     "description": ("Run a HeatLoss plugin window inside Revit. Keys: settings, modeling, "
                     "calculations (default), main, reports, tools, about, snapshot. "
                     "Opens the plugin UI modelessly; returns Succeeded/Failed + message."),
     "inputSchema": {"type": "object", "properties": {
         "command": {"type": "string", "description": "HeatLoss command key (default calculations)"}}}},
]


def tool_call(name, args):
    args = args or {}
    if name == "revit_ping":
        return _api("GET", "/ping", timeout=15)
    if name == "revit_open_document":
        path = args.get("path") or DEFAULT_MODEL
        launch = args.get("launch_if_needed", True)
        wait_sec = int(args.get("wait_sec") or 300)
        if launch and not _ping_ok():
            _launch_revit(path)
            deadline = time.time() + wait_sec
            while time.time() < deadline:
                time.sleep(5)
                if _ping_ok():
                    break
            else:
                raise RuntimeError("Timed out waiting for Revit bridge after launch")
        return _api("POST", "/open",
                    {"path": path,
                     "detach": bool(args.get("detach_from_central", False)),
                     "audit": bool(args.get("audit", False))},
                    timeout=600)
    if name == "revit_close_document":
        return _api("POST", "/close", {"save": bool(args.get("save", False))}, timeout=180)
    if name == "revit_doc_info":
        return _api("GET", "/doc", timeout=120)
    if name == "revit_list_rooms":
        limit = max(1, min(500, int(args.get("limit") or 50)))
        offset = max(0, int(args.get("offset") or 0))
        q = urllib.parse.urlencode({"limit": limit, "offset": offset})
        return _api("GET", "/rooms?%s" % q, timeout=120)
    if name == "revit_list_levels":
        return _api("GET", "/levels", timeout=60)
    if name == "revit_commands":
        return _api("GET", "/commands", timeout=30)
    if name == "revit_run_heatloss":
        return _api("POST", "/run", {"command": args.get("command") or "calculations"}, timeout=300)
    raise ValueError("unknown tool: %s" % name)


def handle(msg):
    req_id = msg.get("id")
    method = msg.get("method")
    params = msg.get("params") or {}
    if method == "initialize":
        return {"protocolVersion": PROTOCOL_VERSION, "capabilities": {"tools": {}},
                "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION}}
    if method == "notifications/initialized":
        return None
    if method == "ping":
        return {}
    if method == "tools/list":
        return {"tools": TOOLS}
    if method == "tools/call":
        try:
            result = tool_call(params.get("name"), params.get("arguments"))
            return {"content": [{"type": "text", "text": json.dumps(result, ensure_ascii=False)}],
                    "isError": False}
        except Exception as e:
            return {"content": [{"type": "text",
                                 "text": json.dumps({"error": str(e)}, ensure_ascii=False)}],
                    "isError": True}
    raise ValueError("unsupported method: %s" % method)


def main():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError:
            continue
        try:
            resp = handle(msg)
        except Exception as e:
            resp = {"error": {"code": -32603, "message": str(e)}}
        if resp is not None:
            if "id" in msg:
                # JSON-RPC 2.0: payload MUST be nested under "result" (or "error").
                # Without this the official MCP SDK rejects the frame
                # (invalid_union / expected object at path ["result"]) and
                # initialize times out, so no tools ever register.
                if isinstance(resp, dict) and set(resp.keys()) == {"error"}:
                    out = {"jsonrpc": "2.0", "id": msg["id"], "error": resp["error"]}
                else:
                    out = {"jsonrpc": "2.0", "id": msg["id"], "result": resp}
                sys.stdout.write(json.dumps(out, ensure_ascii=False) + "\n")
                sys.stdout.flush()


if __name__ == "__main__":
    main()
