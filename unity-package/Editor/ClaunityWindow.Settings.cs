using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace Claunity
{

public partial class ClaunityWindow
{
    // ══════════════════════════════════════════════════════════════════════════
    // SETTINGS, WELCOME & INSTALLER
    // ══════════════════════════════════════════════════════════════════════════

    // ── Settings panel ─────────────────────────────────────────────────────────

    private void SnapshotSettings()
    {
        _snapshotKey   = _apiKeyField.value;
        var modelDropdown = rootVisualElement.Q<DropdownField>("model-dropdown");
        _snapshotModel = modelDropdown?.value ?? "";
        var u = rootVisualElement.Q<DropdownField>("ai-source-dropdown");
        _snapshotUseClaudeCode  = u != null && u.value == "Claude Code";
        _snapshotHistoryLimit   = rootVisualElement.Q<IntegerField>("history-limit-field")?.value ?? 4;
        _snapshotPersonalPrompt = rootVisualElement.Q<TextField>("personal-prompt-field")?.value ?? "";
    }

    private bool HasUnsavedChanges()
    {
        var u = rootVisualElement.Q<DropdownField>("ai-source-dropdown");
        return _apiKeyField.value != _snapshotKey
            || rootVisualElement.Q<DropdownField>("model-dropdown").value != _snapshotModel
            || (u != null && u.value == "Claude Code") != _snapshotUseClaudeCode
            || (rootVisualElement.Q<IntegerField>("history-limit-field")?.value ?? 4) != _snapshotHistoryLimit
            || (rootVisualElement.Q<TextField>("personal-prompt-field")?.value ?? "") != _snapshotPersonalPrompt;
    }

    private void CloseSettings()
    {
        _settingsPanel.RemoveFromClassList("settings-panel--visible");
        _tabs.RemoveFromClassList("tabs--hidden");
        SwitchTab(_activeTab);
    }

    private void ToggleSettings()
    {
        bool open = _settingsPanel.ClassListContains("settings-panel--visible");
        if (open)
        {
            if (HasUnsavedChanges())
            {
                bool save = EditorUtility.DisplayDialog(
                    "Unsaved changes",
                    "You have unsaved settings. Save before closing?",
                    "Save", "Discard");

                if (save) OnSaveSettings();
                else      CloseSettings();
            }
            else
            {
                CloseSettings();
            }
        }
        else
        {
            SnapshotSettings();
            _settingsPanel.AddToClassList("settings-panel--visible");
            _tabs.AddToClassList("tabs--hidden");
            _chatView.AddToClassList("chat-view--hidden");
            _buildView?.AddToClassList("build-view--hidden");
            _testDashboard?.AddToClassList("test-dashboard--hidden");
            _projectView?.AddToClassList("project-view--hidden");
            _scoutView?.AddToClassList("scout-view--hidden");
            RefreshDailyUsage();
        }
    }

    private void ToggleKeyVisibility()
    {
        _apiKeyField.isPasswordField = !_apiKeyField.isPasswordField;
        rootVisualElement.Q<Button>("toggle-key-btn").text =
            _apiKeyField.isPasswordField ? "\ud83d\udc41" : "\ud83d\ude48";
    }

    private void OnSaveSettings()
    {
        var key   = _apiKeyField.value?.Trim() ?? "";
        var model = rootVisualElement.Q<DropdownField>("model-dropdown").value;

        var aiSourceDropdownCheck = rootVisualElement.Q<DropdownField>("ai-source-dropdown");
        bool isClaudeCode = aiSourceDropdownCheck?.value == "Claude Code";

        // Warn if API key is empty and not using Claude Code
        if (string.IsNullOrEmpty(key) && !isClaudeCode)
        {
            bool proceed = EditorUtility.DisplayDialog(
                "Claunity",
                "API key is empty. You won't be able to use Claunity until you set one.\n\nSave anyway?",
                "Save", "Cancel");
            if (!proceed) return;
        }

        EditorPrefs.SetString(ApiKeyPref, key);
        EditorPrefs.SetString(ModelPref,  model);

        var aiSourceDropdown = rootVisualElement.Q<DropdownField>("ai-source-dropdown");
        bool useClaudeCode = aiSourceDropdown?.value == "Claude Code";
        EditorPrefs.SetBool(UseClaudeCodePref, useClaudeCode);

        int historyLimit = rootVisualElement.Q<IntegerField>("history-limit-field")?.value ?? 4;
        historyLimit = System.Math.Max(1, System.Math.Min(historyLimit, 100));
        EditorPrefs.SetInt(HistoryLimitPref, historyLimit);

        var personalPrompt = rootVisualElement.Q<TextField>("personal-prompt-field")?.value ?? "";
        EditorPrefs.SetString(PersonalPromptPref, personalPrompt);

        EditorCoroutineUtility.StartCoroutineOwnerless(PostConfig(key, model: model, useClaudeCode: useClaudeCode, personalPrompt: personalPrompt, historyLimit: historyLimit));
        SnapshotSettings(); // mark as saved so ToggleSettings won't ask again
        CloseSettings();
    }

    private void OnTestConnection() =>
        EditorCoroutineUtility.StartCoroutineOwnerless(TestConnectionCoroutine());

    private void OnClearUserData()
    {
        bool confirmed = EditorUtility.DisplayDialog(
            "Clear User Data",
            "This will delete all chat history and project plans. This cannot be undone.\n\nContinue?",
            "Clear", "Cancel");

        if (!confirmed) return;

        var statusLabel = rootVisualElement.Q<Label>("clear-data-status");

        try
        {
            // Clear chat history
            if (System.IO.File.Exists(HistoryFilePath))
                System.IO.File.WriteAllText(HistoryFilePath, "{}");

            // Clear project plan
            var planPath = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "../", ProjectPlanPath));
            if (System.IO.File.Exists(planPath))
                System.IO.File.WriteAllText(planPath, "{}");

            // Reset in-memory state
            _history.Clear();
            _chatContainer?.Clear();
            _hasMessages = false;
            _tabMessages.Clear();
            _tabHistories.Clear();
            _tabHasMessages.Clear();

            if (statusLabel != null)
            {
                statusLabel.text = "Data cleared successfully.";
                statusLabel.style.color = new StyleColor(new Color(0.4f, 0.8f, 0.4f));
                rootVisualElement.schedule.Execute(() => statusLabel.text = "").StartingIn(3000);
            }
        }
        catch (System.Exception ex)
        {
            if (statusLabel != null)
            {
                statusLabel.text = $"Error: {ex.Message}";
                statusLabel.style.color = new StyleColor(new Color(1f, 0.4f, 0.4f));
            }
        }
    }

    // ── Daily usage display ────────────────────────────────────────────────────

    [System.Serializable]
    private class DailyUsageData { public int input_tokens; public int output_tokens; public int requests; public bool has_approx; }
    [System.Serializable]
    private class UsageResponse   { public DailyUsageData today; }

    private void RefreshDailyUsage()
    {
        EditorCoroutineUtility.StartCoroutineOwnerless(FetchDailyUsage());
    }

    private IEnumerator FetchDailyUsage()
    {
        using var req = UnityWebRequest.Get($"{ServerUrl}/usage");
        req.timeout = 5;
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success) yield break;

        var data = JsonUtility.FromJson<UsageResponse>(req.downloadHandler.text);
        if (data?.today == null) yield break;

        var lbl = rootVisualElement.Q<Label>("daily-usage-label");
        if (lbl == null) yield break;

        var prefix = data.today.has_approx ? "~" : "";
        lbl.text = $"Today: \u2191 {prefix}{FormatTokens(data.today.input_tokens)}  \u2193 {prefix}{FormatTokens(data.today.output_tokens)}  ({data.today.requests} requests)"
                 + (data.today.has_approx ? "  (includes Claude Code estimates)" : "");
    }

    private void OnResetDailyUsage()
    {
        EditorCoroutineUtility.StartCoroutineOwnerless(ResetDailyUsageCoroutine());
    }

    private IEnumerator ResetDailyUsageCoroutine()
    {
        using var req = new UnityWebRequest($"{ServerUrl}/usage/reset", "POST");
        req.uploadHandler   = new UploadHandlerRaw(new byte[0]);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.timeout = 5;
        yield return req.SendWebRequest();
        RefreshDailyUsage();
    }

    // ── Backend control ────────────────────────────────────────────────────────

    private void OnStopBackend()
    {
        EditorCoroutineUtility.StartCoroutineOwnerless(StopBackendCoroutine(restart: false));
    }

    private void OnRestartBackend()
    {
        EditorCoroutineUtility.StartCoroutineOwnerless(StopBackendCoroutine(restart: true));
    }

    private IEnumerator StopBackendCoroutine(bool restart)
    {
        var dot   = rootVisualElement.Q("backend-status-dot");
        var label = rootVisualElement.Q<Label>("backend-status-label");

        if (dot   != null) { dot.RemoveFromClassList("backend-dot--on"); dot.AddToClassList("backend-dot--off"); }
        if (label != null) label.text = restart ? "Restarting..." : "Stopping...";

        using (var req = new UnityWebRequest($"{ServerUrl}/shutdown", "POST"))
        {
            req.uploadHandler   = new UploadHandlerRaw(new byte[0]);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout = 3;
            yield return req.SendWebRequest();
        }

        if (!restart)
        {
            // Return to welcome screen
            CloseSettings();
            EditorPrefs.SetBool(StartedPref, false);
            _mainUI.AddToClassList("main-ui--hidden");
            _welcomeScreen.style.display = DisplayStyle.Flex;
            SetWelcomeState(WelcomeState.NotRunning);
        }
        else
        {
            yield return new EditorWaitForSeconds(1.5f);
            EditorCoroutineUtility.StartCoroutineOwnerless(LaunchAndConnect());
        }
    }

    // ── Welcome screen / local backend ─────────────────────────────────────────
    //
    // The backend is a small Python (FastAPI) service that ships inside this package
    // (Backend~/). "Set up" creates a private virtual environment and installs its
    // dependencies; "Start" runs main.py from that environment on 127.0.0.1:8765.

    private const string PackageName    = "com.claunity.editor";
    private const int    MinPythonVer   = 309;   // 3.9
    private const string DepsMarkerFile = ".claunity-deps";

    private static volatile string _setupStatus = "";

    private void SetWelcomeState(WelcomeState state, string extra = "")
    {
        _welcomeProgress?.EnableInClassList("welcome-progress--hidden", true);
        _welcomeActionBtn?.EnableInClassList("welcome-action-btn--hidden", true);
        _welcomeActionBtn?.RemoveFromClassList("welcome-action-btn--error");

        switch (state)
        {
            case WelcomeState.Checking:
                if (_welcomeStatus != null) _welcomeStatus.text = "Checking...";
                if (_welcomeHint   != null) _welcomeHint.text   = "";
                break;

            case WelcomeState.NotInstalled:
                if (_welcomeStatus != null) _welcomeStatus.text = "Backend is not set up yet";
                if (_welcomeHint   != null) _welcomeHint.text   = "Needs Python 3.9+ · one-time setup, about a minute";
                ShowWelcomeBtn("⚙   Set up Claunity backend", OnSetupClicked);
                break;

            case WelcomeState.Installing:
                if (_welcomeStatus != null) _welcomeStatus.text = "Setting up...";
                if (_welcomeHint   != null) _welcomeHint.text   = "Creating a private Python environment";
                break;

            case WelcomeState.NotRunning:
                if (_welcomeStatus != null) _welcomeStatus.text = "Ready to launch";
                if (_welcomeHint   != null) _welcomeHint.text   = "Runs locally on 127.0.0.1:8765";
                ShowWelcomeBtn("▶   Start Claunity", OnStartBtnClicked);
                break;

            case WelcomeState.Starting:
                if (_welcomeStatus != null) _welcomeStatus.text = "Starting...";
                if (_welcomeHint   != null) _welcomeHint.text   = "Connecting to backend";
                break;

            case WelcomeState.Error:
                if (_welcomeStatus != null) _welcomeStatus.text = extra;
                if (_welcomeHint   != null) _welcomeHint.text   = "";
                ShowWelcomeBtn("Retry", OnRetryClicked);
                _welcomeActionBtn?.AddToClassList("welcome-action-btn--error");
                break;
        }
    }

    private void ShowWelcomeBtn(string label, Action callback)
    {
        if (_welcomeActionBtn == null) return;
        _welcomeActionBtn.text = label;
        _welcomeActionBtn.EnableInClassList("welcome-action-btn--hidden", false);
        _welcomeActionCallback = callback;
    }

    // On fresh window open — auto-connect if the backend is already running
    private IEnumerator CheckInstallationOnStart()
    {
        SetWelcomeState(WelcomeState.Checking);

        using (var req = UnityWebRequest.Get($"{ServerUrl}/health"))
        {
            req.timeout = 5;
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success)
            {
                EnterMainUI();
                yield break;
            }
        }

        SetWelcomeState(IsInstalled() ? WelcomeState.NotRunning : WelcomeState.NotInstalled);
    }

    private void OnStartBtnClicked() => EditorCoroutineUtility.StartCoroutineOwnerless(LaunchAndConnect());
    private void OnSetupClicked()    => EditorCoroutineUtility.StartCoroutineOwnerless(SetupBackend());
    private void OnRetryClicked()    => EditorCoroutineUtility.StartCoroutineOwnerless(CheckInstallationOnStart());

    // ── Paths ──────────────────────────────────────────────────────────────────

    private static bool IsWindowsEditor => Application.platform == RuntimePlatform.WindowsEditor;

    private string GetDataDir()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home  = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (IsWindowsEditor)
            return Path.Combine(local, "Claunity");
        if (Application.platform == RuntimePlatform.OSXEditor)
            return Path.Combine(home, "Library", "Application Support", "Claunity");
        return Path.Combine(home, ".local", "share", "Claunity");
    }

    private string GetVenvDir() => Path.Combine(GetDataDir(), "venv");

    private string GetVenvPython() => IsWindowsEditor
        ? Path.Combine(GetVenvDir(), "Scripts", "python.exe")
        : Path.Combine(GetVenvDir(), "bin", "python");

    // Folder with main.py. Works when installed as a package (git URL / disk / embedded)
    // and when the folder was copied into Assets/.
    private string GetBackendDir()
    {
        var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssetPath($"Packages/{PackageName}");
        if (pkg != null)
        {
            var dir = Path.Combine(pkg.resolvedPath, "Backend~");
            if (File.Exists(Path.Combine(dir, "main.py"))) return dir;
        }

        foreach (var guid in AssetDatabase.FindAssets("ClaunityWindow t:MonoScript"))
        {
            var assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (!assetPath.EndsWith("/ClaunityWindow.cs")) continue;
            var editorDir  = Path.GetDirectoryName(Path.GetFullPath(assetPath));
            var packageDir = Directory.GetParent(editorDir)?.FullName;
            if (packageDir == null) continue;
            var dir = Path.Combine(packageDir, "Backend~");
            if (File.Exists(Path.Combine(dir, "main.py"))) return dir;
        }
        return null;
    }

    private static string RequirementsFingerprint(string backendDir)
    {
        var path = Path.Combine(backendDir, "requirements.txt");
        if (!File.Exists(path)) return "";
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
    }

    // Set up = a venv exists whose installed dependencies match the shipped requirements.txt
    private bool IsInstalled()
    {
        var backend = GetBackendDir();
        if (backend == null || !File.Exists(GetVenvPython())) return false;
        var marker = Path.Combine(GetVenvDir(), DepsMarkerFile);
        return File.Exists(marker) && File.ReadAllText(marker).Trim() == RequirementsFingerprint(backend);
    }

    // ── Process helper (blocking — call from a background thread) ──────────────

    private static int RunProcess(string file, string args, string workDir, int timeoutMs,
                                  out string stdout, out string stderr)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file, args)
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        if (!string.IsNullOrEmpty(workDir)) psi.WorkingDirectory = workDir;

        var outSb = new System.Text.StringBuilder();
        var errSb = new System.Text.StringBuilder();
        using (var p = new System.Diagnostics.Process { StartInfo = psi })
        {
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
            p.ErrorDataReceived  += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                stdout = outSb.ToString(); stderr = "Timed out.";
                return -1;
            }
            p.WaitForExit();   // flush async output
            stdout = outSb.ToString(); stderr = errSb.ToString();
            return p.ExitCode;
        }
    }

    // First interpreter on PATH that is Python >= 3.9
    private static bool FindPython(bool win, out string file, out string prefixArgs)
    {
        var candidates = win
            ? new[] { ("py", "-3 "), ("python", ""), ("python3", "") }
            : new[] { ("python3", ""), ("python", "") };

        foreach (var (f, a) in candidates)
        {
            try
            {
                var code = RunProcess(f, a + "-c \"import sys; print(sys.version_info[0]*100+sys.version_info[1])\"",
                                      null, 15000, out var o, out _);
                if (code == 0 && int.TryParse(o.Trim(), out var ver) && ver >= MinPythonVer)
                {
                    file = f; prefixArgs = a;
                    return true;
                }
            }
            catch { /* not installed under this name */ }
        }
        file = null; prefixArgs = null;
        return false;
    }

    private static string LastLines(string text, int n)
    {
        var lines = (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n", lines.Skip(Math.Max(0, lines.Length - n)));
    }

    // ── Set up (venv + dependencies) ───────────────────────────────────────────

    private IEnumerator SetupBackend()
    {
        SetWelcomeState(WelcomeState.Installing);

        var backendDir = GetBackendDir();
        if (backendDir == null)
        {
            SetWelcomeState(WelcomeState.Error,
                "Backend files not found.\nThe package must contain the Backend~ folder.");
            yield break;
        }

        // Resolve everything Unity-API-dependent here, on the main thread.
        bool   win       = IsWindowsEditor;
        string dataDir   = GetDataDir();
        string venvDir   = GetVenvDir();
        string venvPy    = GetVenvPython();

        string error = null;
        _setupStatus = "Looking for Python 3.9+";
        var thread = new System.Threading.Thread(() =>
        {
            try { error = RunSetup(backendDir, dataDir, venvDir, venvPy, win); }
            catch (Exception ex) { error = ex.Message; }
        });
        thread.Start();

        while (thread.IsAlive)
        {
            if (_welcomeHint != null) _welcomeHint.text = _setupStatus;
            yield return null;
        }

        if (error != null)
        {
            SetWelcomeState(WelcomeState.Error, error);
            yield break;
        }
        SetWelcomeState(WelcomeState.NotRunning);
    }

    // Runs on a background thread. Returns null on success, otherwise an error message.
    private static string RunSetup(string backendDir, string dataDir, string venvDir, string venvPy, bool win)
    {
        if (!FindPython(win, out var py, out var pyArgs))
            return "Python 3.9 or newer was not found.\nInstall it from python.org, restart Unity and retry.";

        Directory.CreateDirectory(dataDir);

        if (!File.Exists(venvPy))
        {
            _setupStatus = "Creating virtual environment";
            var code = RunProcess(py, pyArgs + $"-m venv \"{venvDir}\"", null, 120_000, out _, out var err);
            if (code != 0)
                return "Could not create the virtual environment:\n" + LastLines(err, 4);
        }

        _setupStatus = "Installing dependencies (this can take a minute)";
        var req  = Path.Combine(backendDir, "requirements.txt");
        var pip  = RunProcess(venvPy,
                              $"-m pip install --disable-pip-version-check -r \"{req}\"",
                              backendDir, 600_000, out _, out var pipErr);
        if (pip != 0)
            return "Installing dependencies failed:\n" + LastLines(pipErr, 4);

        File.WriteAllText(Path.Combine(venvDir, DepsMarkerFile), RequirementsFingerprint(backendDir));
        return null;
    }

    // ── Launch & Connect ───────────────────────────────────────────────────────

    private System.Diagnostics.Process _backendProcess;

    private IEnumerator LaunchAndConnect()
    {
        SetWelcomeState(WelcomeState.Starting);

        var backendDir = GetBackendDir();
        if (backendDir == null || !IsInstalled())
        {
            SetWelcomeState(WelcomeState.NotInstalled);
            yield break;
        }

        // Fine if it is already running — the port is then taken and /health still answers.
        string startError = null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(GetVenvPython(), "main.py")
            {
                WorkingDirectory = backendDir,
                UseShellExecute  = false,
                CreateNoWindow   = true,
            };
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            _backendProcess = System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { startError = ex.Message; }

        if (startError != null)
        {
            SetWelcomeState(WelcomeState.Error, $"Could not start the backend:\n{startError}");
            yield break;
        }

        // Poll /health for up to 30 seconds
        float elapsed = 0f;
        while (elapsed < 30f)
        {
            yield return new EditorWaitForSeconds(0.5f);
            elapsed += 0.5f;

            using var req = UnityWebRequest.Get($"{ServerUrl}/health");
            req.timeout = 5;
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                EnterMainUI();
                yield break;
            }
        }

        var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                               ".config", "claunity", "claunity.log");
        SetWelcomeState(WelcomeState.Error,
            $"Could not connect to the Claunity backend.\nSee the log: {log}");
    }

    // ── Backend version label ──────────────────────────────────────────────────

    private IEnumerator ShowBackendVersion()
    {
        using var req = UnityWebRequest.Get($"{ServerUrl}/version");
        req.timeout = 5;
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success) yield break;

        VersionResponse vr;
        try { vr = JsonUtility.FromJson<VersionResponse>(req.downloadHandler.text); }
        catch { yield break; }
        if (vr == null) yield break;

        var versionLabel = rootVisualElement?.Q<Label>("app-version-label");
        if (versionLabel != null) versionLabel.text = $"Claunity backend v{vr.version}";
    }
}

}
