using System;
using System.IO;
using UnityEditor;

namespace Claunity
{

/// <summary>Domain-reload-safe test runner for Play &amp; Test Mode.</summary>
[InitializeOnLoad]
public static class ClaunityTestRunner
{
    private const string PendingKey      = "Claunity_TestPending";
    private const string WaitStartKey    = "Claunity_TestWaitStart";
    private const string ResultReadyKey  = "Claunity_TestResultReady";
    private const string ConsoleDataKey  = "Claunity_TestConsoleData";
    private static readonly string ScreenshotTempPath =
        Path.Combine("Temp", "claunity_test_screenshot.b64");

    /// <summary>Fired with a status string at each step of the test sequence.</summary>
    public static event Action<string> OnTestStatus;

    /// <summary>Fired when data is collected. Args: (screenshot base64 or null, consoleData).</summary>
    public static event Action<string, string> OnTestComplete;

    private static bool _waitingInPlayMode;
    private static double _exitRequestedAt = -1;

    static ClaunityTestRunner()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        EditorApplication.update -= OnUpdate;
        EditorApplication.update += OnUpdate;
    }

    public static bool IsPending => EditorPrefs.GetBool(PendingKey, false);

    /// <summary>Clears stale pending state left from a previous Unity session (e.g., Unity was closed mid-test).</summary>
    public static void ClearStalePendingIfNeeded()
    {
        if (!EditorApplication.isPlaying && EditorPrefs.GetBool(PendingKey, false))
        {
            EditorPrefs.DeleteKey(PendingKey);
            EditorPrefs.DeleteKey(WaitStartKey);
            EditorPrefs.DeleteKey(ResultReadyKey);
            EditorPrefs.DeleteKey(ConsoleDataKey);
            _waitingInPlayMode = false;
            _exitRequestedAt   = -1;
        }
    }

    public static void StartTest()
    {
        if (EditorPrefs.GetBool(PendingKey, false)) return;
        EditorPrefs.SetBool(PendingKey, true);
        EditorPrefs.SetFloat(WaitStartKey, -1f);
        OnTestStatus?.Invoke("🎮 Entering Play Mode...");
        EditorApplication.isPlaying = true;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (!EditorPrefs.GetBool(PendingKey, false)) return;

        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            EditorPrefs.SetFloat(WaitStartKey, (float)EditorApplication.timeSinceStartup);
            _waitingInPlayMode = true;
            OnTestStatus?.Invoke("📸 Waiting for game to initialize...");
        }
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            EditorPrefs.DeleteKey(PendingKey);
            EditorPrefs.DeleteKey(WaitStartKey);
            _waitingInPlayMode = false;
            EditorApplication.delayCall += CollectAndReport;
        }
    }

    private static void OnUpdate()
    {
        if (EditorPrefs.GetBool(ResultReadyKey, false) && OnTestComplete != null)
        {
            FlushPendingResult();
            return;
        }

        if (_exitRequestedAt > 0 &&
            EditorApplication.timeSinceStartup - _exitRequestedAt > 15.0 &&
            !EditorApplication.isPlaying)
        {
            _exitRequestedAt = -1;
            EditorApplication.delayCall += CollectAndReport;
            return;
        }

        if (!_waitingInPlayMode) return;
        if (!EditorPrefs.GetBool(PendingKey, false)) return;

        var waitStart = EditorPrefs.GetFloat(WaitStartKey, -1f);
        if (waitStart < 0) return;
        if (EditorApplication.timeSinceStartup - waitStart < 2.0) return;

        _waitingInPlayMode = false;
        OnTestStatus?.Invoke("📸 Capturing Game View...");

        var result = ClaunityActionExecutor.Execute(new ActionPayload { type = "take_screenshot", view = "game" });
        if (result != null && result.Length > 8 && result.StartsWith("[IMAGE:") && result.EndsWith("]"))
        {
            var base64 = result.Substring(7, result.Length - 8);
            try { File.WriteAllText(ScreenshotTempPath, base64); } catch { }
        }
        else if (File.Exists(ScreenshotTempPath))
        {
            try { File.Delete(ScreenshotTempPath); } catch { }
        }

        OnTestStatus?.Invoke("🛑 Exiting Play Mode...");
        _exitRequestedAt = EditorApplication.timeSinceStartup;
        EditorApplication.isPlaying = false;
    }

    private static void CollectAndReport()
    {
        _exitRequestedAt = -1;

        string base64 = null;
        if (File.Exists(ScreenshotTempPath))
        {
            try
            {
                base64 = File.ReadAllText(ScreenshotTempPath);
                File.Delete(ScreenshotTempPath);
            }
            catch { }
        }

        string consoleData;
        try { consoleData = ClaunityConsole.FormatAllForTestReport(); }
        catch { consoleData = "=== UNITY CONSOLE ===\nCould not read console data."; }

        if (OnTestComplete != null)
        {
            OnTestStatus?.Invoke("🔍 Analyzing...");
            try { OnTestComplete?.Invoke(base64, consoleData); }
            catch { }
        }
        else
        {
            EditorPrefs.SetString(ConsoleDataKey, consoleData ?? "");
            EditorPrefs.SetBool(ResultReadyKey, true);
            if (base64 != null)
            {
                try { File.WriteAllText(ScreenshotTempPath, base64); } catch { }
            }
        }
    }

    /// <summary>Called by ClaunityWindow after it subscribes to OnTestComplete, to flush any pending result that was collected before the window finished initialising.</summary>
    public static void FlushPendingResult()
    {
        if (!EditorPrefs.GetBool(ResultReadyKey, false)) return;

        EditorPrefs.DeleteKey(ResultReadyKey);
        var consoleData = EditorPrefs.GetString(ConsoleDataKey, "");
        EditorPrefs.DeleteKey(ConsoleDataKey);

        string base64 = null;
        if (File.Exists(ScreenshotTempPath))
        {
            try { base64 = File.ReadAllText(ScreenshotTempPath); File.Delete(ScreenshotTempPath); }
            catch { }
        }

        OnTestStatus?.Invoke("🔍 Analyzing...");
        OnTestComplete?.Invoke(base64, consoleData);
    }
}

}