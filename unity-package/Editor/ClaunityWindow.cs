using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Unity.EditorCoroutines.Editor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace Claunity
{

public partial class ClaunityWindow : EditorWindow
{
    private const string ServerUrl    = "http://127.0.0.1:8765";
    private const string ApiKeyPref   = "Claunity_ApiKey";
    private const string ModelPref    = "Claunity_Model";
    private const string StartedPref      = "Claunity_Started";
    private const string SessionInitKey  = "Claunity_SessionInit";

    private const string UseClaudeCodePref   = "Claunity_UseClaudeCode";
    private const string HistoryLimitPref    = "Claunity_HistoryLimit";
    private const string PersonalPromptPref  = "Claunity_PersonalPrompt";
    private const string ChatModelPref          = "Claunity_ChatModel";

    private static readonly string[] ChatModels     = { "claude-sonnet-4-6", "claude-haiku-4-5-20251001", "claude-opus-4-8" };
    private static readonly string[] ChatModelLabels = { "Sonnet", "Haiku", "Opus" };
    private string _chatModelOverride = "claude-sonnet-4-6";

    // Phase 13 — Installer

    // Claunity 2.0 — Claude only
    private static readonly string[] Providers = { "Claude (Anthropic)" };

    private static readonly Dictionary<string, string[]> ProviderModels =
        new Dictionary<string, string[]>
    {
        { "Claude (Anthropic)", new[] { "claude-sonnet-4-6", "claude-haiku-4-5-20251001", "claude-opus-4-6" } },
    };

    private static readonly Dictionary<string, string> ProviderKeys =
        new Dictionary<string, string>
    {
        { "Claude (Anthropic)", "claude" },
    };

    // ── UI references ──────────────────────────────────────────────────────────

    private VisualElement  _welcomeScreen;
    private VisualElement  _mainUI;
    private VisualElement  _chatView;
    private VisualElement  _buildView;
    private TextField      _buildNameField;
    private DropdownField  _buildPlatformDropdown;
    private TextField      _buildPathField;
    private Toggle         _buildScenesAllToggle;
    private Toggle         _buildScenesCurrentToggle;
    private Toggle         _buildDevToggle;
    private Toggle         _buildDebugToggle;
    private Button         _buildBtn;
    private VisualElement  _buildResult;
    private Label          _buildResultSummary;
    private Label          _buildResultPath;
    private Button         _buildOpenFolderBtn;
    private string         _lastBuildFolder;
    private VisualElement  _testDashboard;
    private VisualElement  _testEmptySplash;
    private ScrollView     _testScroll;
    private VisualElement  _testContent;
    private Button         _testRunBarBtn;
    private VisualElement  _testLoadingBar;
    private VisualElement  _testLoadingFill;
    private Label          _testLoadingText;
    private IVisualElementScheduledItem _testLoadingAnimation;
    private int            _testLoadingStep;
    private int            _testRunCount;

    // Scout view
    private VisualElement  _scoutView;
    private TextField      _scoutInputField;
    private Toggle         _scoutFreeOnly;
    private Button         _scoutSearchBtn;
    private VisualElement  _scoutLoading;
    private Label          _scoutLoadingText;
    private VisualElement  _scoutLoadingFill;
    private ScrollView     _scoutScroll;
    private VisualElement  _scoutResults;
    private VisualElement  _scoutIdleSplash;
    private IVisualElementScheduledItem _scoutLoadingAnimation;
    private IVisualElementScheduledItem _scoutStatusAnimation;
    private int            _scoutLoadingStep;
    private int            _scoutStatusStep;
    private static readonly string[] ScoutStatusMessages = {
        "Analyzing your game...",
        "Searching Asset Store...",
        "Comparing assets...",
        "Picking the best matches...",
    };

    // Project view
    private VisualElement  _projectView;
    private VisualElement  _projectChatPhase;
    private VisualElement  _projectIdleSplash;
    private VisualElement  _projectActiveArea;
    private VisualElement  _projectBoardPhase;
    private ScrollView     _projectChatScroll;
    private VisualElement  _projectChatContainer;
    private TextField      _projectInputField;
    private Button         _projectSendBtn;
    private Button         _projectUploadBtn;
    private Label          _projectFileInfo;
    private VisualElement  _projectStartBar;
    private Button         _projectStartBtn;
    private VisualElement  _projectDoneBar;
    private Button         _projectNewFileBtn;
    private VisualElement  _projectInputArea;
    private Label          _projectBoardStatus;
    private VisualElement  _projectBoardContent;
    private VisualElement  _projectQuestionBlock;
    private Label          _projectQuestionText;
    private TextField      _projectAnswerField;
    private Button         _projectAnswerBtn;
    private Button         _projectBoardStartBtn;
    private Button         _projectPauseBtn;
    private Button         _projectContinueBtn;
    private VisualElement  _projectLoadingBar;
    private VisualElement  _projectLoadingFill;
    private Label          _projectLoadingText;
    private IVisualElementScheduledItem _projectLoadingAnimation;
    private int            _projectLoadingStep;
    private VisualElement  _projectThinkingIndicator;
    private Label          _projectThinkingDots;
    private IVisualElementScheduledItem _projectThinkingAnimation;
    private int            _projectDotFrame;
    private VisualElement  _settingsPanel;
    private VisualElement  _tabs;

    // Snapshot to detect unsaved settings changes
    private string _snapshotKey;
    private string _snapshotModel;
    private bool   _snapshotUseClaudeCode;
    private int    _snapshotHistoryLimit;
    private string _snapshotPersonalPrompt;
    private VisualElement  _chatContainer;
    private ScrollView     _chatScroll;
    private VisualElement  _thinkingIndicator;
    private Label          _thinkingDots;
    private TextField      _inputField;
    private Button         _sendBtn;
    private Button         _continueBtn;
    private VisualElement  _connectionDot;
    private TextField      _apiKeyField;
    private Label          _connectionStatus;
    private Label          _errorBadge;

    // Phase 13 — Welcome Screen state machine
    private enum WelcomeState { Checking, NotInstalled, Installing, NotRunning, Starting, Error }
    private Label         _welcomeStatus;
    private Button        _welcomeActionBtn;
    private VisualElement _welcomeProgress;
    private VisualElement _welcomeProgressFill;
    private Label         _welcomeProgressLabel;
    private Label         _welcomeHint;
    private Action        _welcomeActionCallback;

    private VisualElement  _mentionDropdown;
    private VisualElement  _mentionList;
    private string         _mentionPrefix;
    private List<string>   _mentionItems = new List<string>();
    private int            _mentionIndex = -1;

    // Picker overlay
    private VisualElement  _pickerOverlay;
    private VisualElement  _pickerList;
    private TextField      _pickerSearch;
    private Action<string> _pickerCallback;
    private List<(string name, string path)> _pickerAllItems = new List<(string, string)>();

    // Chat history persistence
    private const int MaxSavedMessages = 100;
    private bool _rebuilding; // prevents saving during rebuild
    private static string HistoryFilePath =>
        System.IO.Path.GetFullPath(
            System.IO.Path.Combine(Application.dataPath, "Claunity/UserSettings/ClaunityHistory.json"));

    // ── State ──────────────────────────────────────────────────────────────────

    private bool _waitingForResponse;
    private string                       _requestTab;
    private bool                         _hasMessages;
    private string                       _activeTab = "chat";
    private IVisualElementScheduledItem  _thinkingAnimation;
    private IVisualElementScheduledItem  _loadingAnimation;
    private int                          _dotFrame;
    private int                          _loadingStep;
    private VisualElement                _loadingBar;
    private VisualElement                _loadingFill;
    private Label                        _loadingText;

    private string _lastSentMessage = "";   // for "Again" retry on timeout

    // ── Chat auto-loop state ───────────────────────────────────────────────────

    private bool                         _chatAutoLooping;
    private int                          _chatActionsCount;

    // ── Project Mode state ─────────────────────────────────────────────────────

    private ProjectPlanFile              _plan;
    private readonly List<HistoryMessage> _projectHistory = new List<HistoryMessage>();
    private int                          _taskHistoryStart;   // index where current task's history begins
    private int                          _planningHistoryEnd; // index where planning ended (only these shown in chat)
    private bool                         _projectExecuting;
    private bool                         _projectVerifying;
    private bool                         _projectPauseRequested;
    private const int                    VerificationTaskId = -1;
    private readonly Dictionary<int, VisualElement> _projectTaskRows
                                                    = new Dictionary<int, VisualElement>();
    private const string ProjectPlanPath = "Assets/Claunity/UserSettings/ClaunityProjectPlan.json";
    private const string ProjectResumingPref   = "Claunity_ProjectResuming";
    private const string ProjectResumeTaskPref = "Claunity_ProjectResumeTask";
    private const string ProjectPausePref      = "Claunity_PauseRequested";
    private const string ProjectExecutingPref      = "Claunity_ProjectExecuting";
    private const string ProjectExecutingTaskPref  = "Claunity_ProjectExecutingTask";
    private const string ProjectPlanningEndPref    = "Claunity_PlanningHistoryEnd";
    private const string StreamContextPref         = "Claunity_StreamContext";   // "chat" | "project_chat" | "project_execution"
    private const string StreamTaskIdPref          = "Claunity_StreamTaskId";    // task id for project_execution
    private const string ActiveTabPref             = "Claunity_ActiveTab";       // last active tab, restored after domain reload

    // ── Project intelligence ───────────────────────────────────────────────────

    private string                       _projectContext = "";
    private int                          _indexedCount;

    // Adaptive context: model prefix → max context chars for project intelligence
    private static readonly Dictionary<string, int> ModelContextLimits =
        new Dictionary<string, int>
    {
        { "claude-opus",    80000 },
        { "claude-sonnet",  80000 },
        { "claude-haiku",   60000 },
    };
    private const int DefaultMaxContextChars = 40000;

    private int GetMaxContextChars()
    {
        var model = EditorPrefs.GetString(ModelPref, "");
        foreach (var kv in ModelContextLimits)
            if (model.StartsWith(kv.Key)) return kv.Value;
        return DefaultMaxContextChars;
    }
    // Snapshot of file write times taken at session start — to detect changes
    private Dictionary<string, System.DateTime> _sessionSnapshot = new Dictionary<string, System.DateTime>();

    // Token usage tracking
    private int _sessionInputTokens;
    private int _sessionOutputTokens;
    private Label _sessionTokenCounter;

    // Client-side rate limiter — auto-pause if too many requests per minute
    private const int  ClientRateLimit  = 10;   // max requests per 60s before pause dialog
    private const int  ClientRateWindow = 60;   // seconds
    private readonly System.Collections.Generic.List<double> _chatTimestamps = new();
    private bool _ratePausedByUser = false;

    // Per-tab chat state
    private readonly List<HistoryMessage>                   _history     = new List<HistoryMessage>();
    private readonly Dictionary<string, List<VisualElement>> _tabMessages  = new Dictionary<string, List<VisualElement>>();
    private readonly Dictionary<string, List<HistoryMessage>> _tabHistories = new Dictionary<string, List<HistoryMessage>>();
    private readonly Dictionary<string, bool>               _tabHasMessages = new Dictionary<string, bool>();

    private static readonly string[] ChatTabs = { "chat", "test" }; // project has its own UI/state

    // Model list served by the local backend (Backend~/models.json)
    private Dictionary<string, string[]> _dynamicModels;

    private static readonly string[] DotFrames = { "·  ", "· · ", "· · ·" };

    // ── Entry point ────────────────────────────────────────────────────────────

    [MenuItem("Window/Claunity")]
    public static void ShowWindow()
    {
        var window = GetWindow<ClaunityWindow>("Claunity");
        window.minSize = new Vector2(380, 500);
    }

    public void CreateGUI()
    {
        // Always clear before cloning UXML — prevents duplicate elements on domain reload
        rootVisualElement.Clear();

        var uxml = LoadAsset<VisualTreeAsset>("ClaunityWindow.uxml");
        if (uxml == null)
        {
            rootVisualElement.Add(new Label(
                "Error: ClaunityWindow.uxml not found.\n" +
                "Make sure the package is installed via Package Manager."));
            return;
        }
        uxml.CloneTree(rootVisualElement);

        var uss = LoadAsset<StyleSheet>("ClaunityWindow.uss");
        if (uss != null)
            rootVisualElement.styleSheets.Add(uss);

        QueryElements();
        WireEvents();
        InitSettings();
        LoadLogo();

        ClaunityConsole.OnChanged              += RefreshErrorBadge;
        ClaunityConsole.OnProjectCompileFailed += HandleProjectCompileFailed;
        ClaunityTestRunner.OnTestStatus        += HandleTestStatus;
        ClaunityTestRunner.OnTestComplete      += HandleTestComplete;
        // Flush any test result collected before this window finished initialising
        EditorApplication.delayCall            += ClaunityTestRunner.FlushPendingResult;

        // A new Unity session always starts on the welcome page; domain reloads keep the open UI.
        if (!SessionState.GetBool(SessionInitKey, false))
        {
            SessionState.SetBool(SessionInitKey, true);
            EditorPrefs.SetBool(StartedPref, false);
        }

        if (EditorPrefs.GetBool(StartedPref, false))
            RestoreAfterReload();
        else
            EditorCoroutineUtility.StartCoroutineOwnerless(CheckInstallationOnStart());
    }

    private void RestoreAfterReload()
    {
        _welcomeScreen.style.display = DisplayStyle.None;
        _mainUI.RemoveFromClassList("main-ui--hidden");

        foreach (var tab in ChatTabs)
        {
            if (!_tabMessages.ContainsKey(tab))    _tabMessages[tab]    = new List<VisualElement>();
            if (!_tabHistories.ContainsKey(tab))   _tabHistories[tab]   = new List<HistoryMessage>();
            if (!_tabHasMessages.ContainsKey(tab)) _tabHasMessages[tab] = false;
        }

        SwitchTab(EditorPrefs.GetString(ActiveTabPref, "chat"));
        LoadChatHistory();
        IndexProject();
        EditorCoroutineUtility.StartCoroutineOwnerless(CheckConnectionOnStart());
        EditorCoroutineUtility.StartCoroutineOwnerless(FetchModels());
        EditorCoroutineUtility.StartCoroutineOwnerless(ShowBackendVersion());

        // Clear stale test state if Unity was closed mid-test (PendingKey left in EditorPrefs)
        ClaunityTestRunner.ClearStalePendingIfNeeded();
        // Restore test loading bar if a test run genuinely survived a domain reload (in play mode)
        if (ClaunityTestRunner.IsPending)
            ShowTestLoading("Running test...");

        // Resume smart build if interrupted by domain reload
        CheckSmartBuildResume();

        // Execute any actions that were queued before the domain reload
        var pending = ClaunityActionExecutor.ConsumePendingActions();
        if (pending != null)
        {
            if (pending.actions?.Length > 0)
                EditorApplication.delayCall += () => ExecutePendingPostReloadActions(pending.actions, pending.autoResume);
            else if (pending.autoResume)
                EditorApplication.delayCall += OnPostReloadComplete;
        }

        // Resume project execution after recompile
        if (EditorPrefs.GetBool(ProjectResumingPref, false))
        {
            EditorPrefs.SetBool(ProjectResumingPref, false);
            var resumeTaskId = EditorPrefs.GetInt(ProjectResumeTaskPref, -1);
            if (resumeTaskId >= 0)
                EditorApplication.delayCall += () => ResumeProjectAfterReload(resumeTaskId);
        }

        // Clear stale ProjectExecuting flag if no project is actually running.
        // Without this, a leftover EditorPref from a previous session would cause
        // any compile error to trigger Project Mode autofix even in Chat mode.
        if (EditorPrefs.GetBool(ProjectExecutingPref, false))
        {
            LoadProjectPlan();
            if (_plan == null || _plan.state != "executing")
            {
                EditorPrefs.DeleteKey(ProjectExecutingPref);
                EditorPrefs.DeleteKey(ProjectExecutingTaskPref);
            }
        }

        // Resume streaming poll interrupted by domain reload (Claude Code wrote a script)
        var streamResume = EditorPrefs.GetString("Claunity_StreamResume", "");
        if (!string.IsNullOrEmpty(streamResume))
        {
            var streamContext = EditorPrefs.GetString(StreamContextPref, "chat");
            var streamTaskId  = EditorPrefs.GetInt(StreamTaskIdPref, -1);
            // Keys are deleted by the polling coroutine when it finishes, not here.
            // This way a second domain reload during polling can still resume.
            EditorPrefs.DeleteKey(StreamTaskIdPref);

            if (streamContext == "project_execution" && streamTaskId >= 0)
            {
                _projectExecuting = true;
                var capturedId = streamTaskId;
                var capturedSession = streamResume;
                EditorApplication.delayCall += () =>
                {
                    StartProjectLoading("Resuming...");
                    EditorCoroutineUtility.StartCoroutineOwnerless(PollStreamingForProjectExecution(capturedSession, capturedId));
                };
            }
            else if (streamContext == "project_chat")
            {
                var capturedSession = streamResume;
                EditorApplication.delayCall += () =>
                {
                    ShowProjectThinking();
                    EditorCoroutineUtility.StartCoroutineOwnerless(PollStreamingForProjectChat(capturedSession));
                };
            }
            else
            {
                _waitingForResponse = true;
                UpdateSendButton();
                EditorApplication.delayCall += () =>
                {
                    StartThinking();
                    EditorCoroutineUtility.StartCoroutineOwnerless(PollStreamingResponse(streamResume));
                };
            }
        }
    }

    private void LoadLogo()
    {
        var logo = LoadAsset<Texture2D>("ClaunityLogo.png");
        if (logo == null) return;

        // Welcome screen image
        rootVisualElement.Q<Image>("logo-image").image = logo;

        // Header image
        rootVisualElement.Q<Image>("header-logo-img").image = logo;

        // Window tab icon
        titleContent = new GUIContent("Claunity", logo);
    }

    private void OnDisable()
    {
        _thinkingAnimation?.Pause();
        _thinkingAnimation = null;
        _loadingAnimation?.Pause();
        _loadingAnimation = null;
        _projectLoadingAnimation?.Pause();
        _projectLoadingAnimation = null;
        _testLoadingAnimation?.Pause();
        _testLoadingAnimation = null;
        _scoutLoadingAnimation?.Pause();
        _scoutLoadingAnimation = null;
        _scoutStatusAnimation?.Pause();
        _scoutStatusAnimation = null;
        _projectThinkingAnimation?.Pause();
        _projectThinkingAnimation = null;
        ClaunityConsole.OnChanged              -= RefreshErrorBadge;
        ClaunityConsole.OnProjectCompileFailed -= HandleProjectCompileFailed;
        ClaunityTestRunner.OnTestStatus        -= HandleTestStatus;
        ClaunityTestRunner.OnTestComplete      -= HandleTestComplete;
    }

    private void RefreshErrorBadge()
    {
        if (_errorBadge == null) return;
        int count = ClaunityConsole.ErrorCount;
        if (count == 0)
        {
            _errorBadge.AddToClassList("error-badge--hidden");
        }
        else
        {
            _errorBadge.text = count.ToString();
            _errorBadge.RemoveFromClassList("error-badge--hidden");
        }
    }

    // ── Asset loading (package path + GUID fallback) ───────────────────────────

    private static T LoadAsset<T>(string fileName) where T : UnityEngine.Object
    {
        var asset = AssetDatabase.LoadAssetAtPath<T>(
            $"Packages/com.claunity.editor/Editor/UI/{fileName}");
        if (asset != null) return asset;

        string typeName = typeof(T).Name;
        string[] guids = AssetDatabase.FindAssets(
            $"{System.IO.Path.GetFileNameWithoutExtension(fileName)} t:{typeName}");
        if (guids.Length > 0)
            return AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guids[0]));

        return null;
    }

    // ── Query & wire ───────────────────────────────────────────────────────────

    private void QueryElements()
    {
        _welcomeScreen       = rootVisualElement.Q("welcome-screen");
        _mainUI              = rootVisualElement.Q("main-ui");

        // Phase 13 — Welcome Screen state elements
        _welcomeStatus       = rootVisualElement.Q<Label>("welcome-status");
        _welcomeActionBtn    = rootVisualElement.Q<Button>("welcome-action-btn");
        _welcomeProgress     = rootVisualElement.Q("welcome-progress");
        _welcomeProgressFill = rootVisualElement.Q("welcome-progress-fill");
        _welcomeProgressLabel = rootVisualElement.Q<Label>("welcome-progress-label");
        _welcomeHint         = rootVisualElement.Q<Label>("welcome-hint");
        _chatView          = rootVisualElement.Q("chat-view");
        _buildView                = rootVisualElement.Q("build-view");
        _buildNameField           = rootVisualElement.Q<TextField>("build-name-field");
        _buildPlatformDropdown    = rootVisualElement.Q<DropdownField>("build-platform-dropdown");
        _buildPathField           = rootVisualElement.Q<TextField>("build-path-field");
        _buildScenesAllToggle     = rootVisualElement.Q<Toggle>("build-scenes-all-toggle");
        _buildScenesCurrentToggle = rootVisualElement.Q<Toggle>("build-scenes-current-toggle");
        _buildDevToggle           = rootVisualElement.Q<Toggle>("build-dev-toggle");
        _buildDebugToggle         = rootVisualElement.Q<Toggle>("build-debug-toggle");
        _buildBtn                 = rootVisualElement.Q<Button>("build-btn");
        _buildResult              = rootVisualElement.Q("build-result");
        _buildResultSummary       = rootVisualElement.Q<Label>("build-result-summary");
        _buildResultPath          = rootVisualElement.Q<Label>("build-result-path");
        _buildOpenFolderBtn       = rootVisualElement.Q<Button>("build-open-folder-btn");
        _settingsPanel     = rootVisualElement.Q("settings-panel");
        _tabs              = rootVisualElement.Q("tabs");
        _chatContainer     = rootVisualElement.Q("chat-container");
        _chatScroll        = rootVisualElement.Q<ScrollView>("chat-scroll");
        _continueBtn       = rootVisualElement.Q<Button>("continue-btn");
        _thinkingIndicator = rootVisualElement.Q("thinking-indicator");
        _thinkingDots      = rootVisualElement.Q<Label>("thinking-dots");
        _inputField        = rootVisualElement.Q<TextField>("input-field");
        _sendBtn           = rootVisualElement.Q<Button>("send-btn");
        _connectionDot     = rootVisualElement.Q("connection-dot");
        _apiKeyField       = rootVisualElement.Q<TextField>("api-key-field");
        _connectionStatus    = rootVisualElement.Q<Label>("connection-status");
        _errorBadge          = rootVisualElement.Q<Label>("error-badge");
        _sessionTokenCounter = rootVisualElement.Q<Label>("session-token-counter");
        _mentionDropdown   = rootVisualElement.Q("mention-dropdown");
        _mentionList       = rootVisualElement.Q("mention-list");
        _pickerOverlay     = rootVisualElement.Q("picker-overlay");
        _pickerList        = rootVisualElement.Q("picker-list");
        _pickerSearch      = rootVisualElement.Q<TextField>("picker-search");
        _testDashboard        = rootVisualElement.Q("test-dashboard");
        _testContent          = rootVisualElement.Q("test-content");
        _testRunBarBtn        = rootVisualElement.Q<Button>("test-run-bar-btn");
        _testLoadingBar       = rootVisualElement.Q("test-loading-bar");
        _testLoadingFill      = rootVisualElement.Q("test-loading-fill");
        _testLoadingText      = rootVisualElement.Q<Label>("test-loading-text");
        _testEmptySplash      = rootVisualElement.Q("test-empty-splash");
        _testScroll           = rootVisualElement.Q<ScrollView>("test-scroll");
        _loadingBar           = rootVisualElement.Q("loading-bar");
        _loadingFill          = rootVisualElement.Q("loading-fill");
        _loadingText          = rootVisualElement.Q<Label>("loading-text");
        _projectView          = rootVisualElement.Q("project-view");
        _projectChatPhase     = rootVisualElement.Q("project-chat-phase");
        _projectIdleSplash    = rootVisualElement.Q("project-idle-splash");
        _projectActiveArea    = rootVisualElement.Q("project-active-area");
        _projectBoardPhase    = rootVisualElement.Q("project-board-phase");
        _projectChatScroll    = rootVisualElement.Q<ScrollView>("project-chat-scroll");
        _projectChatContainer = rootVisualElement.Q("project-chat-container");
        _projectInputField    = rootVisualElement.Q<TextField>("project-input-field");
        _projectSendBtn       = rootVisualElement.Q<Button>("project-send-btn");
        _projectUploadBtn     = rootVisualElement.Q<Button>("project-upload-btn");
        _projectFileInfo      = rootVisualElement.Q<Label>("project-file-info");
        _projectStartBar      = rootVisualElement.Q("project-start-bar");
        _projectStartBtn      = rootVisualElement.Q<Button>("project-start-btn");
        _projectDoneBar       = rootVisualElement.Q("project-done-bar");
        _projectNewFileBtn    = rootVisualElement.Q<Button>("project-new-file-btn");
        _projectInputArea     = rootVisualElement.Q("project-input-area");
        _projectBoardStatus   = rootVisualElement.Q<Label>("project-board-status");
        _projectBoardContent  = rootVisualElement.Q("project-board-content");
        _projectQuestionBlock = rootVisualElement.Q("project-question-block");
        _projectQuestionText  = rootVisualElement.Q<Label>("project-question-text");
        _projectAnswerField   = rootVisualElement.Q<TextField>("project-answer-field");
        _projectAnswerBtn     = rootVisualElement.Q<Button>("project-answer-btn");
        _projectBoardStartBtn = rootVisualElement.Q<Button>("project-board-start-btn");
        _projectPauseBtn      = rootVisualElement.Q<Button>("project-pause-btn");
        _projectContinueBtn   = rootVisualElement.Q<Button>("project-continue-btn");
        _projectLoadingBar    = rootVisualElement.Q("project-loading-bar");
        _projectLoadingFill   = rootVisualElement.Q("project-loading-fill");
        _projectLoadingText   = rootVisualElement.Q<Label>("project-loading-text");
        _projectThinkingIndicator = rootVisualElement.Q("project-thinking-indicator");
        _projectThinkingDots      = rootVisualElement.Q<Label>("project-thinking-dots");

        _scoutView        = rootVisualElement.Q("scout-view");
        _scoutInputField  = rootVisualElement.Q<TextField>("scout-input-field");
        _scoutFreeOnly    = rootVisualElement.Q<Toggle>("scout-free-only");
        _scoutSearchBtn   = rootVisualElement.Q<Button>("scout-search-btn");
        _scoutLoading     = rootVisualElement.Q("scout-loading");
        _scoutLoadingText = rootVisualElement.Q<Label>("scout-loading-text");
        _scoutLoadingFill = rootVisualElement.Q("scout-loading-fill");
        _scoutScroll      = rootVisualElement.Q<ScrollView>("scout-scroll");
        _scoutResults     = rootVisualElement.Q("scout-results");
        _scoutIdleSplash  = rootVisualElement.Q("scout-idle-splash");
    }

    private void WireEvents()
    {
        // Welcome (Phase 13 — dynamic action button)
        if (_welcomeActionBtn != null) _welcomeActionBtn.clicked += () => _welcomeActionCallback?.Invoke();

        // Header
        rootVisualElement.Q("logo-btn").RegisterCallback<ClickEvent>(_ => OnLogoClicked());
        rootVisualElement.Q<Button>("settings-btn").clicked += ToggleSettings;

        // Tabs
        foreach (var id in new[] { "chat", "project", "test", "scout", "build" })
        {
            var captured = id;
            rootVisualElement.Q<Button>($"tab-{id}").clicked += () => SwitchTab(captured);
        }

        // Scout
        if (_scoutSearchBtn != null) _scoutSearchBtn.clicked += OnScoutSearch;

        // Build tab
        InitBuildView();

        // Input
        _inputField.RegisterCallback<KeyDownEvent>(OnInputKeyDown, TrickleDown.TrickleDown);
        _inputField.RegisterValueChangedCallback(e => { UpdateSendButton(); UpdateInputHeight(); CheckMentionDropdown(e.newValue); });
        _sendBtn.clicked += OnSendClicked;

        // Model selector
        var modelSelectorBtn = rootVisualElement.Q<Button>("model-selector-btn");
        if (modelSelectorBtn != null)
        {
            _chatModelOverride = EditorPrefs.GetString(ChatModelPref, ChatModels[0]);
            RefreshModelSelectorBtn(modelSelectorBtn);
            modelSelectorBtn.clicked += () =>
            {
                int idx = System.Array.IndexOf(ChatModels, _chatModelOverride);
                idx = (idx + 1) % ChatModels.Length;
                _chatModelOverride = ChatModels[idx];
                EditorPrefs.SetString(ChatModelPref, _chatModelOverride);
                RefreshModelSelectorBtn(modelSelectorBtn);
            };
        }

        // Click on thinking indicator = cancel/unlock (escape hatch for stuck state)
        _thinkingIndicator?.RegisterCallback<ClickEvent>(_ =>
        {
            if (!_waitingForResponse) return;
            StopThinking();
            _waitingForResponse = false;
            UpdateSendButton();
        });
        if (_testRunBarBtn != null) _testRunBarBtn.clicked += StartTestRun;
        _testRunCount = EditorPrefs.GetInt("Claunity_TestRunCount", 0);
        UpdateSendButton();

        // Chat toolbar
        rootVisualElement.Q<Button>("clear-btn").clicked += OnClearChat;
        if (_continueBtn != null) _continueBtn.clicked += OnContinueClicked;

        // Feature buttons — special interactive ones
        var featScene = rootVisualElement.Q<Button>("feat-scene");
        if (featScene != null) featScene.clicked += () => OnFeatScreenshot("scene");
        var featGame = rootVisualElement.Q<Button>("feat-game");
        if (featGame != null) featGame.clicked += () => OnFeatScreenshot("game");
        var featReview = rootVisualElement.Q<Button>("feat-review");
        if (featReview != null) featReview.clicked += () =>
            ShowScriptPicker("Select script to review", name =>
                SendFeatureMessage($"Review @{name} for bugs and improvements"));
        var featExplain = rootVisualElement.Q<Button>("feat-explain");
        if (featExplain != null) featExplain.clicked += () =>
            ShowScriptPicker("Select script to explain", name =>
                SendFeatureMessage($"Explain how @{name} works"));
        var featPrefab = rootVisualElement.Q<Button>("feat-prefab");
        if (featPrefab != null) featPrefab.clicked += () =>
            ShowGameObjectPicker("Select object to create prefab from", name =>
                SendFeatureMessage($"Create a prefab from the '{name}' GameObject and save it to Assets/Prefabs"));

        // Feature buttons — simple static messages
        var featBugs = rootVisualElement.Q<Button>("feat-bugs");
        if (featBugs != null) featBugs.clicked += () =>
            SendFeatureMessage(ClaunityConsole.FormatForFixRequest());
        var featDiff = rootVisualElement.Q<Button>("feat-diff");
        if (featDiff != null) featDiff.clicked += () =>
            SendFeatureMessage(BuildRecentChangesMessage());
        var featProject = rootVisualElement.Q<Button>("feat-project");
        if (featProject != null) featProject.clicked += () =>
            SendFeatureMessage("Describe the structure of this Unity project");
        var featMap = rootVisualElement.Q<Button>("feat-map");
        if (featMap != null) featMap.clicked += () =>
            SendFeatureMessage("Create a map of scripts and their relationships in the project");

        // Picker overlay
        rootVisualElement.Q<Button>("picker-cancel-btn").clicked += HidePicker;
        _pickerSearch.RegisterValueChangedCallback(e => FilterPicker(e.newValue));

        // Project view
        if (_projectUploadBtn  != null) _projectUploadBtn.clicked  += OnProjectUploadClicked;
        if (_projectSendBtn    != null) _projectSendBtn.clicked    += OnProjectSendClicked;
        if (_projectInputField != null)
        {
            _projectInputField.RegisterCallback<KeyDownEvent>(OnProjectInputKeyDown, TrickleDown.TrickleDown);
            _projectInputField.RegisterValueChangedCallback(_ => UpdateProjectSendButton());
        }
        if (_projectStartBtn   != null) _projectStartBtn.clicked   += OnProjectStartBuilding;
        if (_projectNewFileBtn != null) _projectNewFileBtn.clicked += OnNewProjectClicked;
        if (_projectAnswerBtn  != null) _projectAnswerBtn.clicked  += OnProjectAnswerSubmit;
        if (_projectAnswerField != null)
            _projectAnswerField.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return) { e.StopPropagation(); OnProjectAnswerSubmit(); }
            }, TrickleDown.TrickleDown);
        if (_projectBoardStartBtn != null) _projectBoardStartBtn.clicked += OnProjectStartBuilding;
        if (_projectPauseBtn     != null) _projectPauseBtn.clicked     += OnProjectPauseClicked;
        if (_projectContinueBtn  != null) _projectContinueBtn.clicked  += OnProjectContinueClicked;

        // Settings
        rootVisualElement.Q<Button>("toggle-key-btn").clicked      += ToggleKeyVisibility;
        rootVisualElement.Q<Button>("save-btn").clicked            += OnSaveSettings;
        rootVisualElement.Q<Button>("test-connection-btn").clicked += OnTestConnection;
        rootVisualElement.Q<Button>("clear-data-btn").clicked      += OnClearUserData;

        var resetUsageBtn = rootVisualElement.Q<Button>("reset-usage-btn");
        if (resetUsageBtn != null) resetUsageBtn.clicked += OnResetDailyUsage;

        var stopBtn    = rootVisualElement.Q<Button>("backend-stop-btn");
        var restartBtn = rootVisualElement.Q<Button>("backend-restart-btn");
        if (stopBtn    != null) stopBtn.clicked    += OnStopBackend;
        if (restartBtn != null) restartBtn.clicked += OnRestartBackend;
    }

    private void InitSettings()
    {
        // Seed dynamic models from fallback
        _dynamicModels = new Dictionary<string, string[]>(ProviderModels);

        _apiKeyField.value           = EditorPrefs.GetString(ApiKeyPref, "");
        _apiKeyField.isPasswordField = true;

        var modelDropdown = rootVisualElement.Q<DropdownField>("model-dropdown");

        // Claunity 2.0 — Claude only
        var savedModel = EditorPrefs.GetString(ModelPref, "");
        UpdateModelDropdown(modelDropdown, Providers[0], savedModel);

        var aiSourceDropdown = rootVisualElement.Q<DropdownField>("ai-source-dropdown");
        var apiSourceSection = rootVisualElement.Q("api-source-section");
        if (aiSourceDropdown != null)
        {
            aiSourceDropdown.choices = new List<string> { "API Key", "Claude Code" };
            bool savedClaudeCode = EditorPrefs.GetBool(UseClaudeCodePref, false);
            aiSourceDropdown.value = savedClaudeCode ? "Claude Code" : "API Key";
            if (apiSourceSection != null)
                apiSourceSection.EnableInClassList("api-source-section--hidden", savedClaudeCode);
            aiSourceDropdown.RegisterValueChangedCallback(e =>
            {
                bool isClaudeCode = e.newValue == "Claude Code";
                if (apiSourceSection != null)
                    apiSourceSection.EnableInClassList("api-source-section--hidden", isClaudeCode);
                var statusLabel = rootVisualElement.Q<Label>("connection-status");
                if (statusLabel != null) statusLabel.text = "";
            });
        }

        var historyLimitField = rootVisualElement.Q<IntegerField>("history-limit-field");
        if (historyLimitField != null)
            historyLimitField.value = EditorPrefs.GetInt(HistoryLimitPref, 4);

        var personalPromptField = rootVisualElement.Q<TextField>("personal-prompt-field");
        if (personalPromptField != null)
            personalPromptField.value = EditorPrefs.GetString(PersonalPromptPref, "");

    }

    private void UpdateModelDropdown(DropdownField modelDropdown, string provider, string preferredModel)
    {
        if (!_dynamicModels.TryGetValue(provider, out var models)) return;
        modelDropdown.choices = new List<string>(models);
        modelDropdown.value   = models.Contains(preferredModel) ? preferredModel : models[0];
    }

    // ── Welcome ────────────────────────────────────────────────────────────────

    private void RefreshModelSelectorBtn(Button btn)
    {
        int idx = System.Array.IndexOf(ChatModels, _chatModelOverride);
        if (idx < 0) idx = 0;
        btn.text = ChatModelLabels[idx];
        foreach (var label in ChatModelLabels)
            btn.RemoveFromClassList($"model-selector-btn--{label.ToLower()}");
        btn.AddToClassList($"model-selector-btn--{ChatModelLabels[idx].ToLower()}");
    }

    private void OnLogoClicked()
    {
        bool settingsOpen = _settingsPanel.ClassListContains("settings-panel--visible");
        if (settingsOpen && HasUnsavedChanges())
        {
            bool save = EditorUtility.DisplayDialog(
                "Unsaved changes",
                "You have unsaved settings. Save before leaving?",
                "Save", "Discard");

            if (save) OnSaveSettings();
        }

        EditorPrefs.SetBool(StartedPref, false);
        _mainUI.AddToClassList("main-ui--hidden");
        _welcomeScreen.style.display = DisplayStyle.Flex;
        _hasMessages = false;

        // Show correct welcome state (don't auto-enter, user explicitly went back)
        SetWelcomeState(IsInstalled() ? WelcomeState.NotRunning : WelcomeState.NotInstalled);
    }

    // Called once main UI is fully ready to open
    private void EnterMainUI()
    {
        EditorPrefs.SetBool(StartedPref, true);
        _welcomeScreen.style.display = DisplayStyle.None;
        _mainUI.RemoveFromClassList("main-ui--hidden");

        foreach (var tab in ChatTabs)
        {
            if (!_tabMessages.ContainsKey(tab))    _tabMessages[tab]    = new List<VisualElement>();
            if (!_tabHistories.ContainsKey(tab))   _tabHistories[tab]   = new List<HistoryMessage>();
            if (!_tabHasMessages.ContainsKey(tab)) _tabHasMessages[tab] = false;
        }

        SwitchTab("chat");
        LoadChatHistory();
        IndexProject();

        // Restore saved settings to the freshly-started backend
        var savedKey           = EditorPrefs.GetString(ApiKeyPref, "");
        var savedModel         = EditorPrefs.GetString(ModelPref,  "");
        var savedClaudeCode    = EditorPrefs.GetBool(UseClaudeCodePref, false);
        var savedPersonalPrompt = EditorPrefs.GetString(PersonalPromptPref, "");
        var savedHistoryLimit   = EditorPrefs.GetInt(HistoryLimitPref, 4);
        EditorCoroutineUtility.StartCoroutineOwnerless(PostConfig(savedKey, model: savedModel, useClaudeCode: savedClaudeCode, personalPrompt: savedPersonalPrompt, historyLimit: savedHistoryLimit));

        EditorCoroutineUtility.StartCoroutineOwnerless(CheckConnectionOnStart());
        EditorCoroutineUtility.StartCoroutineOwnerless(FetchModels());
        EditorCoroutineUtility.StartCoroutineOwnerless(ShowBackendVersion());
    }

    // ── Tabs ───────────────────────────────────────────────────────────────────

    private void SwitchTab(string tabId)
    {
        // Save current tab state before switching
        SaveCurrentTabState();

        foreach (var id in new[] { "chat", "project", "test", "scout", "build" })
        {
            var btn = rootVisualElement.Q<Button>($"tab-{id}");
            if (id == tabId) btn?.AddToClassList("tab--active");
            else             btn?.RemoveFromClassList("tab--active");
        }
        _activeTab = tabId;
        EditorPrefs.SetString(ActiveTabPref, tabId);

        bool isBuild   = tabId == "build";
        bool isTest    = tabId == "test";
        bool isProject = tabId == "project";
        bool isScout   = tabId == "scout";

        if (_chatView != null)      _chatView.EnableInClassList("chat-view--hidden", isBuild || isTest || isProject || isScout);
        if (_buildView != null)     _buildView.EnableInClassList("build-view--hidden", !isBuild);
        if (_testDashboard != null) _testDashboard.EnableInClassList("test-dashboard--hidden", !isTest);
        if (_projectView != null)   _projectView.EnableInClassList("project-view--hidden", !isProject);
        if (_scoutView != null)     _scoutView.EnableInClassList("scout-view--hidden", !isScout);

        if (isProject) RefreshProjectView();

        if (!isBuild && !isTest && !isProject && !isScout)
        {
            RestoreTabState(tabId);
            RefreshSplash(tabId);

            var toolbar = rootVisualElement.Q("chat-toolbar");
            toolbar?.EnableInClassList("chat-toolbar--hidden", !_hasMessages);
        }
    }

    private void SaveCurrentTabState()
    {
        if (!_tabMessages.ContainsKey(_activeTab)) return;
        _tabMessages[_activeTab]    = _chatContainer.Children().ToList();
        _tabHistories[_activeTab]   = new List<HistoryMessage>(_history);
        _tabHasMessages[_activeTab] = _hasMessages;
    }

    private void RestoreTabState(string tabId)
    {
        _chatContainer.Clear();
        _history.Clear();
        _hasMessages = false;

        if (_tabMessages.TryGetValue(tabId, out var msgs))
            foreach (var msg in msgs) _chatContainer.Add(msg);

        if (_tabHistories.TryGetValue(tabId, out var hist))
            _history.AddRange(hist);

        _hasMessages = _tabHasMessages.TryGetValue(tabId, out var hm) && hm;

        if (_hasMessages)
        {
            // Ensure scroll is visible (chat-scroll starts hidden in UXML, shown via HideSplash)
            _chatScroll?.RemoveFromClassList("chat-scroll--hidden");
            rootVisualElement.Q("mode-splash")?.AddToClassList("mode-splash--hidden");

            // Force UIToolkit to re-layout and scroll — needed after Clear()+re-add in EditorWindows
            _chatContainer.MarkDirtyRepaint();
            _chatScroll.schedule.Execute(() =>
            {
                _chatScroll.MarkDirtyRepaint();
                _chatScroll.scrollOffset = new Vector2(0, float.MaxValue);
            }).StartingIn(100);
        }
    }

    // ── Data classes ───────────────────────────────────────────────────────────

    [System.Serializable]
    private class HistoryMessage
    {
        public string role;
        public string content;
    }

    [System.Serializable]
    private class ScoutRequest
    {
        public string description;
        public bool   free_only;
    }

    [System.Serializable]
    private class ScoutAsset
    {
        public string category;
        public string name;
        public string url;
        public string price;
        public string why;
    }

    [System.Serializable]
    private class ScoutResponse
    {
        public ScoutAsset[] assets;
    }

    [System.Serializable]
    private class ChatRequest
    {
        public string           message;
        public string           project_files;  // script list for context classifier
        public string           project_path;   // absolute path to Unity project root
        public string           mode;
        public string           image;          // base64 PNG, empty if none
        public HistoryMessage[] history;
        public string           model_override; // overrides config model for this session
    }

    [System.Serializable]
    private class ChatContinueRequest
    {
        public string session_id;
        public string tool_use_id;
        public string tool_result;
    }

    [System.Serializable] private class ConfigRequest          { public string api_key; public string model; public bool use_claude_code; public string personal_prompt; public int history_limit; }
    [System.Serializable] private class ChatModelOverride      { public string model_override; }
    [System.Serializable] private class ClaudeCodeCheckResponse { public bool found; public string version; }

    // Unified response — type is "tool_request" or "final"
    [System.Serializable]
    private class ChatResponse
    {
        public string type;           // "tool_request" | "final" | "streaming"
        // tool_request fields
        public string session_id;
        public string tool_name;
        public string tool_input_json;
        public string tool_use_id;
        public string narration;      // Claude's text before tool call
        // final fields
        public string reply;
        public string stop_reason;
        // always present
        public int    input_tokens;
        public int    output_tokens;
    }

    [System.Serializable]
    private class StreamEvent
    {
        public string type;  // "narration" | "tool" | "final" | "error"
        public string text;
    }

    [System.Serializable]
    private class StreamPollResponse
    {
        public StreamEvent[] events;
        public bool          done;
        public string        reply; // final reply text, always set when done=true
    }

    [System.Serializable] private class ErrorResponse { public string detail; public string error_code; }

    [System.Serializable]
    private class ModelsPayload
    {
        public string[] claude;
    }

    [System.Serializable]
    private class SavedMessage
    {
        public string role;    // "user" | "assistant" | "action"
        public string content;
    }

    [System.Serializable]
    private class TabHistory
    {
        public SavedMessage[] messages;
    }

    [System.Serializable]
    private class ChatHistoryStore
    {
        public TabHistory chat = new TabHistory();
    }

    private class TestReport
    {
        public string           working = "";
        public List<string>     errors  = new List<string>();
        public List<string>     warnings = new List<string>();
        public string           visual  = "";
        public List<string[]>   recommendations = new List<string[]>();
    }

    // ── Project Mode data classes ───────────────────────────────────────────────

    [System.Serializable]
    private class ProjectTask
    {
        public int    id;
        public string name;
        public string description;
        public string status = "pending"; // "pending", "in_progress", "done", "failed"
    }

    [System.Serializable]
    private class ProjectEpic
    {
        public string         name;
        public ProjectTask[]  tasks;
    }

    [System.Serializable]
    private class ProjectPlanFile
    {
        public string           title       = "";
        public string           state       = "idle"; // idle, questioning, plan_ready, executing, paused, stopped, completed
        public string           gddFileName = "";
        public string           gddContent  = "";
        public ProjectEpic[]    epics;
        public HistoryMessage[] chatHistory;
    }

    [System.Serializable]
    private class ProjectPlanWrapper { public ProjectPlanData plan; }

    [System.Serializable]
    private class ProjectPlanData
    {
        public string        title;
        public ProjectEpic[] epics;
    }

    // ── Backend data classes ───────────────────────────────────────────────────

    [System.Serializable]
    private class VersionResponse
    {
        public string version;
        public bool   update_available;
        public bool   update_required;
        public string latest_version;
    }
}

}