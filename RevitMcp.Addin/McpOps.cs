using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;

namespace RevitMcp;

/// <summary>
/// All Revit-API work. Every method runs on Revit's main thread (via McpHandler)
/// and returns JSON-serializable objects (dictionaries / lists / primitives).
/// </summary>
internal static class McpOps
{
    public static object Ping(UIApplication app)
    {
        var a = app.Application;
        return new Dictionary<string, object>
        {
            { "ok", true },
            { "revitVersionName", a.VersionName },
            { "revitVersionBuild", a.VersionBuild },
            { "revitVersionNumber", a.VersionNumber },
            { "username", a.Username },
            { "hasActiveDocument", app.ActiveUIDocument != null },
        };
    }

    public static object Open(UIApplication app, string path, bool detach, bool audit)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("path is empty");
        if (!File.Exists(path))
            throw new FileNotFoundException("File not found: " + path);

        var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
        var openOptions = new OpenOptions
        {
            DetachFromCentralOption = detach
                ? DetachFromCentralOption.DetachAndPreserveWorksets
                : DetachFromCentralOption.DoNotDetach,
            Audit = audit,
        };
        var uidoc = app.OpenAndActivateDocument(modelPath, openOptions, false);
        var doc = uidoc.Document;
        return new Dictionary<string, object>
        {
            { "title", doc.Title },
            { "path", doc.PathName },
            { "isWorkshared", doc.IsWorkshared },
        };
    }

    public static object Close(UIApplication app, bool save)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null)
            return new Dictionary<string, object> { { "closed", false }, { "reason", "no active document" } };
        var title = uidoc.Document.Title;
        uidoc.Document.Close(save);
        return new Dictionary<string, object> { { "closed", true }, { "title", title } };
    }

    public static object DocInfo(UIApplication app)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null)
            return new Dictionary<string, object> { { "open", false } };
        var doc = uidoc.Document;
        var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().ToElementIds().Count;
        var spaces = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MEPSpaces).WhereElementIsNotElementType().ToElementIds().Count;
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).ToElementIds().Count;
        return new Dictionary<string, object>
        {
            { "open", true },
            { "title", doc.Title },
            { "path", doc.PathName },
            { "isModified", doc.IsModified },
            { "isWorkshared", doc.IsWorkshared },
            { "roomCount", rooms },
            { "spaceCount", spaces },
            { "levelCount", levels },
        };
    }

    public static object Rooms(UIApplication app, int limit, int offset)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null)
            throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var items = new List<Dictionary<string, object>>();

        foreach (var e in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType())
            items.Add(ReadRoom(e, "Room"));
        foreach (var e in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MEPSpaces).WhereElementIsNotElementType())
            items.Add(ReadRoom(e, "Space"));

        var total = items.Count;
        var page = items.Skip(Math.Max(0, offset)).Take(Math.Max(1, Math.Min(500, limit))).ToList();
        return new Dictionary<string, object>
        {
            { "total", total },
            { "offset", offset },
            { "count", page.Count },
            { "items", page },
        };
    }

    private static Dictionary<string, object> ReadRoom(Element e, string kind)
    {
        string number = "";
        string name = e.Name;
        string level = "";
        double area = 0;
        double volume = 0;
        try
        {
            if (e is Room room)
            {
                number = room.Number ?? "";
                var lvl = room.Level;
                if (lvl != null) level = lvl.Name;
                area = room.Area;
                volume = room.Volume;
            }
            else if (e is Space space)
            {
                number = space.Number ?? "";
                var lvl = space.Level;
                if (lvl != null) level = lvl.Name;
                area = space.Area;
                volume = space.Volume;
            }
        }
        catch (Exception) { }
        return new Dictionary<string, object>
        {
            { "id", e.Id.Value },
            { "kind", kind },
            { "number", number },
            { "name", name },
            { "level", level },
            { "area_ft2", Math.Round(area, 3) },
            { "volume_ft3", Math.Round(volume, 3) },
        };
    }

    public static object Levels(UIApplication app)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null)
            throw new InvalidOperationException("No active document");
        var doc = uidoc.Document;
        var items = new List<Dictionary<string, object>>();
        foreach (Level lv in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation))
        {
            items.Add(new Dictionary<string, object>
            {
                { "id", lv.Id.Value },
                { "name", lv.Name },
                { "elevation_ft", Math.Round(lv.Elevation, 4) },
            });
        }
        return new Dictionary<string, object> { { "count", items.Count }, { "items", items } };
    }

    // HeatLoss plugin commands: key -> full type name in MainAppHeatLoss.dll.
    private static readonly Dictionary<string, string> HeatLossCommands =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "settings", "MainAppHeatLoss.OpenSettingsCommand" },
            { "modeling", "MainAppHeatLoss.Projects.Modeling.ModelingCommand" },
            { "calculations", "MainAppHeatLoss.Calculations.OpenCalculationsCommand" },
            { "main", "MainAppHeatLoss.ShowMainWindowCommand" },
            { "reports", "MainAppHeatLoss.Reports.OpenReportsWindowCommand" },
            { "tools", "MainAppHeatLoss.Projects.ToolsPanel.ToolsPanelCommand" },
            { "about", "MainAppHeatLoss.About.OpenAboutWindowCommand" },
            { "snapshot", "DirectShapeSnapshot.TestKit.Diagnostics.DumpDirectShapesCommand" },
        };

    public static object RunHeatLoss(UIApplication app, string command)
    {
        var key = (command ?? "calculations").Trim();
        if (!HeatLossCommands.TryGetValue(key, out var typeName))
            throw new ArgumentException("Unknown command '" + key + "'. Known: " + string.Join(", ", HeatLossCommands.Keys));

        var asm = FindAssembly("MainAppHeatLoss");
        if (asm == null)
            throw new InvalidOperationException(
                "MainAppHeatLoss.dll is not loaded. Open a model first or install the HeatLoss plugin (MainAppHeatLoss.addin).");

        var type = asm.GetType(typeName);
        if (type == null)
            throw new InvalidOperationException("Type not found: " + typeName + " in " + asm.FullName);

        var cmd = Activator.CreateInstance(type) as IExternalCommand;
        if (cmd == null)
            throw new InvalidOperationException("Type is not IExternalCommand: " + typeName);

        var cmdData = ForgeCommandData(app);
        string message = "";
        var elements = new ElementSet();
        var result = cmd.Execute(cmdData, ref message, elements);
        return new Dictionary<string, object>
        {
            { "command", key },
            { "type", typeName },
            { "result", result.ToString() },
            { "message", message ?? "" },
        };
    }

    public static object ListHeatLossCommands()
    {
        return new Dictionary<string, object>
        {
            { "commands", HeatLossCommands.Keys.ToArray() },
        };
    }

    private static Assembly FindAssembly(string simpleName)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    return a;
            }
            catch (Exception) { }
        }
        // Not loaded yet: probe Revit Addins folders (HeatLoss deploys next to its .addin).
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var dir in new[] { @"Autodesk\Revit\Addins\2024", @"Autodesk\Revit\Addins\2025" })
        {
            try
            {
                var root = Path.Combine(appData, dir);
                if (!Directory.Exists(root)) continue;
                var hit = Directory.GetFiles(root, simpleName + ".dll", SearchOption.AllDirectories).FirstOrDefault();
                if (hit != null) return Assembly.LoadFrom(hit);
            }
            catch (Exception) { }
        }
        return null;
    }

    /// <summary>
    /// ExternalCommandData has no public constructor (Revit creates it per command call).
    /// Forge an instance via FormatterServices and inject the UIApplication into its
    /// UIApplication-typed backing field (field name varies by Revit version, so match by type).
    /// </summary>
    private static Autodesk.Revit.UI.ExternalCommandData ForgeCommandData(UIApplication app)
    {
        var cmdData = (Autodesk.Revit.UI.ExternalCommandData)
            FormatterServices.GetUninitializedObject(typeof(Autodesk.Revit.UI.ExternalCommandData));
        var fields = typeof(Autodesk.Revit.UI.ExternalCommandData)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var injected = false;
        foreach (var f in fields)
        {
            try
            {
                if (f.FieldType == typeof(UIApplication))
                {
                    f.SetValue(cmdData, app);
                    injected = true;
                }
            }
            catch (Exception) { }
        }
        if (!injected)
            throw new InvalidOperationException("Cannot inject UIApplication into ExternalCommandData (API layout changed)");
        return cmdData;
    }
}
