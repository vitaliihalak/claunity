using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Claunity
{

/// <summary>
/// Captures Unity Console messages across domain reloads.
/// Subscribed via [InitializeOnLoad] so it re-subscribes after every reload.
/// </summary>
[InitializeOnLoad]
public static class ClaunityConsole
{
    public const int MaxEntries = 50;

    [Serializable]
    public class Entry
    {
        public string logType;    // "error" | "warning"
        public string message;
        public string stackTrace; // first line only
        public string time;
        public int    count = 1;  // how many times this exact message appeared
    }

    private static readonly List<Entry> _entries = new List<Entry>();
    private static readonly List<string> _compileErrors = new List<string>();

    /// <summary>Fired on the main thread when new errors/warnings arrive.</summary>
    public static event Action OnChanged;

    /// <summary>Fired when compilation finishes with errors while Project Mode is executing.</summary>
    public static event Action<string[]> OnProjectCompileFailed;

    static ClaunityConsole()
    {
        Application.logMessageReceived -= OnLog; // guard against double-subscribe
        Application.logMessageReceived += OnLog;
        CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompiled;
        CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
    }

    private static void OnAssemblyCompiled(string assemblyPath, CompilerMessage[] messages)
    {
        var errors = messages
            .Where(m => m.type == CompilerMessageType.Error)
            .Select(m => $"{m.file}({m.line},{m.column}): {m.message}")
            .ToArray();

        if (errors.Length == 0) return;

        lock (_compileErrors)
        {
            _compileErrors.AddRange(errors);
        }

        // Notify Project Mode if it was waiting for recompile OR actively executing a task
        bool resuming  = EditorPrefs.GetBool("Claunity_ProjectResuming", false);
        bool executing = EditorPrefs.GetBool("Claunity_ProjectExecuting", false);
        if (resuming || executing)
        {
            var captured = errors;
            EditorApplication.delayCall += () => OnProjectCompileFailed?.Invoke(captured);
        }
    }

    public static string[] GetAndClearCompileErrors()
    {
        lock (_compileErrors)
        {
            var result = _compileErrors.ToArray();
            _compileErrors.Clear();
            return result;
        }
    }

    private static void OnLog(string message, string stackTrace, LogType type)
    {
        if (type == LogType.Log || type == LogType.Assert) return;
        if (message.StartsWith("[Claunity]")) return; // ignore own logs

        var entry = new Entry
        {
            logType    = (type == LogType.Warning) ? "warning" : "error",
            message    = message,
            stackTrace = stackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "",
            time       = DateTime.Now.ToString("HH:mm:ss"),
        };

        lock (_entries)
        {
            // Deduplicate: if the same message+type already exists, just bump its count
            var existing = _entries.LastOrDefault(
                e => e.logType == entry.logType && e.message == entry.message);
            if (existing != null)
            {
                existing.count++;
                existing.time = entry.time; // keep the latest timestamp
            }
            else
            {
                _entries.Add(entry);
                if (_entries.Count > MaxEntries) _entries.RemoveAt(0);
            }
        }

        // Dispatch to main thread safely
        EditorApplication.delayCall += () => OnChanged?.Invoke();
    }

    public static int ErrorCount
    {
        get { lock (_entries) return _entries.Count(e => e.logType == "error"); }
    }

    public static int WarningCount
    {
        get { lock (_entries) return _entries.Count(e => e.logType == "warning"); }
    }

    public static List<Entry> GetEntries()
    {
        lock (_entries) return new List<Entry>(_entries);
    }

    /// <summary>Formats recent errors for Claude context. Empty string if no errors.</summary>
    public static string FormatForContext(int max = 10)
    {
        List<Entry> errors;
        lock (_entries)
            errors = _entries.Where(e => e.logType == "error").TakeLast(max).ToList();

        if (errors.Count == 0) return "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== UNITY CONSOLE ERRORS ({errors.Count}) ===");
        foreach (var e in errors)
        {
            var prefix = e.count > 1 ? $"(x{e.count}) " : "";
            sb.AppendLine($"[{e.time}] {prefix}{e.message}");
            if (!string.IsNullOrEmpty(e.stackTrace))
                sb.AppendLine($"  at {e.stackTrace}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Formats errors as a user message for "Fix errors" flow.</summary>
    public static string FormatForFixRequest()
    {
        List<Entry> errors;
        lock (_entries)
            errors = _entries.Where(e => e.logType == "error").TakeLast(10).ToList();

        if (errors.Count == 0) return "There are no errors in the Console right now.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"I have {errors.Count} error(s) in the Unity Console. Please analyze and fix them:");
        sb.AppendLine();
        foreach (var e in errors)
        {
            var prefix = e.count > 1 ? $"(x{e.count}) " : "";
            sb.AppendLine($"• {prefix}{e.message}");
            if (!string.IsNullOrEmpty(e.stackTrace))
                sb.AppendLine($"  at {e.stackTrace}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Formats all entries (errors + warnings) for the Play & Test Mode report.</summary>
    public static string FormatAllForTestReport()
    {
        List<Entry> entries;
        lock (_entries) entries = new List<Entry>(_entries);

        if (entries.Count == 0) return "=== UNITY CONSOLE ===\nNo errors or warnings.";

        var sb = new System.Text.StringBuilder();
        var errors   = entries.Where(e => e.logType == "error").ToList();
        var warnings = entries.Where(e => e.logType == "warning").ToList();
        sb.AppendLine($"=== UNITY CONSOLE ({errors.Count} errors, {warnings.Count} warnings) ===");
        foreach (var e in entries)
        {
            var prefix = e.count > 1 ? $"(x{e.count}) " : "";
            sb.AppendLine($"[{e.logType.ToUpper()}][{e.time}] {prefix}{e.message}");
            if (!string.IsNullOrEmpty(e.stackTrace))
                sb.AppendLine($"  at {e.stackTrace}");
        }
        return sb.ToString().TrimEnd();
    }

    public static void Clear()
    {
        lock (_entries) _entries.Clear();
        OnChanged?.Invoke();
    }
}

}