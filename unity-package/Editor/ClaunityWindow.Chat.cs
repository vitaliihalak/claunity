using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
    // CHAT — bubbles, input, streaming, actions, history
    // ══════════════════════════════════════════════════════════════════════════

    // ── Project Intelligence ───────────────────────────────────────────────────

    private void IndexProject()
    {
        var guids = AssetDatabase.FindAssets("t:Script", new[] { "Assets" });
        var paths = guids
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => p.EndsWith(".cs") && !p.Contains("/Claunity/"))
            .OrderBy(p => p)
            .ToArray();

        _indexedCount = paths.Length;

        // Take snapshot of write times on first index (session start)
        if (_sessionSnapshot.Count == 0)
        {
            foreach (var p in paths)
            {
                try { _sessionSnapshot[p] = File.GetLastWriteTime(p); }
                catch { }
            }
        }

        // Determine which files changed since session start
        var changedPaths = paths
            .Where(p => {
                try {
                    return !_sessionSnapshot.TryGetValue(p, out var snap)
                        || File.GetLastWriteTime(p) > snap;
                } catch { return false; }
            })
            .ToArray();

        var sb = new StringBuilder();
        sb.AppendLine($"=== Unity Project: {paths.Length} C# scripts ===");
        sb.AppendLine("// All scripts (names only):");
        foreach (var p in paths)
            sb.AppendLine($"//   {p}");

        int fullCount = 0;
        if (changedPaths.Length > 0)
        {
            sb.AppendLine($"\n// Modified this session ({changedPaths.Length}) — use read_script for full content:");
            foreach (var path in changedPaths)
            {
                sb.AppendLine($"//   {path}");
                fullCount++;
            }
        }

        _projectContext = sb.ToString();

        var statusLabel = rootVisualElement.Q<Label>("index-status");
        if (statusLabel != null)
        {
            statusLabel.text = changedPaths.Length > 0
                ? $"📁 {_indexedCount} scripts  •  {fullCount} modified this session"
                : $"📁 {_indexedCount} scripts indexed";
        }
    }

    // ── Client-side rate limiter ──────────────────────────────────────────────

    /// <summary>
    /// Returns false and shows a warning dialog if too many requests were sent recently.
    /// Call this before every /chat request. Returns true if safe to proceed.
    /// </summary>
    private bool CheckClientRateLimit()
    {
        if (_ratePausedByUser) return false;

        var now = EditorApplication.timeSinceStartup;
        _chatTimestamps.RemoveAll(t => now - t > ClientRateWindow);
        _chatTimestamps.Add(now);

        if (_chatTimestamps.Count > ClientRateLimit)
        {
            _ratePausedByUser = true;

            // Stop any active project execution
            if (_projectExecuting)
            {
                _projectPauseRequested = true;
                _projectExecuting = false;
                EditorPrefs.SetBool(ProjectPausePref, true);
            }
            _waitingForResponse = false;
            UpdateSendButton();
            StopThinking();
            StopProjectLoading();

            bool continueAnyway = EditorUtility.DisplayDialog(
                "Claunity — Too Many Requests",
                $"Claunity sent {_chatTimestamps.Count} requests in the last {ClientRateWindow}s.\n\n" +
                "This may indicate a bug consuming your API quota.\n\n" +
                "Continue sending requests?",
                "Continue", "Stop");

            if (continueAnyway)
            {
                _ratePausedByUser = false;
                _chatTimestamps.Clear();
                return true;
            }

            if (_projectExecuting || (_plan != null && _plan.state == "executing"))
            {
                if (_plan != null) _plan.state = "paused";
                SaveProjectPlan();
                SetProjectBoardStatus("⏸  Paused — rate limit protection triggered");
                UpdateProjectControls();
            }
            return false;
        }
        return true;
    }

    // ── Token usage tracking ──────────────────────────────────────────────────

    private void TrackTokenUsage(int inputTokens, int outputTokens)
    {
        _sessionInputTokens  += inputTokens;
        _sessionOutputTokens += outputTokens;

        // Update session counter in toolbar
        if (_sessionTokenCounter != null)
            _sessionTokenCounter.text = $"↑ {FormatTokens(_sessionInputTokens)}  ↓ {FormatTokens(_sessionOutputTokens)}";

        // Add per-message usage label to chat (last message in container)
        if (_chatContainer != null && _chatContainer.childCount > 0)
        {
            var lbl = new Label($"↑ {FormatTokens(inputTokens)}  ↓ {FormatTokens(outputTokens)}");
            lbl.AddToClassList("token-usage-label");
            _chatContainer.Add(lbl);
        }
    }

    private static string FormatTokens(int tokens)
    {
        if (tokens >= 1000) return $"{tokens / 1000.0:0.#}k";
        return tokens.ToString();
    }

    // ── Script signature extractor ────────────────────────────────────────────

    private static readonly System.Text.RegularExpressions.Regex _sigRegex =
        new System.Text.RegularExpressions.Regex(
            @"^\s*(public|protected|private|internal|static|abstract|virtual|override|sealed|\[SerializeField\]|\[Header|\[Tooltip).*$",
            System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>
    /// Extracts class/struct declarations, public/serialized fields and public method
    /// signatures from C# source. Gives Claude structural awareness without full code.
    /// </summary>
    private static string ExtractSignatures(string source)
    {
        var lines = source.Split('\n');
        var result = new StringBuilder();
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.TrimStart();

            // Class / struct / interface / enum declarations
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed,
                @"^(public|internal|private|protected)\s+(static\s+|abstract\s+|sealed\s+)?(class|struct|interface|enum)\s+"))
            {
                result.AppendLine(line);
                continue;
            }

            // [SerializeField] attribute lines
            if (trimmed.StartsWith("[SerializeField]") || trimmed.StartsWith("[Header") || trimmed.StartsWith("[Tooltip"))
            {
                result.AppendLine(line);
                continue;
            }

            // Public fields and properties
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed,
                @"^public\s+.+\s+\w+\s*(;|{|\[|=)"))
            {
                result.AppendLine(line);
                continue;
            }

            // Public / protected method signatures (line ending with { or ;)
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed,
                @"^(public|protected)\s+.+\s+\w+\s*\(") && (line.Contains("{") || line.TrimEnd().EndsWith(";")))
            {
                // Strip method body — keep only the signature line
                var sig = line.Contains("{") ? line.Substring(0, line.IndexOf('{')) + "{}" : line;
                result.AppendLine(sig.TrimEnd());
                continue;
            }
        }
        return result.ToString().TrimEnd();
    }

    // ── Chat history & clear ───────────────────────────────────────────────────

    private void OnClearChat()
    {
        _chatContainer.Clear();
        _history.Clear();
        _hasMessages = false;
        _chatAutoLooping = false;
        _chatActionsCount = 0;
        _sessionInputTokens  = 0;
        _sessionOutputTokens = 0;
        if (_sessionTokenCounter != null) _sessionTokenCounter.text = "";

        if (_tabMessages.ContainsKey(_activeTab))    _tabMessages[_activeTab]    = new List<VisualElement>();
        if (_tabHistories.ContainsKey(_activeTab))   _tabHistories[_activeTab]   = new List<HistoryMessage>();
        if (_tabHasMessages.ContainsKey(_activeTab)) _tabHasMessages[_activeTab] = false;

        rootVisualElement.Q("chat-toolbar").AddToClassList("chat-toolbar--hidden");
        _continueBtn?.AddToClassList("continue-btn--hidden");
        RefreshSplash(_activeTab);
    }

    private void OnContinueClicked()
    {
        if (_waitingForResponse) return;
        _inputField.value = "";
        PostChat("Continue.", isContinuation: true);
        _continueBtn?.AddToClassList("continue-btn--hidden");
    }

    private void UpdateContinueBtn()
    {
        if (_continueBtn == null) return;
        // Show only in chat tab when Claude just responded and is idle
        bool show = _hasMessages && !_waitingForResponse
                    && (_activeTab == "chat");
        _continueBtn.EnableInClassList("continue-btn--hidden", !show);
    }

    // ── Mode Splash ────────────────────────────────────────────────────────────

    private void RefreshSplash(string tabId)
    {
        if (_hasMessages)
        {
            HideSplash();
            return;
        }
        ShowSplash(tabId);
    }

    private void ShowSplash(string tabId)
    {
        var splash = rootVisualElement.Q("mode-splash");
        if (splash == null || _chatScroll == null) return;

        splash.Clear();
        splash.RemoveFromClassList("mode-splash--hidden");
        _chatScroll.AddToClassList("chat-scroll--hidden");

        GetSplashContent(tabId, out var icon, out var name, out var desc, out var examples);

        if (string.IsNullOrEmpty(name)) return;

        var iconLbl = new Label(icon);
        iconLbl.AddToClassList("splash-icon");

        var nameLbl = new Label(name);
        nameLbl.AddToClassList("splash-name");

        var descLbl = new Label(desc);
        descLbl.AddToClassList("splash-desc");

        var tryLabel = new Label("TRY ASKING");
        tryLabel.AddToClassList("splash-examples-label");

        splash.Add(iconLbl);
        splash.Add(nameLbl);
        splash.Add(descLbl);

        splash.Add(tryLabel);

        foreach (var ex in examples)
        {
            var text = ex;
            var chip = new Button(() =>
            {
                _inputField.value = text;
                _inputField.Focus();
            });
            chip.text = $"\"{text}\"";
            chip.AddToClassList("splash-chip");
            splash.Add(chip);
        }
    }

    private void HideSplash()
    {
        rootVisualElement.Q("mode-splash").AddToClassList("mode-splash--hidden");
        _chatScroll.RemoveFromClassList("chat-scroll--hidden");
    }

    private static void GetSplashContent(string tabId,
        out string icon, out string name, out string desc, out string[] examples)
    {
        if (tabId == "chat")
        {
            icon     = "💬";
            name     = "Chat Mode";
            desc     = "Chat, ask, build — anything goes.\nFor big tasks Claunity breaks them into steps.";
            examples = new[] {
                "What scripts handle player movement?",
                "Add a double jump to the Player",
                "Create a health system with a UI bar",
                "How should I structure my inventory system?",
            };
        }
        else if (tabId == "project")
        {
            icon     = "🗂";
            name     = "Project Mode";
            desc     = "From idea to full project skeleton.\nPaste your GDD and let's build together.";
            examples = new[] {
                "I want to make a 2D platformer, help me plan it",
                "Generate a project structure for a tower defense game",
            };
        }
        else if (tabId == "test")
        {
            icon     = "🎮";
            name     = "Play & Test Mode";
            desc     = "Enter Play Mode, watch your game,\nread the console — get a full bug report.";
            examples = new[] {
                "Test the game and tell me what looks broken",
                "Enter Play Mode and check for console errors",
                "Take a screenshot and describe the UI",
            };
        }
        else
        {
            icon = ""; name = ""; desc = ""; examples = new string[0];
        }
    }

    // ── Chat bubbles ───────────────────────────────────────────────────────────

    private void AddMessage(string text, bool isUser)
    {
        if (!_hasMessages)
        {
            _hasMessages = true;
            var msgTab = !string.IsNullOrEmpty(_requestTab) ? _requestTab : _activeTab;
            if (_tabHasMessages.ContainsKey(msgTab))
                _tabHasMessages[msgTab] = true;
            HideSplash();
            rootVisualElement.Q("chat-toolbar").RemoveFromClassList("chat-toolbar--hidden");
        }

        var label = new Label(isUser ? text : ParseMarkdown(text));
        label.AddToClassList("bubble-text");
        label.enableRichText = true;

        var bubble = MakeBubble(label, isUser);

        if (!isUser)
        {
            var rawText = text;
            var copyBtn = new Button();
            copyBtn.text = "⎘ copy";
            copyBtn.AddToClassList("copy-btn");
            copyBtn.clicked += () =>
            {
                GUIUtility.systemCopyBuffer = rawText;
                copyBtn.text = "✓ copied";
                copyBtn.AddToClassList("copy-btn--done");
                copyBtn.schedule.Execute(() =>
                {
                    copyBtn.text = "⎘ copy";
                    copyBtn.RemoveFromClassList("copy-btn--done");
                }).StartingIn(1500);
            };
            bubble.Add(copyBtn);
        }

        var row = MakeRow(isUser);
        if (!isUser) row.Add(MakeAvatar());
        row.Add(bubble);
        _chatContainer.Add(row);
        ScrollToBottom();

        // Persist Chat and Project chats
        if (!_rebuilding && (_activeTab == "chat" || _activeTab == "project"))
            SaveChatHistory();
    }

    /// <summary>Small inline system note (e.g. "Response was cut off — continuing...").</summary>
    private void AddSystemNote(string text)
    {
        var label = new Label(text);
        label.AddToClassList("system-note");
        _chatContainer.Add(label);
        ScrollToBottom();
    }

    private void AddSummaryCard(string text, int actionsCount)
    {
        if (!_hasMessages)
        {
            _hasMessages = true;
            var msgTab = !string.IsNullOrEmpty(_requestTab) ? _requestTab : _activeTab;
            if (_tabHasMessages.ContainsKey(msgTab)) _tabHasMessages[msgTab] = true;
            HideSplash();
            rootVisualElement.Q("chat-toolbar").RemoveFromClassList("chat-toolbar--hidden");
        }

        var row = new VisualElement();
        row.AddToClassList("message-row");
        row.Add(MakeAvatar());

        var card = new VisualElement();
        card.AddToClassList("summary-card");

        var header = new Label($"✅ Done — {actionsCount} action{(actionsCount != 1 ? "s" : "")} executed");
        header.AddToClassList("summary-card-header");
        card.Add(header);

        var divider = new VisualElement();
        divider.AddToClassList("action-divider");
        card.Add(divider);

        var label = new Label(ParseMarkdown(text));
        label.AddToClassList("bubble-text");
        label.enableRichText = true;
        card.Add(label);

        var rawText = text;
        var copyBtn = new Button { text = "⎘ copy" };
        copyBtn.AddToClassList("copy-btn");
        copyBtn.clicked += () =>
        {
            GUIUtility.systemCopyBuffer = rawText;
            copyBtn.text = "✓ copied";
            copyBtn.AddToClassList("copy-btn--done");
            copyBtn.schedule.Execute(() =>
            {
                copyBtn.text = "⎘ copy";
                copyBtn.RemoveFromClassList("copy-btn--done");
            }).StartingIn(1500);
        };
        card.Add(copyBtn);

        row.Add(card);
        _chatContainer.Add(row);
        ScrollToBottom();

        if (!_rebuilding) SaveChatHistory();
    }

    private void ShowErrorCard(string message, bool retryable, string retryMsg, string retryCtx, string retryImg)
    {
        if (!_hasMessages)
        {
            _hasMessages = true;
            if (_tabHasMessages.ContainsKey(_activeTab)) _tabHasMessages[_activeTab] = true;
            HideSplash();
            rootVisualElement.Q("chat-toolbar").RemoveFromClassList("chat-toolbar--hidden");
        }

        var row = new VisualElement();
        row.AddToClassList("message-row");
        row.Add(MakeAvatar());

        var card = new VisualElement();
        card.AddToClassList("error-card");

        var lbl = new Label(message);
        lbl.AddToClassList("error-card-text");
        card.Add(lbl);

        if (retryable)
        {
            var retryBtn = new Button();
            retryBtn.text = "↩  Try again";
            retryBtn.AddToClassList("error-retry-btn");
            retryBtn.clicked += () =>
            {
                row.RemoveFromHierarchy();
                _waitingForResponse = true;
                UpdateSendButton();
                StartThinking();
                EditorCoroutineUtility.StartCoroutineOwnerless(PostChat(retryMsg, context: retryCtx, image: retryImg));
            };
            card.Add(retryBtn);
        }

        row.Add(card);
        _chatContainer.Add(row);
        ScrollToBottom();
    }

    private void AddInfoMessage(string text)
    {
        var row = new VisualElement();
        row.AddToClassList("message-row");
        row.Add(MakeAvatar());
        var bubble = new VisualElement();
        bubble.AddToClassList("bubble");
        bubble.AddToClassList("bubble--info");
        var label = new Label(text);
        label.AddToClassList("bubble-text");
        label.AddToClassList("bubble-text--info");
        bubble.Add(label);
        row.Add(bubble);
        _chatContainer.Add(row);
        if (!_hasMessages)
        {
            _hasMessages = true;
            if (_tabHasMessages.ContainsKey(_activeTab)) _tabHasMessages[_activeTab] = true;
            HideSplash();
            rootVisualElement.Q("chat-toolbar").RemoveFromClassList("chat-toolbar--hidden");
        }
        ScrollToBottom();
    }

    private static VisualElement MakeRow(bool isUser)
    {
        var row = new VisualElement();
        row.AddToClassList("message-row");
        if (isUser) row.AddToClassList("message-row--user");
        return row;
    }

    private static VisualElement MakeBubble(VisualElement content, bool isUser)
    {
        var bubble = new VisualElement();
        bubble.AddToClassList("bubble");
        bubble.AddToClassList(isUser ? "bubble--user" : "bubble--claunity");
        bubble.Add(content);
        return bubble;
    }

    private static Label MakeAvatar()
    {
        var a = new Label("C");
        a.AddToClassList("avatar");
        return a;
    }

    private void ScrollToBottom() =>
        _chatScroll.schedule
            .Execute(() => _chatScroll.scrollOffset = new Vector2(0, float.MaxValue))
            .StartingIn(50);

    // ── Input ──────────────────────────────────────────────────────────────────

    private void OnInputKeyDown(KeyDownEvent e)
    {
        if (e.keyCode == KeyCode.Escape)
        {
            HideMentionDropdown();
            return;
        }

        if (_mentionPrefix != null && _mentionItems.Count > 0)
        {
            if (e.keyCode == KeyCode.Tab)
            {
                e.StopPropagation();
                SelectMention(_mentionItems[_mentionIndex >= 0 ? _mentionIndex : 0]);
                return;
            }
            if (e.keyCode == KeyCode.DownArrow)
            {
                e.StopPropagation();
                SetMentionIndex((_mentionIndex + 1) % _mentionItems.Count);
                return;
            }
            if (e.keyCode == KeyCode.UpArrow)
            {
                e.StopPropagation();
                SetMentionIndex((_mentionIndex - 1 + _mentionItems.Count) % _mentionItems.Count);
                return;
            }
        }

        if (e.keyCode == KeyCode.Return && e.ctrlKey)
        {
            e.StopPropagation();
            OnSendClicked();
        }
    }

    private void UpdateInputHeight()
    {
        if (_inputField == null) return;
        int lines = _inputField.value.Split('\n').Length;
        float newHeight = Mathf.Clamp(lines * 19f + 14f, 44f, 160f);
        _inputField.style.height = newHeight;
    }

    private void UpdateSendButton()
    {
        bool canSend = !string.IsNullOrWhiteSpace(_inputField?.value) && !_waitingForResponse;
        if (_sendBtn != null)
        {
            _sendBtn.SetEnabled(canSend);
            _sendBtn.EnableInClassList("send-btn--disabled", !canSend);
        }
        bool testBusy = _waitingForResponse || ClaunityTestRunner.IsPending;
        if (_testRunBarBtn != null)
            _testRunBarBtn.SetEnabled(!testBusy);
        if (!testBusy) HideTestLoading();
    }

    // ── @ mention autocomplete ─────────────────────────────────────────────────

    private void CheckMentionDropdown(string text)
    {
        var match = Regex.Match(text, @"@(\w*)$");
        if (match.Success)
            ShowMentionDropdown(match.Groups[1].Value);
        else
            HideMentionDropdown();
    }

    private void ShowMentionDropdown(string prefix)
    {
        _mentionPrefix = prefix;
        _mentionIndex  = -1;
        _mentionList.Clear();
        _mentionItems.Clear();

        var scripts = AssetDatabase.FindAssets("t:Script", new[] { "Assets" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => !p.Contains("/Claunity/"))
            .Select(p => (name: System.IO.Path.GetFileNameWithoutExtension(p), path: p))
            .Where(s => string.IsNullOrEmpty(prefix) ||
                        s.name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(s => s.name)
            .Take(8)
            .ToList();

        if (scripts.Count == 0) { HideMentionDropdown(); return; }

        foreach (var script in scripts)
        {
            _mentionItems.Add(script.name);
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;

            var nameLabel = new Label(script.name);
            nameLabel.style.flexGrow = 1;

            var pathLabel = new Label(script.path.Replace("Assets/", ""));
            pathLabel.AddToClassList("mention-path");

            row.Add(nameLabel);
            row.Add(pathLabel);

            var btn = new Button();
            btn.AddToClassList("mention-item");
            btn.Add(row);

            var captured = script.name;
            btn.clicked += () => SelectMention(captured);
            _mentionList.Add(btn);
        }

        _mentionDropdown.RemoveFromClassList("mention-dropdown--hidden");
    }

    private void SetMentionIndex(int index)
    {
        var buttons = _mentionList.Children().OfType<Button>().ToList();
        if (_mentionIndex >= 0 && _mentionIndex < buttons.Count)
            buttons[_mentionIndex].RemoveFromClassList("mention-item--selected");
        _mentionIndex = index;
        if (_mentionIndex >= 0 && _mentionIndex < buttons.Count)
            buttons[_mentionIndex].AddToClassList("mention-item--selected");
    }

    private void HideMentionDropdown()
    {
        _mentionDropdown?.AddToClassList("mention-dropdown--hidden");
        _mentionPrefix = null;
        _mentionIndex  = -1;
    }

    private void SelectMention(string scriptName)
    {
        var text    = _inputField.value;
        var newText = Regex.Replace(text, @"@\w*$", "@" + scriptName + " ");
        _inputField.value = newText;
        _inputField.Focus();
        HideMentionDropdown();
    }

    private void OnSendClicked()
    {
        if (_waitingForResponse) return;
        var text = _inputField.value.Trim();
        if (string.IsNullOrEmpty(text)) return;

        _lastSentMessage = text;
        AddMessage(text, isUser: true);
        _inputField.value = "";
        _inputField.Focus();
        HideMentionDropdown();

        _waitingForResponse = true;
        UpdateSendButton();
        StartThinking();

        var context  = _projectContext;
        var resolved = ResolveAtMentions(text, ref context);
        EditorCoroutineUtility.StartCoroutineOwnerless(PostChat(resolved, context: context));
    }

    // ── Feature buttons ───────────────────────────────────────────────────────

    private void SendFeatureMessage(string message, string context = null, string image = null)
    {
        SwitchTab("chat");
        if (_waitingForResponse) return;

        AddMessage(message, isUser: true);
        _waitingForResponse = true;
        UpdateSendButton();
        StartThinking();

        var resolved = ResolveAtMentions(message, ref context);
        EditorCoroutineUtility.StartCoroutineOwnerless(PostChat(resolved, context: context, image: image));
    }

    // ── Feature button helpers ─────────────────────────────────────────────────

    private void OnFeatScreenshot(string view)
    {
        SwitchTab("chat");
        if (_waitingForResponse) return;

        var label = view == "game" ? "How does the game look right now?" : "Look at the current scene";
        AddMessage(label, isUser: true);
        _waitingForResponse = true;
        UpdateSendButton();
        StartThinking();

        // Capture the screenshot synchronously and pass directly as image
        var result = ClaunityActionExecutor.Execute(new ActionPayload { type = "take_screenshot", view = view });
        string base64 = null;
        if (result != null && result.Length > 8 && result.StartsWith("[IMAGE:") && result.EndsWith("]"))
            base64 = result.Substring(7, result.Length - 8);

        EditorCoroutineUtility.StartCoroutineOwnerless(PostChat(label, image: base64));
    }

    private string BuildRecentChangesMessage()
    {
        var recentScripts = new List<string>();
        var cutoff = System.DateTime.Now.AddDays(-3);
        var guids = AssetDatabase.FindAssets("t:Script", new[] { "Assets" });
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            try
            {
                if (System.IO.File.GetLastWriteTime(path) >= cutoff)
                    recentScripts.Add(path);
            }
            catch { }
        }
        if (recentScripts.Count == 0)
            return "Show what has changed in the project recently (no scripts modified in last 3 days found, describe the general structure instead)";
        var joined = string.Join(", ", recentScripts.Select(p => System.IO.Path.GetFileNameWithoutExtension(p)));
        return $"Show what has changed in the project recently. These scripts were modified in the last 3 days: {joined}. Summarize the recent changes and their impact.";
    }

    // ── Picker overlay ─────────────────────────────────────────────────────────

    private void ShowScriptPicker(string title, Action<string> onSelect)
    {
        _pickerCallback = onSelect;
        _pickerAllItems.Clear();

        var guids = AssetDatabase.FindAssets("t:Script", new[] { "Assets" });
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.Contains("/Claunity/")) continue;
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            _pickerAllItems.Add((name, path));
        }
        _pickerAllItems.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

        rootVisualElement.Q<Label>("picker-title").text = title;
        _pickerSearch.SetValueWithoutNotify("");
        BuildPickerList(_pickerAllItems);
        _pickerOverlay.RemoveFromClassList("picker-overlay--hidden");
        _pickerSearch.Focus();
    }

    private void ShowGameObjectPicker(string title, Action<string> onSelect)
    {
        _pickerCallback = onSelect;
        _pickerAllItems.Clear();

        var gos = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
        foreach (var go in gos)
            _pickerAllItems.Add((go.name, go.name));
        _pickerAllItems.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

        rootVisualElement.Q<Label>("picker-title").text = title;
        _pickerSearch.SetValueWithoutNotify("");
        BuildPickerList(_pickerAllItems);
        _pickerOverlay.RemoveFromClassList("picker-overlay--hidden");
        _pickerSearch.Focus();
    }

    private void HidePicker()
    {
        _pickerOverlay.AddToClassList("picker-overlay--hidden");
        _pickerCallback = null;
    }

    private void FilterPicker(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            BuildPickerList(_pickerAllItems);
            return;
        }
        var filtered = _pickerAllItems
            .Where(x => x.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        BuildPickerList(filtered);
    }

    private void BuildPickerList(List<(string name, string path)> items)
    {
        _pickerList.Clear();
        foreach (var item in items)
        {
            var captured = item.name;
            var btn = new Button(() =>
            {
                var cb = _pickerCallback;
                HidePicker();
                cb?.Invoke(captured);
            });
            btn.AddToClassList("picker-item");
            btn.text = item.name;
            _pickerList.Add(btn);
        }
    }

    // ── Chat history persistence ───────────────────────────────────────────────

    private void SaveChatHistory()
    {
        if (_rebuilding) return;
        try
        {
            var store = new ChatHistoryStore();
            store.chat = BuildTabHistory("chat");

            var dir = System.IO.Path.GetDirectoryName(HistoryFilePath);
            if (!System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            System.IO.File.WriteAllText(HistoryFilePath, JsonUtility.ToJson(store, true));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Claunity] Could not save chat history: {e.Message}");
        }
    }

    private TabHistory BuildTabHistory(string tabId)
    {
        // Get the history for the given tab (active tab uses _history directly)
        List<HistoryMessage> hist;
        if (_activeTab == tabId)
            hist = _history;
        else if (!_tabHistories.TryGetValue(tabId, out hist))
            hist = new List<HistoryMessage>();

        var messages = hist
            .Where(h => h.role == "user" || h.role == "assistant" || h.role == "action")
            .TakeLast(MaxSavedMessages)
            .Select(h => new SavedMessage { role = h.role, content = h.content })
            .ToArray();
        return new TabHistory { messages = messages };
    }

    private void LoadChatHistory()
    {
        if (!System.IO.File.Exists(HistoryFilePath)) return;
        try
        {
            var json  = System.IO.File.ReadAllText(HistoryFilePath);
            var store = JsonUtility.FromJson<ChatHistoryStore>(json);
            if (store == null) return;

            _rebuilding = true;
            RebuildTabMessages("chat", store.chat);
            _rebuilding = false;
        }
        catch (Exception e)
        {
            _rebuilding = false;
            Debug.LogWarning($"[Claunity] Could not load chat history: {e.Message}");
        }
    }

    private void RebuildTabMessages(string tabId, TabHistory tabHistory)
    {
        if (tabHistory?.messages == null || tabHistory.messages.Length == 0) return;

        var hist = tabHistory.messages
            .Select(m => new HistoryMessage { role = m.role, content = m.content })
            .ToList();

        if (_activeTab == tabId)
        {
            // Active tab — populate container and history directly
            _history.Clear();
            _hasMessages = false;
            _chatContainer.Clear();

            foreach (var msg in tabHistory.messages)
            {
                if (msg.role == "action")
                    AddInfoMessage(msg.content);
                else
                {
                    _history.Add(new HistoryMessage { role = msg.role, content = msg.content });
                    AddMessage(msg.content, isUser: msg.role == "user");
                }
            }

            _tabHasMessages[tabId] = _hasMessages;
        }
        else
        {
            // Inactive tab — build into a temporary container, save to tab state
            var prevContainer = _chatContainer.Children().ToList();
            var prevHistory   = new List<HistoryMessage>(_history);
            var prevHas       = _hasMessages;
            var prevTab       = _activeTab;

            _activeTab   = tabId;
            _history.Clear();
            _hasMessages = false;
            _chatContainer.Clear();

            foreach (var msg in tabHistory.messages)
            {
                if (msg.role == "action")
                    AddInfoMessage(msg.content);
                else
                {
                    _history.Add(new HistoryMessage { role = msg.role, content = msg.content });
                    AddMessage(msg.content, isUser: msg.role == "user");
                }
            }

            _tabMessages[tabId]    = _chatContainer.Children().ToList();
            _tabHistories[tabId]   = new List<HistoryMessage>(_history);
            _tabHasMessages[tabId] = _hasMessages;

            // Restore active tab state
            _activeTab   = prevTab;
            _history.Clear();
            _history.AddRange(prevHistory);
            _hasMessages = prevHas;
            _chatContainer.Clear();
            foreach (var el in prevContainer) _chatContainer.Add(el);
        }
    }

    // ── Thinking animation ─────────────────────────────────────────────────────

    private void StartThinking()
    {
        _continueBtn?.AddToClassList("continue-btn--hidden");
        _dotFrame = 0;
        _thinkingIndicator.AddToClassList("thinking-indicator--visible");
        _thinkingAnimation = _thinkingDots.schedule
            .Execute(() => _thinkingDots.text = DotFrames[_dotFrame++ % 3])
            .Every(450);

        if (_loadingBar != null)
        {
            _loadingBar.RemoveFromClassList("loading-bar--hidden");
            if (_inputField != null) _inputField.style.display = DisplayStyle.None;
            if (_sendBtn != null)    _sendBtn.style.display    = DisplayStyle.None;
            _loadingStep = 0;
            _loadingAnimation = _loadingFill?.schedule
                .Execute(() =>
                {
                    _loadingStep = (_loadingStep + 1) % 40;
                    if (_loadingFill != null)
                        _loadingFill.style.width = Length.Percent(_loadingStep * 2.5f);
                })
                .Every(50);
        }
    }

    private void StopThinking()
    {
        _thinkingAnimation?.Pause();
        _thinkingAnimation = null;
        _thinkingIndicator.RemoveFromClassList("thinking-indicator--visible");

        _loadingAnimation?.Pause();
        _loadingAnimation = null;
        if (_loadingBar != null) _loadingBar.AddToClassList("loading-bar--hidden");
        if (_inputField != null) _inputField.style.display = DisplayStyle.Flex;
        if (_sendBtn != null)    _sendBtn.style.display    = DisplayStyle.Flex;
        UpdateContinueBtn();
    }

    // ── @ mentions ─────────────────────────────────────────────────────────────

    /// <summary>Finds @ScriptName mentions, loads file content, appends to context.</summary>
    private static string ResolveAtMentions(string message, ref string context)
    {
        var found = new System.Collections.Generic.HashSet<string>();
        var extra  = new StringBuilder();

        message = Regex.Replace(message, @"@(\w+)", m =>
        {
            var name  = m.Groups[1].Value;
            if (found.Contains(name)) return m.Value;
            var guids = AssetDatabase.FindAssets($"{name} t:Script");
            foreach (var guid in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(p) != name) continue;
                extra.AppendLine($"\n// @{name}  ←  {p}");
                extra.AppendLine(System.IO.File.ReadAllText(p));
                found.Add(name);
                return $"@{name}";
            }
            return m.Value; // mention not resolved
        });

        if (extra.Length > 0)
            context += $"\n\n=== ATTACHED SCRIPTS ===\n{extra}";

        return message;
    }

    // ── Diff ───────────────────────────────────────────────────────────────────

    private static string BuildDiffBlock(string oldText, string newText)
    {
        const int MaxLines = 30;
        var oldLines = oldText.Split('\n');
        var newLines = newText.Split('\n');
        var sb = new StringBuilder();
        int shown = 0;

        int maxLen = Mathf.Max(oldLines.Length, newLines.Length);
        for (int i = 0; i < maxLen && shown < MaxLines; i++)
        {
            var o = i < oldLines.Length ? oldLines[i].TrimEnd() : null;
            var n = i < newLines.Length ? newLines[i].TrimEnd() : null;

            if (o == n) continue; // unchanged line

            if (o != null)
            {
                sb.AppendLine($"<color=#F44336>- {EscapeRichText(o)}</color>");
                shown++;
            }
            if (n != null)
            {
                sb.AppendLine($"<color=#66BB6A>+ {EscapeRichText(n)}</color>");
                shown++;
            }
        }

        int totalChanged = 0;
        for (int i = 0; i < maxLen; i++)
        {
            var o = i < oldLines.Length ? oldLines[i].TrimEnd() : null;
            var n = i < newLines.Length ? newLines[i].TrimEnd() : null;
            if (o != n) totalChanged++;
        }

        if (shown < totalChanged)
            sb.AppendLine($"<color=#9E9E9E>  ... {totalChanged - shown} more changes</color>");

        return sb.ToString().TrimEnd();
    }

    private static string EscapeRichText(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ── Markdown ───────────────────────────────────────────────────────────────

    private static string ParseMarkdown(string text)
    {
        // Convert markdown tables to simple dashed lines (UI Toolkit can't render tables)
        text = Regex.Replace(text, @"^\|.+\|$", m =>
        {
            var line = m.Value.Trim('|').Trim();
            if (Regex.IsMatch(line, @"^[-| ]+$")) return "──────────────";
            var cells = line.Split('|').Select(c => c.Trim()).Where(c => c.Length > 0);
            return "• " + string.Join("  |  ", cells);
        }, RegexOptions.Multiline);

        text = Regex.Replace(text,
            @"```(?:\w*)\n?([\s\S]*?)```",
            "\n<color=#A8FF78>$1</color>\n",
            RegexOptions.Multiline);
        text = Regex.Replace(text, @"^### (.+)$", "<b>$1</b>",                 RegexOptions.Multiline);
        text = Regex.Replace(text, @"^## (.+)$",  "<size=14><b>$1</b></size>", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^# (.+)$",   "<size=15><b>$1</b></size>", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "<b>$1</b>");
        text = Regex.Replace(text, @"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", "<i>$1</i>");
        text = Regex.Replace(text, @"`(.+?)`", "<color=#A8FF78>$1</color>");
        return text;
    }

    // ── Action preview & execution ─────────────────────────────────────────────

    private static ClaunityActionResponse TryParseActionResponse(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("\"actions\"")) return null;

        // Claude sometimes writes explanation text before the JSON — find the JSON block
        var jsonStart = text.IndexOf("{\"message\"");
        if (jsonStart < 0) jsonStart = text.IndexOf("{ \"message\"");
        if (jsonStart < 0) jsonStart = text.TrimStart().StartsWith("{") ? 0 : -1;
        if (jsonStart < 0) return null;

        var json = text.Substring(jsonStart);

        // Extract the JSON object (find the matching closing brace)
        int depth = 0, end = -1;
        for (int i = 0; i < json.Length; i++)
        {
            if (json[i] == '{') depth++;
            else if (json[i] == '}') { depth--; if (depth == 0) { end = i; break; } }
        }
        if (end >= 0) json = json.Substring(0, end + 1);

        try
        {
            var resp = JsonUtility.FromJson<ClaunityActionResponse>(json);
            if (resp?.actions == null || resp.actions.Length == 0) return null;

            // Warn about unknown action types
            foreach (var action in resp.actions)
                if (!string.IsNullOrEmpty(action.type) && !ClaunityActionExecutor.IsKnownAction(action.type))
                    Debug.LogWarning($"[Claunity] Unknown action type: '{action.type}' — will be skipped");

            return resp;
        }
        catch (Exception e)
        {
            Debug.LogError($"[Claunity] Action parse error: {e.Message}");
            return null;
        }
    }

    private static string DescribeAction(ActionPayload a)
    {
        var target = a.name ?? a.path ?? "?";
        switch (a.type)
        {
            case "create_gameobject":  return $"Create GameObject '{a.name}'";
            case "delete_gameobject":  return $"Delete '{target}'";
            case "rename_gameobject":  return $"Rename '{target}' → '{a.newName}'";
            case "move_gameobject":    return $"Move '{target}' to ({a.position?.x}, {a.position?.y}, {a.position?.z})";
            case "rotate_gameobject":  return $"Rotate '{target}'";
            case "scale_gameobject":   return $"Scale '{target}'";
            case "add_component":            return $"Add {a.componentType} to '{target}'";
            case "remove_component":         return $"Remove {a.componentType} from '{target}'";
            case "set_component_property":   return $"Set {a.componentType}.{a.propertyName} = {a.content} on '{target}'";
            case "set_active":         return $"{(a.active ? "Activate" : "Deactivate")} '{target}'";
            case "reparent":           return $"Reparent '{target}' → '{a.parent ?? "root"}'";
            case "duplicate_gameobject": return $"Duplicate '{target}'" + (string.IsNullOrEmpty(a.newName) ? "" : $" as '{a.newName}'");
            case "update_gameobject":  return $"Update '{target}'" + (string.IsNullOrEmpty(a.tag) ? "" : $" tag→{a.tag}") + (string.IsNullOrEmpty(a.layerName) ? "" : $" layer→{a.layerName}");
            case "create_material":    return $"Create material '{a.name}'" + (string.IsNullOrEmpty(a.savePath) ? "" : $" at {a.savePath}");
            case "assign_material":    return $"Assign '{a.materialPath}' to '{target}'";
            case "modify_material":    return $"Modify material '{a.materialPath}'";
            case "save_scene":         return "Save current scene";
            case "create_scene":       return $"Create scene '{a.sceneName}'";
            case "load_scene":         return $"Load scene '{a.sceneName ?? a.assetPath}'";
            case "create_prefab":      return $"Create prefab from '{target}'";
            case "add_asset_to_scene": return $"Add asset '{a.assetPath}' to scene";
            case "add_primitive":      return $"Create {a.name ?? "Cube"}" + (string.IsNullOrEmpty(a.newName) ? "" : $" '{a.newName}'") + (string.IsNullOrEmpty(a.parent) ? "" : $" → parent '{a.parent}'");
            case "enter_play_mode":    return "Enter Play Mode";
            case "exit_play_mode":     return "Exit Play Mode";
            case "pause_play_mode":    return "Pause / Resume Play Mode";
            case "execute_menu_item":  return $"Execute menu: '{a.menuPath}'";
            case "get_scene_info":          return "Read scene hierarchy";
            case "get_gameobject":          return $"Read info about '{target}'";
            case "get_component_property":  return $"Read {a.componentType}.{a.propertyName ?? "properties"} on '{target}'";
            case "take_screenshot":    return $"Look at {(a.view == "game" ? "Game View" : a.view == "camera" ? "Camera render" : "Scene View")}";
            case "read_script":        return $"Read script '{a.scriptName ?? a.scriptPath}'";
            case "create_script":      return $"Create script '{a.scriptName}'" + (string.IsNullOrEmpty(a.savePath) ? "" : $" at {a.savePath}");
            case "edit_script":        return $"Edit script '{a.scriptName ?? System.IO.Path.GetFileName(a.scriptPath ?? "?")}'";
            case "attach_script":      return $"Attach '{a.scriptName}' to '{target}'";
            case "recompile_scripts":  return "Recompile all scripts";
            default:                   return a.type;
        }
    }

    private void ShowActionPreview(ClaunityActionResponse response)
    {
        if (!_hasMessages)
        {
            _hasMessages = true;
            if (_tabHasMessages.ContainsKey(_activeTab)) _tabHasMessages[_activeTab] = true;
            HideSplash();
            rootVisualElement.Q("chat-toolbar").RemoveFromClassList("chat-toolbar--hidden");
        }

        var row = new VisualElement();
        row.AddToClassList("message-row");
        row.Add(MakeAvatar());

        var card = new VisualElement();
        card.AddToClassList("action-preview-card");

        if (!string.IsNullOrEmpty(response.message))
        {
            var msgLabel = new Label(ParseMarkdown(response.message));
            msgLabel.AddToClassList("bubble-text");
            msgLabel.enableRichText = true;
            card.Add(msgLabel);
        }

        var divider = new VisualElement();
        divider.AddToClassList("action-divider");
        card.Add(divider);

        var header = new Label($"⚡ {response.actions.Length} action{(response.actions.Length != 1 ? "s" : "")} planned:");
        header.AddToClassList("action-preview-header");
        card.Add(header);

        foreach (var action in response.actions)
        {
            var item = new Label($"• {DescribeAction(action)}");
            item.AddToClassList("action-item");
            card.Add(item);

            // Diff preview for edit_script
            if (action.type == "edit_script" && !string.IsNullOrEmpty(action.content))
            {
                var oldContent = ClaunityActionExecutor.ReadScriptForDiff(action);
                if (oldContent != null)
                {
                    var diffBlock = BuildDiffBlock(oldContent, action.content);
                    if (!string.IsNullOrEmpty(diffBlock))
                    {
                        var diffLabel = new Label(diffBlock);
                        diffLabel.AddToClassList("script-diff");
                        diffLabel.enableRichText = true;
                        card.Add(diffLabel);
                    }
                }
            }
        }

        row.Add(card);
        _chatContainer.Add(row);
        ScrollToBottom();

        Action<bool, string> onActionsComplete = null;
        if (_requestTab == "chat")
        {
            onActionsComplete = (anyError, results) =>
            {
                _chatActionsCount++;
                _chatAutoLooping = true;
                _waitingForResponse = true;
                UpdateSendButton();
                StartThinking();
                string doneMsg;
                if (anyError)
                    doneMsg = $"Some actions failed in Unity. Results:\n{results}\n\nDo NOT claim success for failed actions. Inform the user about the errors honestly. If the failure is expected (e.g. object has no Renderer), explain why and ask if they want you to fix it (e.g. add a MeshRenderer first).";
                else
                    doneMsg = "The actions above were executed successfully in Unity. If the original request is now fully complete, respond with a short plain-text summary of what was done. If there are still more steps needed to fulfill the original request, continue with the next step (JSON only, no mixing).";
                EditorCoroutineUtility.StartCoroutineOwnerless(PostChat(doneMsg, isContinuation: false));
            };
        }

        var btnRow = new VisualElement();
        btnRow.AddToClassList("action-btn-row");

        var applyBtn  = new Button { text = "✓  Apply"  };
        applyBtn.AddToClassList("action-apply-btn");

        var cancelBtn = new Button { text = "✗  Cancel" };
        cancelBtn.AddToClassList("action-cancel-btn");

        applyBtn.clicked += () =>
        {
            applyBtn.style.display  = DisplayStyle.None;
            cancelBtn.style.display = DisplayStyle.None;
            ExecuteActions(response.actions, card, onActionsComplete);
        };

        cancelBtn.clicked += () =>
        {
            applyBtn.style.display  = DisplayStyle.None;
            cancelBtn.style.display = DisplayStyle.None;
            var lbl = new Label("Cancelled");
            lbl.AddToClassList("action-cancelled");
            card.Add(lbl);
            _waitingForResponse = false;
            _chatAutoLooping = false;
            _chatActionsCount = 0;
            UpdateSendButton();
        };

        btnRow.Add(applyBtn);
        btnRow.Add(cancelBtn);
        card.Add(btnRow);
    }

    private void ExecuteActions(ActionPayload[] actions, VisualElement card, Action<bool, string> onComplete = null)
    {
        _waitingForResponse = true;
        UpdateSendButton();

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Claunity: Apply scene actions");

        var resultsContainer = new VisualElement();
        resultsContainer.AddToClassList("action-results");

        bool anyError = false;
        var resultLines = new System.Text.StringBuilder();
        for (int i = 0; i < actions.Length; i++)
        {
            var action = actions[i];
            var result = ClaunityActionExecutor.Execute(action);
            var lbl    = new Label(result);
            lbl.AddToClassList(result.StartsWith("✓") ? "action-result-ok" : "action-result-err");
            resultsContainer.Add(lbl);
            resultLines.AppendLine($"- {action.type}: {result}");
            if (!result.StartsWith("✓")) anyError = true;

            // After recompile, save remaining actions and always flag autoResume
            if (action.type == "recompile_scripts")
            {
                var remaining = i < actions.Length - 1 ? actions.Skip(i + 1).ToArray() : new ActionPayload[0];
                ClaunityActionExecutor.SavePendingActions(remaining, autoResume: true);
                var pendingLbl = new Label("⏳ Compiling… will resume after reload");
                pendingLbl.AddToClassList("action-status-warning");
                resultsContainer.Add(pendingLbl);
                break;
            }
        }

        Undo.CollapseUndoOperations(undoGroup);

        card.Add(resultsContainer);

        var status = new Label(anyError ? "⚠ Applied with errors" : "✓ Applied successfully");
        status.AddToClassList(anyError ? "action-status-warning" : "action-status-ok");
        card.Add(status);

        ScrollToBottom();

        _waitingForResponse = false;
        UpdateSendButton();

        PersistActionSummary(actions, anyError);
        onComplete?.Invoke(anyError, resultLines.ToString());
    }

    private void PersistActionSummary(ActionPayload[] actions, bool anyError)
    {
        if (_rebuilding) return;
        if (_activeTab != "chat" && _activeTab != "project") return;

        var icon = anyError ? "⚠" : "✓";
        var names = string.Join(", ", actions
            .Where(a => a.type != "recompile_scripts")
            .Select(a => a.type.Replace("_", " "))
            .Distinct());
        var text = $"{icon} Actions: {names}";

        // Store in history so it gets saved and rebuilt
        _history.Add(new HistoryMessage { role = "action", content = text });
        SaveChatHistory();
    }

    private void ExecutePendingPostReloadActions(ActionPayload[] actions, bool autoResume = false)
    {
        var row = new VisualElement();
        row.AddToClassList("message-row");
        row.Add(MakeAvatar());

        var card = new VisualElement();
        card.AddToClassList("action-preview-card");

        var header = new Label("⚡ Compilation done — running pending actions:");
        header.AddToClassList("action-preview-header");
        card.Add(header);

        row.Add(card);
        _chatContainer.Add(row);

        ExecuteActions(actions, card, onComplete: autoResume ? (Action<bool, string>)((_, _) => OnPostReloadComplete()) : null);
    }

    private void OnPostReloadComplete()
    {
        // Auto-continue the task that was interrupted by recompile
        var msg = "Recompilation complete. Please continue with the next step of the plan in the same language as the previous conversation.";
        _requestTab = _activeTab; // prevent SwitchTab("") bug after reload
        _history.Add(new HistoryMessage { role = "user", content = msg });
        _waitingForResponse = true;
        UpdateSendButton();
        StartThinking();
        EditorCoroutineUtility.StartCoroutineOwnerless(PostChat(msg, isContinuation: true));
    }

    // ── Models fetch ───────────────────────────────────────────────────────────

    private IEnumerator FetchModels()
    {
        using var req = UnityWebRequest.Get($"{ServerUrl}/models");
        req.timeout = 10;
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success) yield break;

        var payload = JsonUtility.FromJson<ModelsPayload>(req.downloadHandler.text);
        if (payload == null) yield break;

        if (rootVisualElement == null || _dynamicModels == null) yield break;

        var modelDropdown   = rootVisualElement.Q<DropdownField>("model-dropdown");
        var currentProvider = Providers[0];
        var currentModel    = modelDropdown?.value ?? "";

        if (payload.claude != null && payload.claude.Length > 0) _dynamicModels["Claude (Anthropic)"] = payload.claude;

        // Refresh model dropdown preserving current selection if still valid
        UpdateModelDropdown(modelDropdown, currentProvider, currentModel);
    }

    // ── HTTP ───────────────────────────────────────────────────────────────────

    // ── Agentic tool name → friendly status text ──────────────────────────────
    private static string GetToolStatusText(string toolName) => toolName switch
    {
        "get_scene_info"         => "Reading scene...",
        "get_gameobject"         => "Inspecting object...",
        "read_script"            => "Reading script...",
        "get_component_property" => "Reading property...",
        "get_serialized_property"=> "Reading property...",
        "get_animator_info"      => "Reading animator...",
        "get_input_asset_info"   => "Reading input asset...",
        "create_gameobject"      => "Creating object...",
        "add_primitive"          => "Adding primitive...",
        "delete_gameobject"      => "Deleting object...",
        "create_script"          => "Writing script...",
        "edit_script"            => "Editing script...",
        "attach_script"          => "Attaching script...",
        "recompile_scripts"      => "Compiling scripts...",
        "create_material"        => "Creating material...",
        "assign_material"        => "Assigning material...",
        "add_component"          => "Adding component...",
        "set_component_property" => "Setting property...",
        "create_scene"           => "Creating scene...",
        "save_scene"             => "Saving scene...",
        "create_prefab"          => "Creating prefab...",
        "create_animator_controller" => "Creating animator...",
        "bake_navmesh"           => "Baking NavMesh...",
        "add_package"            => "Adding package...",
        "take_screenshot"        => "Capturing screenshot...",
        "build_player"           => "Building player...",
        _                        => "Working...",
    };

    // ── Action Chip (green status message per tool call) ──────────────────

    private void AddActionChip(string toolName, string toolInputJson)
    {
        var text = GetToolChipText(toolName, toolInputJson ?? "{}");
        var chip = new Label(text);
        chip.AddToClassList("action-chip");
        _chatContainer.Add(chip);
        ScrollToBottom();
    }

    private static string GetToolChipText(string toolName, string json)
    {
        string t = ExtractChipTarget(json, "scriptName", "name", "sceneName", "assetPath", "materialPath", "elementType");
        return toolName switch
        {
            "get_scene_info"          => "📖 Read scene",
            "get_gameobject"          => $"📖 Read {t ?? "object"}",
            "read_script"             => $"📖 Read {t ?? "script"}",
            "get_component_property"  => $"📖 Read property on {t ?? "object"}",
            "get_serialized_property" => $"📖 Read property on {t ?? "object"}",
            "get_animator_info"       => "📖 Read animator",
            "get_input_asset_info"    => "📖 Read input asset",
            "create_gameobject"       => $"✨ Created {t ?? "object"}",
            "add_primitive"           => $"✨ Added {t ?? "primitive"}",
            "delete_gameobject"       => $"🗑 Deleted {t ?? "object"}",
            "rename_gameobject"       => $"✏️ Renamed {t ?? "object"}",
            "duplicate_gameobject"    => $"✨ Duplicated {t ?? "object"}",
            "reparent"                => $"📂 Reparented {t ?? "object"}",
            "move_gameobject"         => $"📍 Moved {t ?? "object"}",
            "rotate_gameobject"       => $"🔄 Rotated {t ?? "object"}",
            "scale_gameobject"        => $"⬛ Scaled {t ?? "object"}",
            "add_component"           => $"➕ Added component to {t ?? "object"}",
            "remove_component"        => $"➖ Removed component from {t ?? "object"}",
            "set_component_property"  => $"⚙️ Set property on {t ?? "object"}",
            "create_script"           => $"📝 Created {t ?? "script"}",
            "edit_script"             => $"✏️ Edited {t ?? "script"}",
            "attach_script"           => $"🔗 Attached script to {t ?? "object"}",
            "recompile_scripts"       => "🔨 Compiled scripts",
            "create_material"         => $"🎨 Created material {t ?? ""}".TrimEnd(),
            "assign_material"         => $"🎨 Assigned material to {t ?? "object"}",
            "modify_material"         => $"🎨 Modified {t ?? "material"}",
            "save_scene"              => "💾 Saved scene",
            "create_scene"            => $"🗂 Created scene {t ?? ""}".TrimEnd(),
            "load_scene"              => $"🗂 Loaded {t ?? "scene"}",
            "create_prefab"           => $"📦 Created prefab {t ?? ""}".TrimEnd(),
            "add_asset_to_scene"      => $"✨ Added {t ?? "asset"} to scene",
            "create_ui_element"       => $"🖼 Created {t ?? "UI element"}",
            "bake_navmesh"            => "🗺 Baked NavMesh",
            "take_screenshot"         => "📷 Captured screenshot",
            "build_player"            => "📦 Built player",
            _                         => $"⚡ {GetToolStatusText(toolName).TrimEnd('.')}",
        };
    }

    private static string ExtractChipTarget(string json, params string[] keys)
    {
        foreach (var key in keys)
        {
            var search = "\"" + key + "\"";
            int ki = json.IndexOf(search, StringComparison.Ordinal);
            if (ki < 0) continue;
            int ci = json.IndexOf(':', ki + search.Length);
            if (ci < 0) continue;
            int qi = json.IndexOf('"', ci + 1);
            if (qi < 0) continue;
            int qe = json.IndexOf('"', qi + 1);
            if (qe < 0) continue;
            var val = json.Substring(qi + 1, qe - qi - 1);
            if (!string.IsNullOrEmpty(val)) return val;
        }
        return null;
    }

    // ── Build ActionPayload from tool_use response ─────────────────────────
    private static ActionPayload BuildActionFromTool(string toolName, string toolInputJson)
    {
        // Inject "type" into the tool input JSON object so JsonUtility can parse it as ActionPayload
        if (string.IsNullOrEmpty(toolInputJson) || toolInputJson == "{}")
            toolInputJson = "{}";

        // Insert type field at the start of the JSON object
        var withType = "{\"type\":\"" + toolName + "\"," + toolInputJson.Substring(1);
        try
        {
            var payload = JsonUtility.FromJson<ActionPayload>(withType);
            ClaunityActionExecutor.ClearAbsentOptionalVectors(payload, withType);
            return payload;
        }
        catch
        {
            return new ActionPayload { type = toolName };
        }
    }

    // ── Main chat coroutine (agentic loop) ─────────────────────────────────
    private IEnumerator PostChat(string message, bool isContinuation = false,
                                 string context = null, string image = null)
    {
        if (!isContinuation && !CheckClientRateLimit()) yield break;

        // Check backend is reachable before sending
        using (var healthReq = UnityWebRequest.Get($"{ServerUrl}/health"))
        {
            healthReq.timeout = 5;
            yield return healthReq.SendWebRequest();
            if (healthReq.result != UnityWebRequest.Result.Success)
            {
                _waitingForResponse = false;
                UpdateSendButton();
                StopThinking();
                bool launch = EditorUtility.DisplayDialog(
                    "Claunity App not running",
                    "The Claunity backend is not running.\nLaunch it now?",
                    "Launch", "Cancel");
                if (launch)
                {
                    EditorPrefs.SetBool(StartedPref, false);
                    _mainUI.AddToClassList("main-ui--hidden");
                    _welcomeScreen.style.display = DisplayStyle.Flex;
                    yield return EditorCoroutineUtility.StartCoroutineOwnerless(LaunchAndConnect());
                }
                yield break;
            }
        }

        if (!isContinuation)
        {
            _history.Add(new HistoryMessage { role = "user", content = message });
            SaveChatHistory(); // persist user message immediately — survives domain reload
            _requestTab = _activeTab;
        }

        // Build project_files: use explicit context if provided (test mode, retry), else project index
        var projectFiles = context ?? _projectContext ?? "";
        var errorContext = ClaunityConsole.FormatForContext();
        if (!string.IsNullOrEmpty(errorContext) && context == null)
            projectFiles += "\n\n" + errorContext;

        // Build initial payload
        int maxHistory = System.Math.Max(1, EditorPrefs.GetInt(HistoryLimitPref, 4));
        var payload = new ChatRequest
        {
            message        = message,
            project_files  = projectFiles,
            project_path   = System.IO.Path.GetDirectoryName(Application.dataPath),
            image          = image ?? "",
            mode           = _activeTab,
            model_override = _chatModelOverride,
            history        = _history
                .Take(_history.Count - 1)
                .Where(h => h.role == "user" || h.role == "assistant")
                .TakeLast(maxHistory)
                .ToArray(),
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
            yield return HandleChatError(req, message, projectFiles, image);
            yield break;
        }

        var response = JsonUtility.FromJson<ChatResponse>(req.downloadHandler.text);
        if (response == null)
        {
            AddMessage("Error: Invalid response from server.", false);
            _waitingForResponse = false;
            UpdateSendButton();
            StopThinking();
            yield break;
        }

        TrackTokenUsage(response.input_tokens, response.output_tokens);

        // ── Claude Code streaming path ──────────────────────────────────────
        if (response.type == "streaming")
        {
            // Persist session so we can resume polling after a domain reload
            EditorPrefs.SetString("Claunity_StreamResume", response.session_id);
            EditorPrefs.SetString(StreamContextPref, "chat");
            yield return PollStreamingResponse(response.session_id);
            EditorPrefs.DeleteKey("Claunity_StreamResume");
            EditorPrefs.DeleteKey(StreamContextPref);
            yield break;
        }

        // ── Agentic loop: keep executing tools until we get a final answer ──
        int safetyLimit = 40; // max tool calls per turn
        while (response.type == "tool_request" && safetyLimit-- > 0)
        {
            // Show Claude's narration (text before tool call) as a chat message
            if (!string.IsNullOrEmpty(response.narration))
            {
                StopThinking();
                AddMessage(response.narration, isUser: false);
                StartThinking();
            }

            // Update loading text with what Claude is doing
            if (_loadingText != null)
                _loadingText.text = GetToolStatusText(response.tool_name);
            if (_requestTab == "test" && _testLoadingText != null)
                _testLoadingText.text = GetToolStatusText(response.tool_name);

            // Execute the tool in Unity
            var action     = BuildActionFromTool(response.tool_name, response.tool_input_json ?? "{}");
            var toolResult = ClaunityActionExecutor.Execute(action);
            AddActionChip(response.tool_name, response.tool_input_json);

            // Handle screenshot result (base64 image)
            string toolResultStr;
            if (toolResult != null && toolResult.Length > 8 &&
                toolResult.StartsWith("[IMAGE:") && toolResult.EndsWith("]"))
            {
                toolResultStr = "[Screenshot captured successfully]";
                // Note: we don't feed back the full base64 — Claude already knows it took a screenshot
            }
            else
            {
                toolResultStr = toolResult ?? "✓ done";
            }

            // Send result back to Python
            var continuePayload = new ChatContinueRequest
            {
                session_id  = response.session_id,
                tool_use_id = response.tool_use_id,
                tool_result = toolResultStr,
            };
            var continueBody = JsonUtility.ToJson(continuePayload);

            using var continueReq = new UnityWebRequest($"{ServerUrl}/chat/continue", "POST");
            continueReq.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(continueBody));
            continueReq.downloadHandler = new DownloadHandlerBuffer();
            continueReq.SetRequestHeader("Content-Type", "application/json");
            continueReq.timeout = 120;

            yield return continueReq.SendWebRequest();

            if (continueReq.result != UnityWebRequest.Result.Success)
            {
                yield return HandleChatError(continueReq, message, projectFiles, image);
                yield break;
            }

            var next = JsonUtility.FromJson<ChatResponse>(continueReq.downloadHandler.text);
            if (next == null) break;

            TrackTokenUsage(next.input_tokens, next.output_tokens);
            response = next;
        }

        StopThinking();

        if (_activeTab != _requestTab)
            SwitchTab(_requestTab);

        // ── Handle final text response ──────────────────────────────────────
        if (response.type == "final" || string.IsNullOrEmpty(response.type))
        {
            var reply = response.reply ?? "";

            if (_requestTab == "test")
            {
                _history.Add(new HistoryMessage { role = "assistant", content = reply });
                BuildTestDashboard(reply);
            }
            else if (string.IsNullOrEmpty(reply))
            {
                // Empty response — don't add a blank bubble
            }
            else if (_chatAutoLooping && _requestTab == "chat")
            {
                _history.Add(new HistoryMessage { role = "assistant", content = reply });
                _chatAutoLooping = false;
                AddSummaryCard(reply, _chatActionsCount);
                _chatActionsCount = 0;
            }
            else
            {
                _history.Add(new HistoryMessage { role = "assistant", content = reply });
                AddMessage(reply, isUser: false);
            }

            // Auto-continue if cut off
            if (response.stop_reason == "max_tokens" && _requestTab != "test")
            {
                AddSystemNote("Response was cut off — continuing automatically...");
                var cont = "Continue.";
                _history.Add(new HistoryMessage { role = "user", content = cont });
                StartThinking();
                EditorCoroutineUtility.StartCoroutineOwnerless(PostChat(cont, isContinuation: true));
                yield break;
            }
        }

        _waitingForResponse = false;
        UpdateSendButton();
    }

    // ── Shared HTTP error handler ──────────────────────────────────────────
    private System.Collections.IEnumerator HandleChatError(
        UnityWebRequest req, string message, string context, string image)
    {
        StopThinking();

        if (_history.Count > 0) _history.RemoveAt(_history.Count - 1);

        string errorMsg;
        bool   retryable = false;

        if (req.result == UnityWebRequest.Result.ConnectionError ||
            req.result == UnityWebRequest.Result.DataProcessingError)
        {
            errorMsg  = "Cannot connect to Desktop App.\nMake sure it is running — start it in Settings.";
            retryable = true;
        }
        else
        {
            string errorCode = "";
            try
            {
                var errWrapper = JsonUtility.FromJson<ErrorResponse>(req.downloadHandler.text);
                errorMsg  = !string.IsNullOrEmpty(errWrapper?.detail) ? errWrapper.detail : req.error;
                errorCode = errWrapper?.error_code ?? "";
            }
            catch { errorMsg = req.error; }

            retryable = errorCode is "rate_limit" or "overloaded" or "provider_error"
                                  or "timeout"    or "connection";

            // For auth errors, hint user to check Settings
            if (errorCode is "invalid_key" or "no_key" or "forbidden" or "quota")
                errorMsg += "\n\nOpen the Settings tab to update your API key.";
        }

        ShowErrorCard(errorMsg, retryable, message, context, image);

        _waitingForResponse = false;
        UpdateSendButton();
        yield break;
    }

    private IEnumerator PollStreamingResponse(string sessionId)
    {
        if (_activeTab != _requestTab)
            SwitchTab(_requestTab);

        // Keep session resumable if another domain reload happens during polling
        EditorPrefs.SetString("Claunity_StreamResume", sessionId);
        EditorPrefs.SetString(StreamContextPref, _requestTab ?? "chat");

        int failCount = 0;
        double startTime = EditorApplication.timeSinceStartup;
        const double maxPollSec = 1200.0; // 20-minute hard cap

        while (true)
        {
            yield return new EditorWaitForSeconds(0.5f);

            // Hard timeout — kill backend subprocess and show retry button
            if (EditorApplication.timeSinceStartup - startTime > maxPollSec)
            {
                StopThinking();
                EditorCoroutineUtility.StartCoroutineOwnerless(CancelStreamSession(sessionId));
                AddTimeoutMessage();
                _waitingForResponse = false;
                UpdateSendButton();
                EditorPrefs.DeleteKey("Claunity_StreamResume");
                EditorPrefs.DeleteKey(StreamContextPref);
                yield break;
            }

            using var req = UnityWebRequest.Get($"{ServerUrl}/chat/stream/{sessionId}");
            req.timeout = 10;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                failCount++;
                if (failCount >= 20) // 20 × 0.5s = 10 seconds of backend unreachable → abort
                {
                    StopThinking();
                    _waitingForResponse = false;
                    UpdateSendButton();
                    EditorPrefs.DeleteKey("Claunity_StreamResume");
                    EditorPrefs.DeleteKey(StreamContextPref);
                    yield break;
                }
                continue;
            }
            failCount = 0;

            var poll = JsonUtility.FromJson<StreamPollResponse>(req.downloadHandler.text);
            if (poll?.events != null)
            {
                foreach (var evt in poll.events)
                {
                    if (evt == null) continue;
                    if (evt.type == "narration" && !string.IsNullOrEmpty(evt.text))
                    {
                        StopThinking();
                        // Add to history so it survives domain reload
                        _history.Add(new HistoryMessage { role = "assistant", content = evt.text });
                        SaveChatHistory();
                        if (_requestTab == "test")
                        {
                            BuildTestDashboard(evt.text);
                            _waitingForResponse = false;
                            UpdateSendButton();
                            EditorPrefs.DeleteKey("Claunity_StreamResume");
                            EditorPrefs.DeleteKey(StreamContextPref);
                            yield break;
                        }
                        else
                        {
                            AddMessage(evt.text, isUser: false);
                            StartThinking();
                        }
                    }
                    else if (evt.type == "tool" && !string.IsNullOrEmpty(evt.text))
                    {
                        AddSystemNote(evt.text);
                        if (_loadingText != null)
                            _loadingText.text = evt.text;
                    }
                    else if (evt.type == "final")
                    {
                        StopThinking();
                        var reply = evt.text ?? "";
                        _waitingForResponse = false;
                        UpdateSendButton();
                        if (!string.IsNullOrEmpty(reply) && !IsAlreadyLastReply(reply))
                        {
                            _history.Add(new HistoryMessage { role = "assistant", content = reply });
                            if (_requestTab == "test")
                                BuildTestDashboard(reply);
                            else
                                AddMessage(reply, isUser: false);
                        }
                        EditorPrefs.DeleteKey("Claunity_StreamResume");
                        EditorPrefs.DeleteKey(StreamContextPref);
                        yield break;
                    }
                    else if (evt.type == "error")
                    {
                        StopThinking();
                        var errText = evt.text ?? "Unknown error";
                        if (errText.Contains("invalid_api_key") || errText.Contains("authentication") || errText.Contains("401"))
                            errText += "\n\nOpen the Settings tab to update your API key.";
                        ShowErrorCard(errText, false, null, null, null);
                        _waitingForResponse = false;
                        UpdateSendButton();
                        EditorPrefs.DeleteKey("Claunity_StreamResume");
                        EditorPrefs.DeleteKey(StreamContextPref);
                        yield break;
                    }
                }
            }

            if (poll?.done == true)
            {
                StopThinking();
                var doneReply = poll.reply ?? "";
                if (!string.IsNullOrEmpty(doneReply) && !IsAlreadyLastReply(doneReply))
                {
                    _history.Add(new HistoryMessage { role = "assistant", content = doneReply });
                    if (_activeTab != _requestTab) SwitchTab(_requestTab);
                    if (_requestTab == "test")
                        BuildTestDashboard(doneReply);
                    else
                        AddMessage(doneReply, isUser: false);
                }
                _waitingForResponse = false;
                UpdateSendButton();
                EditorPrefs.DeleteKey("Claunity_StreamResume");
                EditorPrefs.DeleteKey(StreamContextPref);
                yield break;
            }
        }
    }

    /// Returns true if the reply is already the last assistant entry in history (prevents duplicates after domain reload).
    private bool IsAlreadyLastReply(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].role == "assistant")
                return _history[i].content == text;
            if (_history[i].role == "user")
                break; // user message after any assistant = new turn, not a duplicate
        }
        return false;
    }

    private void AddTimeoutMessage()
    {
        var label = new Label("Request timed out.");
        label.AddToClassList("bubble-text");

        var retryBtn = new Button();
        retryBtn.text = "↩ Again";
        retryBtn.AddToClassList("retry-btn");
        var captured = _lastSentMessage;
        retryBtn.clicked += () =>
        {
            if (string.IsNullOrEmpty(captured) || _waitingForResponse) return;
            _inputField.value = captured;
            OnSendClicked();
        };

        var bubble = MakeBubble(label, false);
        bubble.Add(retryBtn);
        var row = MakeRow(false);
        row.Add(MakeAvatar());
        row.Add(bubble);
        _chatContainer?.Add(row);
        _chatScroll?.schedule.Execute(() => _chatScroll.ScrollTo(row)).StartingIn(50);
    }

    private IEnumerator CancelStreamSession(string sessionId)
    {
        using var req = new UnityWebRequest($"{ServerUrl}/chat/stream/cancel/{sessionId}", "POST");
        req.uploadHandler   = new UploadHandlerRaw(new byte[0]);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.timeout = 5;
        yield return req.SendWebRequest();
    }

    private IEnumerator PostConfig(string apiKey, string model = "", bool useClaudeCode = false, string personalPrompt = "", int historyLimit = 6)
    {
        var payload = new ConfigRequest { api_key = apiKey, model = model, use_claude_code = useClaudeCode, personal_prompt = personalPrompt, history_limit = historyLimit };
        var body = JsonUtility.ToJson(payload);
        using var req = new UnityWebRequest($"{ServerUrl}/config", "POST");
        req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 10;
        yield return req.SendWebRequest();
    }

    private IEnumerator TestConnectionCoroutine()
    {
        _connectionStatus.text = "Checking...";
        _connectionStatus.style.color = new StyleColor(Color.gray);

        using var req = UnityWebRequest.Get($"{ServerUrl}/health");
        req.timeout = 5;
        yield return req.SendWebRequest();

        bool serverOk = req.result == UnityWebRequest.Result.Success;
        SetConnectionDot(serverOk);

        if (!serverOk)
        {
            _connectionStatus.text = "❌ Desktop App not running";
            _connectionStatus.style.color = new StyleColor(new Color(0.96f, 0.26f, 0.21f));
            yield break;
        }

        var aiSourceDropdown = rootVisualElement.Q<DropdownField>("ai-source-dropdown");
        bool useClaudeCode = aiSourceDropdown?.value == "Claude Code";

        if (useClaudeCode)
        {
            using var ccReq = UnityWebRequest.Get($"{ServerUrl}/check-claude-code");
            ccReq.timeout = 10;
            yield return ccReq.SendWebRequest();

            if (ccReq.result == UnityWebRequest.Result.Success)
            {
                var data = JsonUtility.FromJson<ClaudeCodeCheckResponse>(ccReq.downloadHandler.text);
                if (data != null && data.found)
                {
                    _connectionStatus.text = $"✅ Claude Code ready";
                    _connectionStatus.style.color = new StyleColor(new Color(0.30f, 0.76f, 0.31f));
                }
                else
                {
                    _connectionStatus.text = "❌ Claude Code not found — run: npm install -g @anthropic-ai/claude-code";
                    _connectionStatus.style.color = new StyleColor(new Color(0.96f, 0.26f, 0.21f));
                }
            }
            else
            {
                _connectionStatus.text = "❌ Could not check Claude Code";
                _connectionStatus.style.color = new StyleColor(new Color(0.96f, 0.26f, 0.21f));
            }
        }
        else
        {
            _connectionStatus.text = "✅ Connected";
            _connectionStatus.style.color = new StyleColor(new Color(0.30f, 0.76f, 0.31f));
        }
    }

    private IEnumerator CheckConnectionOnStart()
    {
        using var req = UnityWebRequest.Get($"{ServerUrl}/health");
        req.timeout = 5;
        yield return req.SendWebRequest();
        SetConnectionDot(req.result == UnityWebRequest.Result.Success);
    }

    private void SetConnectionDot(bool connected)
    {
        _connectionDot.EnableInClassList("connection-dot--connected",    connected);
        _connectionDot.EnableInClassList("connection-dot--disconnected", !connected);
    }
}

}