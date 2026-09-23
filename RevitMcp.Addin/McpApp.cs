using System;
using Autodesk.Revit.UI;

namespace RevitMcp;

/// <summary>
/// Revit ExternalApplication: hosts a tiny localhost HTTP bridge so an out-of-process
/// MCP stdio server (mcp-server/server.py, used by OpenCode) can drive Revit:
/// open model, query doc info / rooms / levels, run HeatLoss plugin commands.
/// Listens on 127.0.0.1:17871 by default (override with REVIT_MCP_PORT env var).
/// </summary>
public class McpApp : IExternalApplication
{
    private McpHttpServer _server;

    public static McpApp Instance { get; private set; }

    internal McpHandler Handler { get; private set; }

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            Instance = this;
            Handler = new McpHandler();
            var uiEvent = ExternalEvent.Create(Handler);
            Handler.Bind(uiEvent);

            var port = 17871;
            var env = Environment.GetEnvironmentVariable("REVIT_MCP_PORT");
            if (int.TryParse(env, out var parsed) && parsed > 0 && parsed < 65536)
                port = parsed;

            _server = new McpHttpServer(port, Handler);
            _server.Start();
            McpLog.Write("RevitMcp started on port " + port);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            McpLog.Write("OnStartup failed: " + ex);
            // Do not block Revit startup if the bridge fails (port busy etc.).
            return Result.Succeeded;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            _server?.Stop();
            McpLog.Write("RevitMcp stopped");
        }
        catch (Exception ex)
        {
            McpLog.Write("OnShutdown failed: " + ex);
        }
        return Result.Succeeded;
    }
}
