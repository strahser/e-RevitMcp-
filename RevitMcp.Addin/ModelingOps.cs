using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace RevitMcp;

internal static partial class McpOps
{
    // ---------- Levels (create) ----------
    public static object CreateLevel(UIApplication app, string name, double elevationMm)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var elevFt = elevationMm / 304.8;
        using (var tx = new Transaction(doc, "Create Level"))
        {
            tx.Start();
            var level = Level.Create(doc, elevFt);
            level.Name = name;
            tx.Commit();
            return new Dictionary<string, object> { { "id", level.Id.Value }, { "name", level.Name }, { "elevation_ft", Math.Round(level.Elevation, 4) } };
        }
    }

    // ---------- Wall types ----------
    public static object ListWallTypes(UIApplication app)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var items = new List<Dictionary<string, object>>();
        foreach (WallType wt in new FilteredElementCollector(doc).OfClass(typeof(WallType)))
        {
            double widthMm = 0;
            try { widthMm = wt.Width * 304.8; } catch (Exception) { }
            items.Add(new Dictionary<string, object>
            {
                { "id", wt.Id.Value },
                { "name", wt.Name },
                { "width_mm", Math.Round(widthMm, 1) },
                { "kind", wt.Kind.ToString() },
            });
        }
        return new Dictionary<string, object> { { "count", items.Count }, { "items", items } };
    }

    public static object CreateWallType(UIApplication app, string name, List<Dictionary<string, object>> layers)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        using (var tx = new Transaction(doc, "Create WallType"))
        {
            tx.Start();
            var baseType = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault();
            if (baseType == null) throw new InvalidOperationException("No base wall type in document");
            var newType = baseType.Duplicate(name) as WallType;
            if (layers != null && layers.Count > 0)
            {
                var csLayers = new List<CompoundStructureLayer>();
                foreach (var l in layers)
                {
                    var funcStr = (l.ContainsKey("function") ? l["function"]?.ToString() : "Structure") ?? "Structure";
                    var func = funcStr switch
                    {
                        "Structure" => MaterialFunctionAssignment.Structure,
                        "Insulation" => MaterialFunctionAssignment.Insulation,
                        "Finish1" => MaterialFunctionAssignment.Finish1,
                        "Finish2" => MaterialFunctionAssignment.Finish2,
                        "Membrane" => MaterialFunctionAssignment.Membrane,
                        _ => MaterialFunctionAssignment.Structure
                    };
                    var thickMm = l.ContainsKey("thickness_mm") ? Convert.ToDouble(l["thickness_mm"]) : 100.0;
                    csLayers.Add(new CompoundStructureLayer(thickMm / 304.8, func, ElementId.InvalidElementId));
                }
                var cs = CompoundStructure.CreateSimpleCompoundStructure(csLayers);
                newType.SetCompoundStructure(cs);
            }
            tx.Commit();
            return new Dictionary<string, object> { { "id", newType.Id.Value }, { "name", newType.Name } };
        }
    }

    // ---------- Walls ----------
    public static object CreateWall(UIApplication app, string typeId, string baseLevelName, string topLevelName, double[] startMm, double[] endMm, bool flip)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var wallType = FindWallType(doc, typeId);
        if (wallType == null) throw new InvalidOperationException("Wall type not found: " + typeId);
        var baseLevel = FindLevel(doc, baseLevelName);
        if (baseLevel == null) throw new InvalidOperationException("Base level not found: " + baseLevelName);
        Level topLevel = null;
        if (!string.IsNullOrWhiteSpace(topLevelName)) topLevel = FindLevel(doc, topLevelName);

        using (var tx = new Transaction(doc, "Create Wall"))
        {
            tx.Start();
            var s = new XYZ(startMm[0] / 304.8, startMm[1] / 304.8, 0);
            var e = new XYZ(endMm[0] / 304.8, endMm[1] / 304.8, 0);
            var curve = Line.CreateBound(s, e);
            double height = 3.0;
            if (topLevel != null) height = topLevel.Elevation - baseLevel.Elevation;
            if (height <= 0) height = 3000.0 / 304.8;
            var wall = Wall.Create(doc, curve, wallType.Id, baseLevel.Id, height, 0, flip, false);
            tx.Commit();
            return new Dictionary<string, object> { { "id", wall.Id.Value }, { "uniqueId", wall.UniqueId }, { "length_mm", Math.Round(wall.Width * 304.8, 1) } };
        }
    }

    public static object GetElement(UIApplication app, long id)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var el = doc.GetElement(new ElementId(id));
        if (el == null) throw new InvalidOperationException("Element not found: " + id);
        var dict = new Dictionary<string, object>
        {
            { "id", el.Id.Value },
            { "uniqueId", el.UniqueId },
            { "name", el.Name },
            { "category", el.Category != null ? el.Category.Name : "" },
            { "type", el.GetType().Name },
        };
        // location
        if (el.Location is LocationCurve lc)
        {
            dict["start_mm"] = new[] { lc.Curve.GetEndPoint(0).X * 304.8, lc.Curve.GetEndPoint(0).Y * 304.8 };
            dict["end_mm"] = new[] { lc.Curve.GetEndPoint(1).X * 304.8, lc.Curve.GetEndPoint(1).Y * 304.8 };
        }
        else if (el.Location is LocationPoint lp)
        {
            dict["position_mm"] = new[] { lp.Point.X * 304.8, lp.Point.Y * 304.8, lp.Point.Z * 304.8 };
        }
        // params (instance)
        var paramsList = new List<Dictionary<string, object>>();
        foreach (Parameter p in el.Parameters)
        {
            try { paramsList.Add(new Dictionary<string, object> { { "name", p.Definition.Name }, { "value", ParamValue(p) }, { "isReadOnly", p.IsReadOnly } }); } catch (Exception) { }
        }
        dict["parameters"] = paramsList;
        return dict;
    }

    public static object QueryElements(UIApplication app, string category, string levelName, int limit)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var items = new List<Dictionary<string, object>>();
        BuiltInCategory bic;
        if (!Enum.TryParse(category, out bic)) bic = BuiltInCategory.OST_Walls;
        var col = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType();
        var all = col.ToList();
        if (!string.IsNullOrWhiteSpace(levelName))
        {
            var lvl = FindLevel(doc, levelName);
            if (lvl != null) all = all.Where(e => { try { return e.LevelId == lvl.Id; } catch (Exception) { return false; } }).ToList();
        }
        foreach (var e in all.Take(Math.Max(1, Math.Min(5000, limit > 0 ? limit : 500))))
        {
            items.Add(new Dictionary<string, object>
            {
                { "id", e.Id.Value },
                { "name", e.Name },
                { "category", e.Category != null ? e.Category.Name : "" },
                { "type", e.GetType().Name },
            });
        }
        return new Dictionary<string, object> { { "count", items.Count }, { "items", items } };
    }

    public static object DeleteElements(UIApplication app, List<long> ids)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var deleted = new List<long>();
        var failed = new List<long>();
        using (var tx = new Transaction(doc, "Delete Elements"))
        {
            tx.Start();
            foreach (var id in ids ?? new List<long>())
            {
                try
                {
                    doc.Delete(new ElementId(id));
                    deleted.Add(id);
                }
                catch (Exception) { failed.Add(id); }
            }
            tx.Commit();
        }
        return new Dictionary<string, object> { { "deleted", deleted }, { "failed", failed } };
    }

    public static object CutOpening(UIApplication app, long hostWallId, double widthMm, double heightMm, double sillMm, double positionMm)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var wall = doc.GetElement(new ElementId(hostWallId)) as Wall;
        if (wall == null) throw new InvalidOperationException("Host wall not found: " + hostWallId);
        using (var tx = new Transaction(doc, "Cut Opening"))
        {
            tx.Start();
            var loc = wall.Location as LocationCurve;
            var curve = loc.Curve;
            var mid = curve.Evaluate(0.5, true);
            var dir = (curve.GetEndPoint(1) - curve.GetEndPoint(0)).Normalize();
            var halfW = (widthMm / 304.8) / 2.0;
            var height = heightMm / 304.8;
            var sill = sillMm / 304.8;
            var posFt = positionMm / 304.8;
            var p1 = mid + dir * (posFt - halfW) + XYZ.BasisZ * sill;
            var p2 = p1 + dir * (halfW * 2) + XYZ.BasisZ * height;
            var opening = doc.Create.NewOpening(wall, p1, p2);
            tx.Commit();
            return new Dictionary<string, object> { { "id", opening.Id.Value }, { "host_wall_id", hostWallId } };
        }
    }

    public static object PlaceFamily(UIApplication app, string familyName, string typeName, long? hostWallId, double[] locationMm, double rotationDeg, Dictionary<string, object> parameters)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        // find family symbol
        FamilySymbol symbol = null;
        foreach (FamilySymbol fs in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)))
        {
            if (fs.Family.Name == familyName && fs.Name == typeName) { symbol = fs; break; }
        }
        if (symbol == null) throw new InvalidOperationException("Family/type not found: " + familyName + " : " + typeName);
        using (var tx = new Transaction(doc, "Place Family"))
        {
            tx.Start();
            if (!symbol.IsActive) symbol.Activate();
            var loc = new XYZ(locationMm[0] / 304.8, locationMm[1] / 304.8, (locationMm.Length > 2 ? locationMm[2] : 0) / 304.8);
            FamilyInstance fi;
            if (hostWallId.HasValue)
            {
                var wall = doc.GetElement(new ElementId(hostWallId.Value)) as Wall;
                fi = doc.Create.NewFamilyInstance(loc, symbol, wall, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            }
            else
            {
                var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();
                fi = doc.Create.NewFamilyInstance(loc, symbol, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            }
            if (Math.Abs(rotationDeg) > 1e-9)
            {
                var axis = Line.CreateBound(loc, loc + XYZ.BasisZ);
                ElementTransformUtils.RotateElement(doc, fi.Id, axis, rotationDeg * Math.PI / 180.0);
            }
            if (parameters != null)
            {
                foreach (var kv in parameters)
                {
                    var p = fi.LookupParameter(kv.Key);
                    if (p != null && !p.IsReadOnly)
                    {
                        if (kv.Value is double d) p.Set(d);
                        else if (kv.Value is int i) p.Set(i);
                        else if (kv.Value is string s) p.Set(s);
                        else if (kv.Value is bool b) p.Set(b ? 1 : 0);
                    }
                }
            }
            tx.Commit();
            return new Dictionary<string, object> { { "id", fi.Id.Value }, { "uniqueId", fi.UniqueId } };
        }
    }

    public static object LoadFamily(UIApplication app, string rfaPath)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        using (var tx = new Transaction(doc, "Load Family"))
        {
            tx.Start();
            Family fam;
            if (!doc.LoadFamily(rfaPath, out fam))
                throw new InvalidOperationException("Failed to load family: " + rfaPath);
            var types = new List<string>();
            foreach (FamilySymbol fs in fam.GetFamilySymbolIds().Select(id => doc.GetElement(id)))
                types.Add(fs.Name);
            tx.Commit();
            return new Dictionary<string, object> { { "family", fam.Name }, { "types", types } };
        }
    }

    // ---------- Schedules ----------
    public static object ListSchedules(UIApplication app)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var items = new List<Dictionary<string, object>>();
        foreach (ViewSchedule vs in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)))
        {
            var fields = new List<string>();
            try { for (int fi = 0; fi < vs.Definition.GetFieldCount(); fi++) { var f = vs.Definition.GetField(fi); if (f != null) fields.Add(f.GetName()); } } catch (Exception) { }
            items.Add(new Dictionary<string, object>
            {
                { "id", vs.Id.Value },
                { "name", vs.Name },
                { "categoryId", vs.Definition.CategoryId.Value },
                { "fields", fields },
            });
        }
        return new Dictionary<string, object> { { "count", items.Count }, { "items", items } };
    }

    public static object GetSchedule(UIApplication app, long scheduleId)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var vs = doc.GetElement(new ElementId(scheduleId)) as ViewSchedule;
        if (vs == null) throw new InvalidOperationException("Schedule not found: " + scheduleId);
        var fields = new List<string>();
        try { for (int fi = 0; fi < vs.Definition.GetFieldCount(); fi++) { var f = vs.Definition.GetField(fi); if (f != null) fields.Add(f.GetName()); } } catch (Exception) { }
        var rows = new List<List<string>>();
        var table = vs.GetTableData();
        for (int i = 0; i < table.GetSectionData(SectionType.Body).NumberOfRows; i++)
        {
            var row = new List<string>();
            for (int j = 0; j < table.GetSectionData(SectionType.Body).NumberOfColumns; j++)
            {
                try { row.Add(table.GetSectionData(SectionType.Body).GetCellText(i, j)); }
                catch (Exception) { row.Add(""); }
            }
            rows.Add(row);
        }
        return new Dictionary<string, object>
        {
            { "id", vs.Id.Value },
            { "name", vs.Name },
            { "fields", fields },
            { "rowCount", rows.Count },
            { "rows", rows },
        };
    }

    public static object DuplicateSchedule(UIApplication app, long sourceId, string newName)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var src = doc.GetElement(new ElementId(sourceId)) as ViewSchedule;
        if (src == null) throw new InvalidOperationException("Source schedule not found: " + sourceId);
        using (var tx = new Transaction(doc, "Duplicate Schedule"))
        {
            tx.Start();
            var newId = src.Duplicate(ViewDuplicateOption.Duplicate);
            var dup = doc.GetElement(newId) as ViewSchedule;
            if (dup != null) dup.Name = newName;
            tx.Commit();
            return new Dictionary<string, object> { { "id", dup.Id.Value }, { "name", dup.Name } };
        }
    }

    public static object ExportSchedule(UIApplication app, long scheduleId, string path)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var vs = doc.GetElement(new ElementId(scheduleId)) as ViewSchedule;
        if (vs == null) throw new InvalidOperationException("Schedule not found: " + scheduleId);
        if (string.IsNullOrWhiteSpace(path)) path = Path.Combine(Path.GetTempPath(), vs.Name + ".csv");
        // export via Revit's built-in schedule export
        var opts = new ViewScheduleExportOptions();
        vs.Export(Path.GetDirectoryName(path), Path.GetFileName(path), opts);
        return new Dictionary<string, object> { { "path", path } };
    }

    public static object CreateRoom(UIApplication app, long levelId, double[] locationMm, string number, string name)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var level = doc.GetElement(new ElementId(levelId)) as Level;
        if (level == null) throw new InvalidOperationException("Level not found: " + levelId);
        using (var tx = new Transaction(doc, "Create Room"))
        {
            tx.Start();
            var loc = new XYZ(locationMm[0] / 304.8, locationMm[1] / 304.8, level.Elevation);
            var room = doc.Create.NewRoom(level, new UV(loc.X, loc.Y));
            if (!string.IsNullOrWhiteSpace(number)) room.Number = number;
            if (!string.IsNullOrWhiteSpace(name)) room.Name = name;
            tx.Commit();
            return new Dictionary<string, object> { { "id", room.Id.Value }, { "number", room.Number }, { "name", room.Name } };
        }
    }
    private static WallType FindWallType(Document doc, string key)
    {
        return new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
            .FirstOrDefault(wt => wt.Name == key || wt.Id.Value.ToString() == key);
    }

    private static Level FindLevel(Document doc, string key)
    {
        return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .FirstOrDefault(l => l.Name == key || l.Id.Value.ToString() == key);
    }

    private static object ParamValue(Parameter p)
    {
        try
        {
            switch (p.StorageType)
            {
                case StorageType.Double: return p.AsDouble();
                case StorageType.Integer: return p.AsInteger();
                case StorageType.String: return p.AsString();
                case StorageType.ElementId: return p.AsElementId().Value;
                default: return p.AsValueString();
            }
        }
        catch (Exception) { return p.AsValueString(); }
    }
    // ---------- Phase 2: Grids ----------
    public static object CreateGrid(UIApplication app, string name, double[] startMm, double[] endMm)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        using (var tx = new Transaction(doc, "Create Grid"))
        {
            tx.Start();
            var s = new XYZ(startMm[0] / 304.8, startMm[1] / 304.8, 0);
            var e = new XYZ(endMm[0] / 304.8, endMm[1] / 304.8, 0);
            var grid = Grid.Create(doc, Line.CreateBound(s, e));
            if (!string.IsNullOrWhiteSpace(name)) grid.Name = name;
            tx.Commit();
            return new Dictionary<string, object> { { "id", grid.Id.Value }, { "name", grid.Name } };
        }
    }

    public static object ListGrids(UIApplication app)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var items = new List<Dictionary<string, object>>();
        foreach (Grid g in new FilteredElementCollector(doc).OfClass(typeof(Grid)))
        {
            var c = g.Curve;
            items.Add(new Dictionary<string, object>
            {
                { "id", g.Id.Value },
                { "name", g.Name },
                { "start_mm", new[] { c.GetEndPoint(0).X * 304.8, c.GetEndPoint(0).Y * 304.8 } },
                { "end_mm", new[] { c.GetEndPoint(1).X * 304.8, c.GetEndPoint(1).Y * 304.8 } },
            });
        }
        return new Dictionary<string, object> { { "count", items.Count }, { "items", items } };
    }

    // ---------- Phase 2: Floors / Roofs / Ceilings ----------
    private static CurveLoop BoundaryToLoop(double[][] boundaryMm)
    {
        var loop = new CurveLoop();
        int n = boundaryMm.Length;
        for (int i = 0; i < n; i++)
        {
            var a = boundaryMm[i];
            var b = boundaryMm[(i + 1) % n];
            var sa = new XYZ(a[0] / 304.8, a[1] / 304.8, 0);
            var sb = new XYZ(b[0] / 304.8, b[1] / 304.8, 0);
            loop.Append(Line.CreateBound(sa, sb));
        }
        return loop;
    }

    public static object CreateFloor(UIApplication app, long levelId, double[][] boundaryMm, string floorTypeName)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var level = doc.GetElement(new ElementId(levelId)) as Level;
        if (level == null) throw new InvalidOperationException("Level not found: " + levelId);
        FloorType ft = null;
        if (!string.IsNullOrWhiteSpace(floorTypeName))
            ft = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().FirstOrDefault(t => t.Name == floorTypeName);
        if (ft == null) ft = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().FirstOrDefault();
        if (ft == null) throw new InvalidOperationException("No floor type in document");
        using (var tx = new Transaction(doc, "Create Floor"))
        {
            tx.Start();
            var floor = Floor.Create(doc, new List<CurveLoop> { BoundaryToLoop(boundaryMm) }, ft.Id, level.Id);
            tx.Commit();
            return new Dictionary<string, object> { { "id", floor.Id.Value }, { "uniqueId", floor.UniqueId } };
        }
    }

    public static object ListFloors(UIApplication app)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var items = new List<Dictionary<string, object>>();
        foreach (Floor f in new FilteredElementCollector(doc).OfClass(typeof(Floor)))
            items.Add(new Dictionary<string, object> { { "id", f.Id.Value }, { "name", f.Name } });
        return new Dictionary<string, object> { { "count", items.Count }, { "items", items } };
    }

    public static object CreateCeiling(UIApplication app, long levelId, double[][] boundaryMm, string ceilingTypeName)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var level = doc.GetElement(new ElementId(levelId)) as Level;
        if (level == null) throw new InvalidOperationException("Level not found: " + levelId);
        CeilingType ct = null;
        if (!string.IsNullOrWhiteSpace(ceilingTypeName))
            ct = new FilteredElementCollector(doc).OfClass(typeof(CeilingType)).Cast<CeilingType>().FirstOrDefault(t => t.Name == ceilingTypeName);
        if (ct == null) ct = new FilteredElementCollector(doc).OfClass(typeof(CeilingType)).Cast<CeilingType>().FirstOrDefault();
        if (ct == null) throw new InvalidOperationException("No ceiling type in document");
        using (var tx = new Transaction(doc, "Create Ceiling"))
        {
            tx.Start();
            var ceiling = Ceiling.Create(doc, new List<CurveLoop> { BoundaryToLoop(boundaryMm) }, ct.Id, level.Id);
            tx.Commit();
            return new Dictionary<string, object> { { "id", ceiling.Id.Value }, { "uniqueId", ceiling.UniqueId } };
        }
    }

    public static object ListCeilings(UIApplication app)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var items = new List<Dictionary<string, object>>();
        foreach (Ceiling c in new FilteredElementCollector(doc).OfClass(typeof(Ceiling)))
            items.Add(new Dictionary<string, object> { { "id", c.Id.Value }, { "name", c.Name } });
        return new Dictionary<string, object> { { "count", items.Count }, { "items", items } };
    }

    public static object CreateRoof(UIApplication app, long levelId, double[][] boundaryMm, string roofTypeName)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var level = doc.GetElement(new ElementId(levelId)) as Level;
        if (level == null) throw new InvalidOperationException("Level not found: " + levelId);
        RoofType rt = null;
        if (!string.IsNullOrWhiteSpace(roofTypeName))
            rt = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().FirstOrDefault(t => t.Name == roofTypeName);
        if (rt == null) rt = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().FirstOrDefault();
        if (rt == null) throw new InvalidOperationException("No roof type in document");
        using (var tx = new Transaction(doc, "Create Roof"))
        {
            tx.Start();
            var curveArray = new CurveArray();
            int n = boundaryMm.Length;
            for (int i = 0; i < n; i++)
            {
                var a = boundaryMm[i];
                var b = boundaryMm[(i + 1) % n];
                curveArray.Append(Line.CreateBound(
                    new XYZ(a[0] / 304.8, a[1] / 304.8, 0),
                    new XYZ(b[0] / 304.8, b[1] / 304.8, 0)));
            }
            ModelCurveArray footPrint;
            var roof = doc.Create.NewFootPrintRoof(curveArray, level, rt, out footPrint);
            tx.Commit();
            return new Dictionary<string, object> { { "id", roof.Id.Value }, { "uniqueId", roof.UniqueId } };
        }
    }

    public static object ListRoofs(UIApplication app)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var items = new List<Dictionary<string, object>>();
        foreach (RoofBase r in new FilteredElementCollector(doc).OfClass(typeof(RoofBase)))
            items.Add(new Dictionary<string, object> { { "id", r.Id.Value }, { "name", r.Name } });
        return new Dictionary<string, object> { { "count", items.Count }, { "items", items } };
    }

    // ---------- Phase 2: Edit geometry ----------
    public static object MoveElement(UIApplication app, long elementId, double[] deltaMm)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var id = new ElementId(elementId);
        if (doc.GetElement(id) == null) throw new InvalidOperationException("Element not found: " + elementId);
        using (var tx = new Transaction(doc, "Move Element"))
        {
            tx.Start();
            ElementTransformUtils.MoveElement(doc, id, new XYZ(deltaMm[0] / 304.8, deltaMm[1] / 304.8, (deltaMm.Length > 2 ? deltaMm[2] : 0) / 304.8));
            tx.Commit();
        }
        return new Dictionary<string, object> { { "ok", true }, { "id", elementId } };
    }

    public static object RotateElement(UIApplication app, long elementId, double[] centerMm, double angleDeg)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var id = new ElementId(elementId);
        var el = doc.GetElement(id);
        if (el == null) throw new InvalidOperationException("Element not found: " + elementId);
        var center = new XYZ(centerMm[0] / 304.8, centerMm[1] / 304.8, (centerMm.Length > 2 ? centerMm[2] : 0) / 304.8);
        using (var tx = new Transaction(doc, "Rotate Element"))
        {
            tx.Start();
            var axis = Line.CreateBound(center, center + XYZ.BasisZ);
            ElementTransformUtils.RotateElement(doc, id, axis, angleDeg * Math.PI / 180.0);
            tx.Commit();
        }
        return new Dictionary<string, object> { { "ok", true }, { "id", elementId } };
    }

    public static object CopyElement(UIApplication app, long elementId, double[] deltaMm)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var id = new ElementId(elementId);
        if (doc.GetElement(id) == null) throw new InvalidOperationException("Element not found: " + elementId);
        using (var tx = new Transaction(doc, "Copy Element"))
        {
            tx.Start();
            var newIds = ElementTransformUtils.CopyElement(doc, id, new XYZ(deltaMm[0] / 304.8, deltaMm[1] / 304.8, (deltaMm.Length > 2 ? deltaMm[2] : 0) / 304.8));
            tx.Commit();
            var newId = newIds.FirstOrDefault().Value;
            var newEl = doc.GetElement(new ElementId(newId));
            return new Dictionary<string, object> { { "id", newId }, { "uniqueId", newEl != null ? newEl.UniqueId : "" } };
        }
    }

    public static object MirrorElement(UIApplication app, long elementId, double[][] axisPointsMm)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var id = new ElementId(elementId);
        if (doc.GetElement(id) == null) throw new InvalidOperationException("Element not found: " + elementId);
        // axisPointsMm: two points defining a mirror line in XY; build a vertical plane
        var p0 = new XYZ(axisPointsMm[0][0] / 304.8, axisPointsMm[0][1] / 304.8, 0);
        var p1 = new XYZ(axisPointsMm[1][0] / 304.8, axisPointsMm[1][1] / 304.8, 0);
        var mid = (p0 + p1) * 0.5;
        var dir = (p1 - p0).Normalize();
        var normal = new XYZ(-dir.Y, dir.X, 0);
        var plane = Plane.CreateByNormalAndOrigin(normal, mid);
        using (var tx = new Transaction(doc, "Mirror Element"))
        {
            tx.Start();
            ElementTransformUtils.MirrorElement(doc, id, plane);
            tx.Commit();
        }
        return new Dictionary<string, object> { { "ok", true }, { "id", elementId } };
    }

    // ---------- Phase 2: Measure / bbox / categories / views ----------
    public static object MeasureDistance(UIApplication app, long id1, long id2)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var e1 = doc.GetElement(new ElementId(id1));
        var e2 = doc.GetElement(new ElementId(id2));
        if (e1 == null || e2 == null) throw new InvalidOperationException("Element not found");
        var c1 = CenterOf(e1);
        var c2 = CenterOf(e2);
        var dist = c1.DistanceTo(c2) * 304.8;
        return new Dictionary<string, object> { { "distance_mm", Math.Round(dist, 1) } };
    }

    private static XYZ CenterOf(Element e)
    {
        var bb = e.get_BoundingBox(null);
        if (bb != null) return (bb.Min + bb.Max) * 0.5;
        if (e.Location is LocationPoint lp) return lp.Point;
        if (e.Location is LocationCurve lc) return lc.Curve.Evaluate(0.5, true);
        return XYZ.Zero;
    }

    public static object GetBoundingBox(UIApplication app, long id)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var e = doc.GetElement(new ElementId(id));
        if (e == null) throw new InvalidOperationException("Element not found: " + id);
        var bb = e.get_BoundingBox(null);
        if (bb == null) throw new InvalidOperationException("No bounding box for element: " + id);
        return new Dictionary<string, object>
        {
            { "min", new[] { bb.Min.X * 304.8, bb.Min.Y * 304.8, bb.Min.Z * 304.8 } },
            { "max", new[] { bb.Max.X * 304.8, bb.Max.Y * 304.8, bb.Max.Z * 304.8 } },
        };
    }

    public static object ListCategories(UIApplication app)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var items = new List<string>();
        foreach (Category c in doc.Settings.Categories)
            items.Add(c.Name);
        items.Sort();
        return new Dictionary<string, object> { { "count", items.Count }, { "items", items } };
    }

    public static object CreateView3D(UIApplication app, string name)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        using (var tx = new Transaction(doc, "Create 3D View"))
        {
            tx.Start();
            var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);
            if (vft == null) throw new InvalidOperationException("No 3D view family type");
            var view = View3D.CreateIsometric(doc, vft.Id);
            if (!string.IsNullOrWhiteSpace(name)) view.Name = name;
            tx.Commit();
            return new Dictionary<string, object> { { "id", view.Id.Value }, { "name", view.Name } };
        }
    }

    public static object SetActiveView(UIApplication app, string viewNameOrId)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        View view = null;
        if (long.TryParse(viewNameOrId, out var vid))
            view = doc.GetElement(new ElementId(vid)) as View;
        if (view == null)
            view = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => v.Name == viewNameOrId);
        if (view == null) throw new InvalidOperationException("View not found: " + viewNameOrId);
        uidoc.ActiveView = view;
        return new Dictionary<string, object> { { "ok", true }, { "id", view.Id.Value }, { "name", view.Name } };
    }

    // ---------- Phase 3: Families & parameters ----------
    public static object ListFamilies(UIApplication app, string category)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var items = new List<Dictionary<string, object>>();
        foreach (Family f in new FilteredElementCollector(doc).OfClass(typeof(Family)))
        {
            if (!string.IsNullOrWhiteSpace(category) && (f.FamilyCategory == null || f.FamilyCategory.Name != category)) continue;
            var types = new List<string>();
            foreach (FamilySymbol fs in f.GetFamilySymbolIds().Select(id => doc.GetElement(id)))
                types.Add(fs.Name);
            items.Add(new Dictionary<string, object>
            {
                { "name", f.Name },
                { "category", f.FamilyCategory != null ? f.FamilyCategory.Name : "" },
                { "types", types },
            });
        }
        return new Dictionary<string, object> { { "count", items.Count }, { "items", items } };
    }

    public static object GetInstanceParams(UIApplication app, long instanceId)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var fi = doc.GetElement(new ElementId(instanceId)) as FamilyInstance;
        if (fi == null) throw new InvalidOperationException("Family instance not found: " + instanceId);
        var items = new List<Dictionary<string, object>>();
        foreach (Parameter p in fi.Parameters)
        {
            try { items.Add(new Dictionary<string, object> { { "name", p.Definition.Name }, { "value", ParamValue(p) }, { "isReadOnly", p.IsReadOnly }, { "storageType", p.StorageType.ToString() } }); } catch (Exception) { }
        }
        return new Dictionary<string, object> { { "id", fi.Id.Value }, { "family", fi.Symbol.Family.Name }, { "type", fi.Symbol.Name }, { "parameters", items } };
    }

    public static object SetInstanceParam(UIApplication app, long instanceId, string paramName, object value)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var fi = doc.GetElement(new ElementId(instanceId)) as FamilyInstance;
        if (fi == null) throw new InvalidOperationException("Family instance not found: " + instanceId);
        using (var tx = new Transaction(doc, "Set Instance Param"))
        {
            tx.Start();
            var p = fi.LookupParameter(paramName);
            if (p == null) throw new InvalidOperationException("Parameter not found: " + paramName);
            if (p.IsReadOnly) throw new InvalidOperationException("Parameter is read-only: " + paramName);
            if (value is double d) p.Set(d);
            else if (value is int i) p.Set(i);
            else if (value is long l) p.Set((int)l);
            else if (value is bool b) p.Set(b ? 1 : 0);
            else if (value is string s) p.Set(s);
            else p.Set(Convert.ToString(value));
            tx.Commit();
            return new Dictionary<string, object> { { "ok", true }, { "id", instanceId }, { "param", paramName }, { "value", ParamValue(p) } };
        }
    }

    // ---------- Phase 4: Schedules on template ----------
    public static object CreateSchedule(UIApplication app, int categoryId, string name, List<string> fields, List<Dictionary<string, object>> filters, string groupingField, string sortingField)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var cat = new ElementId(new Autodesk.Revit.DB.BuiltInCategory().GetType() == typeof(int) ? 0 : 0);
        // resolve category
        Category category = null;
        foreach (Category c in doc.Settings.Categories)
        {
            if (c.Id.Value == categoryId) { category = c; break; }
        }
        if (category == null)
        {
            // try by built-in name
            foreach (Category c in doc.Settings.Categories)
            {
                if (c.Name.Equals("Walls", StringComparison.OrdinalIgnoreCase) && categoryId == (int)BuiltInCategory.OST_Walls) { category = c; break; }
            }
        }
        if (category == null) throw new InvalidOperationException("Category not found: " + categoryId);
        using (var tx = new Transaction(doc, "Create Schedule"))
        {
            tx.Start();
            var vs = ViewSchedule.CreateSchedule(doc, category.Id);
            if (!string.IsNullOrWhiteSpace(name)) vs.Name = name;
            // add fields
            var schedulable = vs.Definition.GetSchedulableFields();
            if (fields != null)
            {
                foreach (var fn in fields)
                {
                    var sf = schedulable.FirstOrDefault(f => { try { return f.GetName(doc).Equals(fn, StringComparison.OrdinalIgnoreCase); } catch (Exception) { return false; } });
                    if (sf != null) vs.Definition.AddField(sf);
                }
            }
            tx.Commit();
            return new Dictionary<string, object> { { "id", vs.Id.Value }, { "name", vs.Name } };
        }
    }

    public static object ModifySchedule(UIApplication app, long scheduleId, List<string> fields, List<Dictionary<string, object>> filters, string groupingField, string sortingField)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var vs = doc.GetElement(new ElementId(scheduleId)) as ViewSchedule;
        if (vs == null) throw new InvalidOperationException("Schedule not found: " + scheduleId);
        using (var tx = new Transaction(doc, "Modify Schedule"))
        {
            tx.Start();
            if (fields != null && fields.Count > 0)
            {
                // clear existing fields
                foreach (var fid in vs.Definition.GetFieldOrder().ToList())
                    vs.Definition.RemoveField(fid);
                var schedulable = vs.Definition.GetSchedulableFields();
                foreach (var fn in fields)
                {
                    var sf = schedulable.FirstOrDefault(f => { try { return f.GetName(doc).Equals(fn, StringComparison.OrdinalIgnoreCase); } catch (Exception) { return false; } });
                    if (sf != null) vs.Definition.AddField(sf);
                }
            }
            tx.Commit();
            return new Dictionary<string, object> { { "ok", true }, { "id", scheduleId } };
        }
    }

    public static object RefreshSchedule(UIApplication app, long? scheduleId)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var schedules = scheduleId.HasValue
            ? new List<ViewSchedule> { doc.GetElement(new ElementId(scheduleId.Value)) as ViewSchedule }.Where(x => x != null).ToList()
            : new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().ToList();
        int n = 0;
        foreach (var vs in schedules)
        {
            try { vs.RefreshData(); n++; } catch (Exception) { }
        }
        return new Dictionary<string, object> { { "refreshed", n } };
    }

    // ---------- Phase 5: Safety ----------
    public static object BackupDocument(UIApplication app, string path)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(Path.GetTempPath(), doc.Title + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".rvt");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
        return new Dictionary<string, object> { { "path", path } };
    }

    public static object DryRun(UIApplication app, string operation, Dictionary<string, object> args)
    {
        // Returns a plan without executing. For RevitMcp, the plan is just echoing the operation + args.
        return new Dictionary<string, object>
        {
            { "dry_run", true },
            { "operation", operation },
            { "args", args ?? new Dictionary<string, object>() },
            { "note", "No changes were made. Re-run the same operation without dry_run to execute." },
        };
    }

    public static object UndoLast(UIApplication app)
    {
        // Revit API does not expose a direct programmatic Undo for committed transactions.
        // Each RevitMcp operation is its own transaction; undo via Revit UI (Ctrl+Z) if needed.
        return new Dictionary<string, object> { { "ok", false }, { "note", "Per-operation transactions are already committed; use Revit UI Undo (Ctrl+Z) to revert. For safety, use backup_document before destructive ops." } };
    }

}
