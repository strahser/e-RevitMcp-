# -*- coding: utf-8 -*-
"""Smoke-test RevitMcp modeling tools against a running Revit bridge.

Usage:
    python scripts/test_modeling.py            # run all checks (read-only + create)
    python scripts/test_modeling.py --readonly  # only read-only queries
    python scripts/test_modeling.py --create    # create a tiny test wall + room

Requires Revit 2024 running with RevitMcp addin and an open document.
"""
import json, os, sys, time, urllib.request, urllib.parse

BASE = os.environ.get("REVIT_MCP_URL", "http://127.0.0.1:17871").rstrip("/")

def api(method, path, body=None, timeout=120):
    url = BASE + path
    data = None
    headers = {"Accept": "application/json"}
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json; charset=utf-8"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    with urllib.request.urlopen(req, timeout=timeout) as r:
        payload = json.loads(r.read().decode("utf-8"))
    if not payload.get("ok"):
        raise RuntimeError(payload.get("error"))
    return payload.get("data")

def main():
    readonly = "--readonly" in sys.argv
    create = "--create" in sys.argv or (not readonly and "--readonly" not in sys.argv)

    print("== ping ==")
    print(api("GET", "/ping"))

    print("== doc info ==")
    print(api("GET", "/doc"))

    print("== levels ==")
    levels = api("GET", "/levels")
    print(levels)
    if not levels.get("items"):
        print("No levels; creating one")
        api("POST", "/create_level", {"name": "Level 1", "elevation_mm": 0})

    print("== wall types ==")
    wts = api("GET", "/wall_types")
    print("count:", wts.get("count"))

    if create:
        print("== create wall type ==")
        wt = api("POST", "/create_wall_type", {"name": "TestWall 200", "layers": [
            {"function": "Structure", "material": "Concrete", "thickness_mm": 200}]})
        print(wt)
        wt_id = wt["name"]

        print("== create wall ==")
        wall = api("POST", "/create_wall", {
            "type_id": wt_id, "base_level": levels["items"][0]["name"] if levels.get("items") else "Level 1",
            "start_mm": [0, 0], "end_mm": [5000, 0]})
        print(wall)
        wall_id = wall["id"]

        print("== cut opening ==")
        op = api("POST", "/cut_opening", {"host_wall_id": wall_id, "width_mm": 1200, "height_mm": 1500, "sill_mm": 900, "position_mm": 2500})
        print(op)

        print("== create room ==")
        room = api("POST", "/create_room", {"level_id": levels["items"][0]["id"] if levels.get("items") else None,
            "location_mm": [1000, 1000], "number": "1", "name": "Test Room"})
        print(room)

        print("== query elements ==")
        q = api("GET", "/query?category=OST_Walls&limit=10")
        print("walls:", q.get("count"))

        print("== get element ==")
        print(api("GET", "/element?id=%d" % wall_id))

        print("== delete elements ==")
        print(api("POST", "/delete", {"ids": [wall_id]}))

    print("== schedules ==")
    sch = api("GET", "/schedules")
    print("schedules:", sch.get("count"))

    print("== DONE ==")

if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print("ERROR:", e)
        sys.exit(1)
"