using System;
using System.IO;
using Autodesk.Revit.UI;

namespace RevitMcp;

/// <summary>
/// Marshals work onto Revit's main thread via a single reusable ExternalEvent.
/// HTTP worker threads call Run(), which raises the event and blocks until
/// Execute() finishes on the Revit UI thread (with timeout).
/// </summary>
internal class McpHandler : IExternalEventHandler
{
    private ExternalEvent _event;
    private Func<UIApplication, object> _pending;
    private object _result;
    private Exception _error;
    private System.Threading.ManualResetEventSlim _gate;

    public void Bind(ExternalEvent uiEvent)
    {
        _event = uiEvent;
    }

    public T Run<T>(Func<UIApplication, T> func, int timeoutMs = 120000)
    {
        if (_event == null)
            throw new InvalidOperationException("ExternalEvent not initialized");

        _gate = new System.Threading.ManualResetEventSlim(false);
        _result = null;
        _error = null;
        _pending = app => func(app);

        var accepted = _event.Raise();
        if (accepted != ExternalEventRequest.Accepted)
            throw new InvalidOperationException("ExternalEvent.Raise() returned " + accepted);

        if (!_gate.Wait(timeoutMs))
            throw new TimeoutException("Revit is busy (timeout " + timeoutMs + " ms). Try again.");

        if (_error != null)
            throw new InvalidOperationException(_error.Message, _error);

        return (T)_result;
    }

    public void Execute(UIApplication app)
    {
        try
        {
            var func = _pending;
            _pending = null;
            _result = func != null ? func(app) : null;
        }
        catch (Exception ex)
        {
            _error = ex;
            _result = null;
        }
        finally
        {
            try
            {
                if (_gate != null) _gate.Set();
            }
            catch (Exception) { }
        }
    }

    public string GetName()
    {
        return "RevitMcp ExternalEvent";
    }
}

/// <summary>Best-effort file log in %TEMP%\RevitMcp.log (never throws).</summary>
internal static class McpLog
{
    public static void Write(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "RevitMcp.log");
            File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
        }
        catch (Exception) { }
    }
}
