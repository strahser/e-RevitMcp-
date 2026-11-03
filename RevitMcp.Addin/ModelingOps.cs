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
}
