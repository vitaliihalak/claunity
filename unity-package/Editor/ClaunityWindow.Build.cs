using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace Claunity
{

public partial class ClaunityWindow
{

    // ── BUILD VIEW ─────────────────────────────────────────────────────────────

    private const string SbActivePref  = "Claunity_SB_Active";
    private const string SbIterPref    = "Claunity_SB_Iter";
    private const string SbOutputPref  = "Claunity_SB_Output";
    private const string SbPlatPref    = "Claunity_SB_Platform";
    private const string SbScenesPref  = "Claunity_SB_Scenes";
    private const string SbOptsPref    = "Claunity_SB_Opts";
    private const int    SbMaxIter     = 5;

    private VisualElement _buildLog;
    private ScrollView    _buildLogScroll;

    private static readonly string[] BuildPlatforms = new[]
    {
        "Windows 64-bit", "Windows 32-bit", "macOS", "Linux 64-bit",
        "Android", "iOS", "WebGL"
    };

    private void InitBuildView()
    {
        _buildLog       = rootVisualElement.Q("build-log");
        _buildLogScroll = rootVisualElement.Q<ScrollView>("build-log-scroll");

        if (_buildPlatformDropdown != null)
        {
            _buildPlatformDropdown.choices = new List<string>(BuildPlatforms);
            _buildPlatformDropdown.index   = 0;
        }

        _buildScenesAllToggle?.RegisterValueChangedCallback(e => {
            if (e.newValue) _buildScenesCurrentToggle?.SetValueWithoutNotify(false);
        });
        _buildScenesCurrentToggle?.RegisterValueChangedCallback(e => {
            if (e.newValue) _buildScenesAllToggle?.SetValueWithoutNotify(false);
        });

        rootVisualElement.Q<Button>("build-browse-btn")?.RegisterCallback<ClickEvent>(_ =>
        {
            var folder = EditorUtility.OpenFolderPanel("Select Build Folder", "Builds", "");
            if (!string.IsNullOrEmpty(folder) && _buildPathField != null)
                _buildPathField.value = folder + "/";
        });

        _buildOpenFolderBtn?.RegisterCallback<ClickEvent>(_ =>
        {
            if (!string.IsNullOrEmpty(_lastBuildFolder) && Directory.Exists(_lastBuildFolder))
                EditorUtility.RevealInFinder(_lastBuildFolder);
        });

        _buildBtn?.RegisterCallback<ClickEvent>(_ => StartSmartBuild());
    }

    /// <summary>Called from CreateGUI to resume a smart build interrupted by domain reload.</summary>
    private void CheckSmartBuildResume()
    {
        if (!EditorPrefs.GetBool(SbActivePref, false)) return;

        SwitchTab("build");
        EditorApplication.delayCall += () =>
            EditorCoroutineUtility.StartCoroutineOwnerless(SmartBuildLoop(
                EditorPrefs.GetInt(SbIterPref, 1),
                EditorPrefs.GetString(SbOutputPref),
                EditorPrefs.GetString(SbPlatPref, "Windows 64-bit"),
                EditorPrefs.GetString(SbScenesPref, "all"),
                (BuildOptions)EditorPrefs.GetInt(SbOptsPref, 0)
            ));
    }

    private void StartSmartBuild()
    {
        string buildName   = _buildNameField?.value?.Trim() ?? "MyGame";
        if (string.IsNullOrEmpty(buildName)) buildName = "MyGame";
        string platformStr = _buildPlatformDropdown?.value ?? "Windows 64-bit";
        string outputBase  = _buildPathField?.value?.Trim() ?? "Builds/";
        bool allScenes     = _buildScenesAllToggle?.value ?? true;
        bool isDev         = _buildDevToggle?.value ?? false;
        bool isDebug       = _buildDebugToggle?.value ?? false;

        (BuildTarget target, string ext, string platformFolder) = ResolvePlatform(platformStr);
        string outputDir  = Path.Combine(outputBase, platformFolder);
        string outputPath = string.IsNullOrEmpty(ext)
            ? Path.Combine(outputDir, buildName)
            : Path.Combine(outputDir, buildName + ext);

        BuildOptions opts = BuildOptions.None;
        if (isDev)   opts |= BuildOptions.Development;
        if (isDebug) opts |= BuildOptions.AllowDebugging;

        EditorPrefs.SetBool  (SbActivePref, true);
        EditorPrefs.SetInt   (SbIterPref,   1);
        EditorPrefs.SetString(SbOutputPref, outputPath);
        EditorPrefs.SetString(SbPlatPref,   platformStr);
        EditorPrefs.SetString(SbScenesPref, allScenes ? "all" : "current");
        EditorPrefs.SetInt   (SbOptsPref,   (int)opts);

        _buildLog?.Clear();
        if (_buildResult != null) _buildResult.RemoveFromClassList("build-result--hidden");
        if (_buildResultSummary != null) _buildResultSummary.text = "";
        if (_buildResultPath    != null) _buildResultPath.text    = "";
        _buildOpenFolderBtn?.AddToClassList("build-open-folder-btn--hidden");

        EditorCoroutineUtility.StartCoroutineOwnerless(SmartBuildLoop(1, outputPath, platformStr, allScenes ? "all" : "current", opts));
    }

    private IEnumerator SmartBuildLoop(int startIter, string outputPath, string platformStr, string scenesMode, BuildOptions opts)
    {
        if (_buildBtn != null) _buildBtn.SetEnabled(false);
        if (_buildResult != null) _buildResult.RemoveFromClassList("build-result--hidden");

        if (startIter > 1)
            AppendBuildLog($"🔄 Resumed after recompilation", "info");

        (BuildTarget target, string ext, string platformFolder) = ResolvePlatform(platformStr);
        string outputDir = Path.GetDirectoryName(outputPath);

        for (int iter = startIter; iter <= SbMaxIter; iter++)
        {
            EditorPrefs.SetInt(SbIterPref, iter);

            AppendBuildLog($"🔨 Attempt {iter}/{SbMaxIter} — building...", "info");
            if (_buildResultSummary != null)
                _buildResultSummary.text = $"⏳ Building... (attempt {iter}/{SbMaxIter})";
            yield return null;

            // ── Attempt build ──────────────────────────────────────────────
            string[] scenes = CollectScenes(scenesMode);
            BuildReport report = null;
            var t0 = DateTime.Now;

            try
            {
                if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);
                report = BuildPipeline.BuildPlayer(scenes, outputPath, target, opts);
            }
            catch (Exception ex)
            {
                AppendBuildLog($"❌ Exception: {ex.Message}", "err");
                FinishBuild(false, $"❌ Build failed: {ex.Message}", outputPath, 0, 0, 0);
                yield break;
            }

            var elapsed  = (DateTime.Now - t0).TotalSeconds;
            var summary  = report.summary;
            bool success = summary.result == BuildResult.Succeeded;

            if (success)
            {
                double mb = summary.totalSize / (1024.0 * 1024.0);
                int m = (int)(elapsed / 60), s = (int)(elapsed % 60);
                AppendBuildLog($"✅ Success — {mb:F1} MB in {m:D2}:{s:D2}", "ok");
                _lastBuildFolder = outputDir;
                FinishBuild(true,
                    $"✅  {mb:F1} MB · {m:D2}:{s:D2} · {summary.totalErrors} errors · {summary.totalWarnings} warnings",
                    outputPath, mb, elapsed, (int)summary.totalErrors);
                yield break;
            }

            // ── Build failed — collect errors ──────────────────────────────
            string errors = CollectBuildErrors(report);
            int errCount  = (int)summary.totalErrors;
            AppendBuildLog($"❌ Failed — {errCount} error(s)", "err");

            if (iter == SbMaxIter)
            {
                FinishBuild(false, $"❌ Build failed after {SbMaxIter} attempts · {errCount} errors", outputPath, 0, elapsed, errCount);
                yield break;
            }

            // ── Ask Claude to fix ──────────────────────────────────────────
            AppendBuildLog($"🤖 Asking Claude to fix errors...", "claude");
            if (_buildResultSummary != null)
                _buildResultSummary.text = $"🤖 Fixing errors... (attempt {iter}/{SbMaxIter})";

            bool fixApplied = false;
            yield return PostSmartBuildFix(errors, result => fixApplied = result);

            if (!fixApplied)
            {
                AppendBuildLog("⚠️ Claude could not fix — stopping", "err");
                FinishBuild(false, $"❌ Claude couldn't fix errors · {errCount} errors", outputPath, 0, elapsed, errCount);
                yield break;
            }

            // ── Scripts were written — save state and wait for domain reload ──
            AppendBuildLog("⏳ Waiting for recompilation...", "info");
            if (_buildResultSummary != null)
                _buildResultSummary.text = "⏳ Recompiling... (will resume automatically)";

            EditorPrefs.SetInt(SbIterPref, iter + 1);

            AssetDatabase.Refresh();
            yield return new WaitForEndOfFrame();
        }
    }

    private IEnumerator PostSmartBuildFix(string errors, Action<bool> onDone)
    {
        var filePaths = ExtractScriptPaths(errors);
        var sb = new StringBuilder();
        foreach (var path in filePaths)
        {
            var result = ClaunityActionExecutor.Execute(new ActionPayload
                { type = "read_script", scriptPath = path });
            if (!result.StartsWith("Script not found"))
                sb.AppendLine(result);
        }

        var compileErrors = ClaunityConsole.GetAndClearCompileErrors();
        if (compileErrors.Length > 0)
            errors += "\n\n=== COMPILE ERRORS ===\n" + string.Join("\n", compileErrors);

        string fileContext = sb.ToString().Trim();

        string message =
            "SMART BUILD — Fix all build/compile errors below so the project compiles and builds successfully.\n\n" +
            "=== BUILD ERRORS ===\n" + errors +
            (fileContext.Length > 0 ? "\n\n=== AFFECTED FILES ===\n" + fileContext : "") +
            "\n\nReturn ONLY write_script and/or edit_script actions to fix the errors. " +
            "Do not explain, do not include any text outside the JSON block.";

        var payload = new ChatRequest
        {
            message       = message,
            project_files = _projectContext ?? "",
            project_path  = System.IO.Path.GetDirectoryName(Application.dataPath),
            image         = "",
            mode          = "chat",
            history       = new HistoryMessage[0],
        };

        var body = JsonUtility.ToJson(payload);
        using var req = new UnityWebRequest($"{ServerUrl}/chat", "POST");
        req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 1200;

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            AppendBuildLog($"⚠️ Server error: {req.error}", "err");
            onDone(false);
            yield break;
        }

        var response = JsonUtility.FromJson<ChatResponse>(req.downloadHandler.text);
        if (response == null) { AppendBuildLog("⚠️ Invalid response from server", "err"); onDone(false); yield break; }

        int applied = 0;

        int safetyLimit = 20;
        while (response.type == "tool_request" && safetyLimit-- > 0)
        {
            if (!string.IsNullOrEmpty(response.narration))
                AppendBuildLog($"Claude: {response.narration}", "claude");

            var toolName = response.tool_name;
            var action   = BuildActionFromTool(toolName, response.tool_input_json ?? "{}");
            var result   = ClaunityActionExecutor.Execute(action);

            if (toolName is "create_script" or "edit_script")
            {
                AppendBuildLog(result.StartsWith("✓")
                    ? $"✓ {toolName}: {action.scriptName}"
                    : result, result.StartsWith("✓") ? "ok" : "err");
                if (result.StartsWith("✓")) applied++;
            }

            var resultStr = result?.StartsWith("[IMAGE:") == true ? "[Screenshot captured]" : (result ?? "✓ done");

            ChatResponse next = null;
            yield return SendToolContinue(response.session_id, response.tool_use_id, resultStr, r => next = r);
            if (next == null) { onDone(applied > 0); yield break; }
            response = next;
        }

        if (!string.IsNullOrEmpty(response.reply))
        {
            var preview = response.reply.Length > 120 ? response.reply.Substring(0, 120) + "…" : response.reply;
            AppendBuildLog($"Claude: {preview}", "claude");
        }

        onDone(applied > 0);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static (BuildTarget target, string ext, string folder) ResolvePlatform(string platformStr)
    {
        switch (platformStr)
        {
            case "Windows 32-bit": return (BuildTarget.StandaloneWindows,   ".exe", "Windows32");
            case "macOS":          return (BuildTarget.StandaloneOSX,       ".app", "macOS");
            case "Linux 64-bit":   return (BuildTarget.StandaloneLinux64,   "",     "Linux");
            case "Android":        return (BuildTarget.Android,             ".apk", "Android");
            case "iOS":            return (BuildTarget.iOS,                 "",     "iOS");
            case "WebGL":          return (BuildTarget.WebGL,               "",     "WebGL");
            default:               return (BuildTarget.StandaloneWindows64, ".exe", "Windows64");
        }
    }

    private static string[] CollectScenes(string scenesMode)
    {
        if (scenesMode == "all")
        {
            var scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();
            return scenes.Length > 0
                ? scenes
                : new[] { EditorSceneManager.GetActiveScene().path };
        }
        return new[] { EditorSceneManager.GetActiveScene().path };
    }

    private static string CollectBuildErrors(BuildReport report)
    {
        var sb = new StringBuilder();

        foreach (var step in report.steps)
        {
            foreach (var msg in step.messages)
            {
                if (msg.type == LogType.Error || msg.type == LogType.Exception)
                    sb.AppendLine(msg.content);
            }
        }

        var consoleErrors = ClaunityConsole.FormatForContext();
        if (!string.IsNullOrEmpty(consoleErrors))
            sb.AppendLine(consoleErrors);

        return sb.Length > 0 ? sb.ToString().Trim() : $"Build failed: {report.summary.result}";
    }

    private static List<string> ExtractScriptPaths(string errors)
    {
        var paths = new List<string>();
        var seen  = new HashSet<string>();
        var matches = Regex.Matches(errors, @"Assets[/\\][^\s:(),]+\.cs");
        foreach (Match m in matches)
        {
            var path = m.Value.Replace('\\', '/');
            if (seen.Add(path) && File.Exists(path))
                paths.Add(path);
        }
        return paths;
    }

    private void AppendBuildLog(string text, string style = "info")
    {
        if (_buildLog == null) return;
        var lbl = new Label(text);
        lbl.AddToClassList("build-log-entry");
        if (style == "ok")     lbl.AddToClassList("build-log-entry--ok");
        if (style == "err")    lbl.AddToClassList("build-log-entry--err");
        if (style == "claude") lbl.AddToClassList("build-log-entry--claude");
        _buildLog.Add(lbl);
        EditorApplication.delayCall += () => _buildLogScroll?.ScrollTo(lbl);
    }

    private void FinishBuild(bool success, string summary, string path, double sizeMB, double elapsed, int errors)
    {
        EditorPrefs.DeleteKey(SbActivePref);
        EditorPrefs.DeleteKey(SbIterPref);
        EditorPrefs.DeleteKey(SbOutputPref);
        EditorPrefs.DeleteKey(SbPlatPref);
        EditorPrefs.DeleteKey(SbScenesPref);
        EditorPrefs.DeleteKey(SbOptsPref);

        if (_buildResult != null)    _buildResult.RemoveFromClassList("build-result--hidden");
        if (_buildResultSummary != null) _buildResultSummary.text = summary;
        if (_buildResultPath    != null) _buildResultPath.text    = success ? "→ " + path : "";
        _buildOpenFolderBtn?.EnableInClassList("build-open-folder-btn--hidden",
            !success || string.IsNullOrEmpty(_lastBuildFolder));

        if (_buildBtn != null) _buildBtn.SetEnabled(true);
    }
}

}