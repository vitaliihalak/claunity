using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
    // PROJECT MODE
    // ══════════════════════════════════════════════════════════════════════════

    // ── View refresh ───────────────────────────────────────────────────────────

    private void RefreshProjectView()
    {
        LoadProjectPlan();
        if (_plan == null || _plan.state == "idle" || _plan.state == "questioning")
        {
            ShowProjectChatPhase();
            RebuildProjectChat();
            _projectInputArea?.RemoveFromClassList("project-input-area--hidden");
            _projectDoneBar?.AddToClassList("project-start-bar--hidden");
        }
        else if (_plan.state == "completed")
        {
            // Completed — show chat phase with summary and "Load New Project" button
            ShowProjectChatPhase();
            RebuildProjectChat();
            _projectInputArea?.AddToClassList("project-input-area--hidden");
            _projectDoneBar?.RemoveFromClassList("project-start-bar--hidden");
        }
        else
        {
            // plan_ready, executing, paused, stopped — show board
            ShowProjectBoardPhase();
            RebuildProjectBoard();
            UpdateProjectControls();
        }
    }

    private void ShowProjectChatPhase()
    {
        _projectChatPhase?.RemoveFromClassList("project-chat-phase--hidden");
        _projectBoardPhase?.AddToClassList("project-board-phase--hidden");

        // Show idle splash only when no plan exists yet
        bool hasGdd = _plan != null && _plan.state != "idle";
        _projectIdleSplash?.EnableInClassList("project-idle-splash--hidden", hasGdd);
        _projectActiveArea?.EnableInClassList("project-active-area--hidden", !hasGdd);
    }

    private void ShowProjectBoardPhase()
    {
        _projectChatPhase?.AddToClassList("project-chat-phase--hidden");
        _projectBoardPhase?.RemoveFromClassList("project-board-phase--hidden");
    }

    // ── File upload ────────────────────────────────────────────────────────────

    private void OnProjectUploadClicked()
    {
        var path = EditorUtility.OpenFilePanel("Load GDD or project brief", "", "txt,md,cs,json");
        if (string.IsNullOrEmpty(path)) return;

        string content;
        try { content = File.ReadAllText(path); }
        catch (Exception e) { AddProjectMessage($"Could not read file: {e.Message}", false); return; }

        var fileName = System.IO.Path.GetFileName(path);
        var charCount = content.Length;

        // Switch from idle splash to active area
        _projectIdleSplash?.AddToClassList("project-idle-splash--hidden");
        _projectActiveArea?.RemoveFromClassList("project-active-area--hidden");

        // Ensure input area is visible and done bar is hidden (in case previous project was completed)
        _projectInputArea?.RemoveFromClassList("project-input-area--hidden");
        _projectDoneBar?.AddToClassList("project-start-bar--hidden");

        if (_projectFileInfo != null)
        {
            _projectFileInfo.text = $"📄 {fileName}  ({charCount:N0} chars)";
            _projectFileInfo.RemoveFromClassList("project-file-info--hidden");
        }

        EnsurePlan();
        _plan.gddFileName = fileName;
        int gddBudget = GetMaxContextChars() / 3; // reserve 1/3 of context for GDD
        _plan.gddContent  = content.Length > gddBudget ? content.Substring(0, gddBudget) + "\n[...truncated]" : content;
        _plan.state = "questioning";
        SaveProjectPlan();

        var msg = $"I've loaded '{fileName}' ({charCount:N0} chars). Please read it and ask any clarifying questions before we build the plan.";
        SendProjectMessage(msg);
    }

    // ── Project chat (planning phase) ─────────────────────────────────────────

    private void OnProjectInputKeyDown(KeyDownEvent e)
    {
        if (e.keyCode == KeyCode.Return && e.ctrlKey)
        {
            e.StopPropagation();
            OnProjectSendClicked();
        }
    }

    private void OnProjectSendClicked()
    {
        if (_projectInputField == null) return;
        var text = _projectInputField.value.Trim();
        if (string.IsNullOrEmpty(text)) return;
        _projectInputField.value = "";
        SendProjectMessage(text);
    }

    private void SendProjectMessage(string text)
    {
        EnsurePlan();
        if (_plan.state == "idle") _plan.state = "questioning";

        AddProjectMessage(text, isUser: true);
        _projectHistory.Add(new HistoryMessage { role = "user", content = text });
        SaveProjectPlan();

        UpdateProjectSendButton();
        EditorCoroutineUtility.StartCoroutineOwnerless(PostProjectChat(text));
    }

    private IEnumerator PostProjectChat(string message)
    {
        SetProjectInputEnabled(false);
        _waitingForResponse = true;
        ShowProjectThinking();

        var projectFiles = _projectContext ?? "";
        if (!string.IsNullOrEmpty(_plan?.gddContent))
            projectFiles += $"\n\n=== PROJECT BRIEF ({_plan.gddFileName}) ===\n{_plan.gddContent}";

        var payload = new ChatRequest
        {
            message       = message,
            project_files = projectFiles,
            project_path  = System.IO.Path.GetDirectoryName(Application.dataPath),
            mode          = "project",
            image   = "",
            history = _projectHistory
                .Take(_projectHistory.Count - 1)
                .Where(h => h.role == "user" || h.role == "assistant")
                .TakeLast(20).ToArray(),
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
            SetProjectInputEnabled(true);
            _waitingForResponse = false;
            HideProjectThinking();
            AddProjectMessage($"Error: {req.error}", false);
            yield break;
        }

        var response = JsonUtility.FromJson<ChatResponse>(req.downloadHandler.text);
        if (response == null)
        {
            SetProjectInputEnabled(true);
            _waitingForResponse = false;
            HideProjectThinking();
            AddProjectMessage("Error: Invalid response from server.", false);
            yield break;
        }

        TrackTokenUsage(response.input_tokens, response.output_tokens);

        // Claude Code streaming path — poll for events in the project chat container
        if (response.type == "streaming")
        {
            EditorPrefs.SetString("Claunity_StreamResume", response.session_id);
            EditorPrefs.SetString(StreamContextPref, "project_chat");
            yield return PollStreamingForProjectChat(response.session_id);
            EditorPrefs.DeleteKey("Claunity_StreamResume");
            EditorPrefs.DeleteKey(StreamContextPref);
            yield break;
        }

        // API path: Claude responds with text only (no tools needed in planning)
        SetProjectInputEnabled(true);
        _waitingForResponse = false;
        HideProjectThinking();
        var reply = response.reply ?? "";
        _projectHistory.Add(new HistoryMessage { role = "assistant", content = reply });
        SaveProjectPlan();

        // Check if Claude returned a plan JSON
        var planData = TryParseProjectPlan(reply);
        if (planData != null)
        {
            OnPlanReceived(planData);
        }
        else
        {
            if (reply.Contains("\"plan\"") && reply.Contains("\"epics\""))
                AddProjectMessage("⚠ Could not parse the project plan. Please try again — ask Claude to return the plan as valid JSON.", isUser: false);
            else
                AddProjectMessage(reply, isUser: false);
        }
    }

    private IEnumerator PollStreamingForProjectChat(string sessionId)
    {
        // Keep session resumable across domain reloads
        EditorPrefs.SetString("Claunity_StreamResume", sessionId);
        EditorPrefs.SetString(StreamContextPref, "project_chat");

        int failCount = 0;
        double startTime = EditorApplication.timeSinceStartup;
        const double maxPollSec = 300.0;

        while (true)
        {
            yield return new EditorWaitForSeconds(0.5f);

            if (EditorApplication.timeSinceStartup - startTime > maxPollSec)
            {
                HideProjectThinking();
                SetProjectInputEnabled(true);
                _waitingForResponse = false;
                AddProjectMessage("⚠ Response timed out. Please try sending your message again.", isUser: false);
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
                if (failCount >= 20)
                {
                    HideProjectThinking();
                    SetProjectInputEnabled(true);
                    _waitingForResponse = false;
                    AddProjectMessage("⚠ Lost connection to Desktop App. Make sure it is running and try again.", isUser: false);
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
                        HideProjectThinking();
                        ApplyProjectChatReply(evt.text);
                        ShowProjectThinking();
                    }
                    else if (evt.type == "final" || evt.type == "error")
                    {
                        HideProjectThinking();
                        SetProjectInputEnabled(true);
                        _waitingForResponse = false;
                        var text = evt.text ?? "";
                        if (!string.IsNullOrEmpty(text))
                            ApplyProjectChatReply(text);
                        EditorPrefs.DeleteKey("Claunity_StreamResume");
                        EditorPrefs.DeleteKey(StreamContextPref);
                        yield break;
                    }
                }
            }

            if (poll?.done == true)
            {
                HideProjectThinking();
                SetProjectInputEnabled(true);
                _waitingForResponse = false;
                var doneReply = poll.reply ?? "";
                if (!string.IsNullOrEmpty(doneReply))
                    ApplyProjectChatReply(doneReply);
                EditorPrefs.DeleteKey("Claunity_StreamResume");
                EditorPrefs.DeleteKey(StreamContextPref);
                yield break;
            }
        }
    }

    private IEnumerator PollStreamingForProjectExecution(string sessionId, int taskId)
    {
        // Keep session resumable across domain reloads
        EditorPrefs.SetString("Claunity_StreamResume", sessionId);
        EditorPrefs.SetString(StreamContextPref, "project_execution");
        EditorPrefs.SetInt(StreamTaskIdPref, taskId);

        int failCount = 0;
        double startTime = EditorApplication.timeSinceStartup;
        const double maxPollSec = 600.0; // tasks can take longer

        while (true)
        {
            yield return new EditorWaitForSeconds(0.5f);

            if (EditorApplication.timeSinceStartup - startTime > maxPollSec)
            {
                AddProjectMessage("⚠ Task execution timed out after 10 minutes.", isUser: false);
                EditorPrefs.DeleteKey("Claunity_StreamResume");
                EditorPrefs.DeleteKey(StreamContextPref);
                EditorPrefs.DeleteKey(StreamTaskIdPref);
                MarkProjectTaskFailed(taskId, "Timeout");
                yield break;
            }

            using var req = UnityWebRequest.Get($"{ServerUrl}/chat/stream/{sessionId}");
            req.timeout = 10;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                failCount++;
                if (failCount >= 20)
                {
                    EditorPrefs.DeleteKey("Claunity_StreamResume");
                    EditorPrefs.DeleteKey(StreamContextPref);
                    EditorPrefs.DeleteKey(StreamTaskIdPref);
                    MarkProjectTaskFailed(taskId, "Poll failed");
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
                        _projectHistory.Add(new HistoryMessage { role = "assistant", content = evt.text });
                        AddProjectMessage(evt.text, isUser: false);
                    }
                    else if (evt.type == "tool" && !string.IsNullOrEmpty(evt.text))
                    {
                        StartProjectLoading(evt.text);
                    }
                    else if (evt.type == "error")
                    {
                        EditorPrefs.DeleteKey("Claunity_StreamResume");
                        EditorPrefs.DeleteKey(StreamContextPref);
                        EditorPrefs.DeleteKey(StreamTaskIdPref);
                        MarkProjectTaskFailed(taskId, evt.text ?? "Unknown error");
                        yield break;
                    }
                    else if (evt.type == "final")
                    {
                        var text = evt.text ?? "";
                        if (!string.IsNullOrEmpty(text))
                        {
                            _projectHistory.Add(new HistoryMessage { role = "assistant", content = text });
                            SaveProjectPlan();
                        }
                        EditorPrefs.DeleteKey("Claunity_StreamResume");
                        EditorPrefs.DeleteKey(StreamContextPref);
                        EditorPrefs.DeleteKey(StreamTaskIdPref);
                        MarkProjectTaskDone(taskId);
                        yield break;
                    }
                }
            }

            if (poll?.done == true)
            {
                var doneReply = poll.reply ?? "";
                if (!string.IsNullOrEmpty(doneReply))
                {
                    _projectHistory.Add(new HistoryMessage { role = "assistant", content = doneReply });
                    SaveProjectPlan();
                }
                EditorPrefs.DeleteKey("Claunity_StreamResume");
                EditorPrefs.DeleteKey(StreamContextPref);
                EditorPrefs.DeleteKey(StreamTaskIdPref);
                MarkProjectTaskDone(taskId);
                yield break;
            }
        }
    }

    private void ApplyProjectChatReply(string reply)
    {
        if (string.IsNullOrEmpty(reply)) return;
        // Check for duplicate (domain reload resume)
        if (_projectHistory.Count > 0 &&
            _projectHistory[_projectHistory.Count - 1].role == "assistant" &&
            _projectHistory[_projectHistory.Count - 1].content == reply) return;

        _projectHistory.Add(new HistoryMessage { role = "assistant", content = reply });
        SaveProjectPlan();

        var planData = TryParseProjectPlan(reply);
        if (planData != null)
            OnPlanReceived(planData);
        else if (reply.Contains("\"plan\"") && reply.Contains("\"epics\""))
            AddProjectMessage("⚠ Could not parse the project plan. Please try again — ask Claude to return the plan as valid JSON.", isUser: false);
        else
            AddProjectMessage(reply, isUser: false);
    }

    private void OnPlanReceived(ProjectPlanData planData)
    {
        EnsurePlan();
        _plan.title  = planData.title ?? "Project Plan";
        _plan.epics  = planData.epics;
        _plan.state  = "plan_ready";
        // Assign sequential IDs and ensure status is set
        int id = 1;
        if (_plan.epics != null)
            foreach (var epic in _plan.epics)
                if (epic.tasks != null)
                    foreach (var task in epic.tasks)
                    {
                        if (task.id == 0) task.id = id++;
                        if (string.IsNullOrEmpty(task.status)) task.status = "pending";
                    }
        SaveProjectPlan();

        // Show plan summary in chat
        var sb = new StringBuilder();
        sb.AppendLine($"✅ Plan ready: **{_plan.title}**");
        sb.AppendLine();
        if (_plan.epics != null)
            foreach (var epic in _plan.epics)
            {
                sb.AppendLine($"**{epic.name}**");
                if (epic.tasks != null)
                    foreach (var t in epic.tasks)
                        sb.AppendLine($"  • {t.name}");
            }
        AddProjectMessage(sb.ToString().TrimEnd(), isUser: false);

        // Show Start Building bar
        _projectStartBar?.RemoveFromClassList("project-start-bar--hidden");

        // Scroll to bottom
        _projectChatScroll?.schedule
            .Execute(() => _projectChatScroll.scrollOffset = new Vector2(0, float.MaxValue))
            .StartingIn(50);
    }

    private void AddProjectMessage(string text, bool isUser)
    {
        if (_projectChatContainer == null) return;

        var label = new Label(isUser ? text : ParseMarkdown(text));
        label.AddToClassList("bubble-text");
        label.enableRichText = true;

        var bubble = new VisualElement();
        bubble.AddToClassList("bubble");
        bubble.AddToClassList(isUser ? "bubble--user" : "bubble--claunity");
        bubble.Add(label);

        var row = new VisualElement();
        row.AddToClassList("message-row");
        if (isUser) row.AddToClassList("message-row--user");
        else row.Add(MakeAvatar());
        row.Add(bubble);

        _projectChatContainer.Add(row);
        _projectChatScroll?.schedule
            .Execute(() => _projectChatScroll.scrollOffset = new Vector2(0, float.MaxValue))
            .StartingIn(50);
    }

    private void RebuildProjectChat()
    {
        if (_projectChatContainer == null) return;
        _projectChatContainer.Clear();

        // Show only planning-phase messages — execution internal traffic is not useful to the user
        var planningMessages = _projectHistory.Take(_planningHistoryEnd > 0 ? _planningHistoryEnd : _projectHistory.Count);
        foreach (var msg in planningMessages)
        {
            if (msg.role == "user" || msg.role == "assistant")
                AddProjectMessage(msg.content, msg.role == "user");
        }

        // Restore file info label
        if (_plan != null && !string.IsNullOrEmpty(_plan.gddFileName) && _projectFileInfo != null)
        {
            _projectFileInfo.text = $"📄 {_plan.gddFileName}";
            _projectFileInfo.RemoveFromClassList("project-file-info--hidden");
        }

        // Restore start bar if plan ready
        if (_plan != null && (_plan.state == "plan_ready"))
            _projectStartBar?.RemoveFromClassList("project-start-bar--hidden");
    }

    private void UpdateProjectSendButton()
    {
        if (_projectSendBtn == null || _projectInputField == null) return;
        bool canSend = !string.IsNullOrEmpty(_projectInputField.value.Trim());
        _projectSendBtn.SetEnabled(canSend);
        _projectSendBtn.EnableInClassList("send-btn--disabled", !canSend);
    }

    private void SetProjectInputEnabled(bool enabled)
    {
        if (_projectInputField != null) _projectInputField.SetEnabled(enabled);
        if (_projectSendBtn    != null) _projectSendBtn.SetEnabled(enabled && !string.IsNullOrEmpty(_projectInputField?.value?.Trim()));
    }

    // ── Plan parsing ───────────────────────────────────────────────────────────

    private static ProjectPlanData TryParseProjectPlan(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("\"plan\"")) return null;

        // Strip markdown code block if present
        var stripped = text;
        var codeStart = text.IndexOf("```");
        if (codeStart >= 0)
        {
            var codeEnd = text.IndexOf("```", codeStart + 3);
            if (codeEnd > codeStart)
            {
                // Extract content between ``` markers, skip first line (```json)
                var inner = text.Substring(codeStart + 3, codeEnd - codeStart - 3);
                var nl = inner.IndexOf('\n');
                stripped = nl >= 0 ? inner.Substring(nl + 1) : inner;
            }
        }

        // Find opening { that contains "plan" key (handles whitespace/newlines)
        var start = stripped.IndexOf("{\"plan\"");
        if (start < 0) start = stripped.IndexOf("{ \"plan\"");
        if (start < 0)
        {
            // Claude may write {\n  "plan": — find first { and check if "plan" follows
            start = stripped.IndexOf('{');
            while (start >= 0)
            {
                var slice = stripped.Substring(start);
                if (slice.Contains("\"plan\"")) break;
                start = stripped.IndexOf('{', start + 1);
            }
        }
        if (start < 0) return null;

        var json = stripped.Substring(start);
        int depth = 0, end = -1;
        for (int i = 0; i < json.Length; i++)
        {
            if (json[i] == '{') depth++;
            else if (json[i] == '}') { depth--; if (depth == 0) { end = i; break; } }
        }
        if (end >= 0) json = json.Substring(0, end + 1);

        try
        {
            var wrapper = JsonUtility.FromJson<ProjectPlanWrapper>(json);
            return wrapper?.plan?.epics?.Length > 0 ? wrapper.plan : null;
        }
        catch { return null; }
    }

    // ── Execution ─────────────────────────────────────────────────────────────

    private void OnProjectStartBuilding()
    {
        if (_plan == null || _plan.epics == null) return;
        _plan.state = "executing";
        _planningHistoryEnd = _projectHistory.Count; // freeze planning chat display
        EditorPrefs.SetInt(ProjectPlanningEndPref, _planningHistoryEnd);
        SaveProjectPlan();

        ShowProjectBoardPhase();
        RebuildProjectBoard();
        UpdateProjectControls();

        _projectPauseRequested = false;
        _projectVerifying      = false;
        _projectExecuting      = true;
        EditorPrefs.DeleteKey(ProjectPausePref);

        EditorCoroutineUtility.StartCoroutineOwnerless(StartNextProjectTask());
    }

    private IEnumerator StartNextProjectTask()
    {
        if (_projectPauseRequested || EditorPrefs.GetBool(ProjectPausePref, false))
        {
            _projectPauseRequested = false;
            EditorPrefs.DeleteKey(ProjectPausePref);
            _plan.state = "paused";
            SaveProjectPlan();
            StopProjectLoading();
            SetProjectBoardStatus("⏸  Paused — press Resume to continue");
            UpdateProjectControls();
            _projectExecuting = false;
            yield break;
        }

        var next = GetNextPendingTask();
        if (next == null)
        {
            if (!_projectVerifying)
            {
                _projectVerifying = true;
                yield return PostProjectVerification();
            }
            else
            {
                CompleteProject();
            }
            yield break;
        }

        yield return PostProjectTask(next.id);
    }

    private IEnumerator PostProjectVerification()
    {
        SetProjectBoardStatus("🔍  Verifying all tasks...");
        StartProjectLoading("Verifying...");

        var allTasks = GetAllTasks().ToArray();
        var sb = new StringBuilder();
        sb.AppendLine("VERIFICATION PASS: All tasks are marked done. Please verify everything is correctly implemented.");
        sb.AppendLine("Task list:");
        foreach (var t in allTasks)
            sb.AppendLine($"- {t.name}: {t.description}");
        sb.AppendLine();
        sb.AppendLine("Use get_scene_info or read_script to check the actual state. Fix anything missing or incomplete. When everything is verified and correct, respond with task_complete:true.");

        yield return PostProjectRequest(sb.ToString(), VerificationTaskId, null);
    }

    private void CompleteProject(string extraNote = null)
    {
        _projectVerifying  = false;
        _projectExecuting  = false;
        _plan.state = "completed";
        SaveProjectPlan();
        StopProjectLoading();
        UpdateProjectControls();

        // Switch back to chat phase and show completion report
        ShowProjectChatPhase();

        // Build summary of completed tasks
        var allTasks = GetAllTasks();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("🎉 Project complete! Here's what was built:");
        sb.AppendLine();
        foreach (var task in allTasks)
            sb.AppendLine($"✅ {task.name}");
        if (!string.IsNullOrEmpty(extraNote))
        {
            sb.AppendLine();
            sb.AppendLine(extraNote);
        }

        AddProjectMessage(sb.ToString().Trim(), isUser: false);

        // Hide input, show "Load New Project" button
        _projectInputArea?.AddToClassList("project-input-area--hidden");
        _projectDoneBar?.RemoveFromClassList("project-start-bar--hidden");
    }

    private void OnNewProjectClicked()
    {
        // Delete saved plan and reset state
        if (File.Exists(ProjectPlanPath))
            File.Delete(ProjectPlanPath);
        AssetDatabase.Refresh();

        _plan = null;
        _projectHistory.Clear();
        _projectExecuting  = false;
        _projectVerifying  = false;
        _projectPauseRequested = false;
        EditorPrefs.DeleteKey(ProjectPausePref);
        EditorPrefs.DeleteKey(ProjectResumingPref);
        EditorPrefs.DeleteKey(ProjectResumeTaskPref);
        EditorPrefs.DeleteKey(ProjectExecutingPref);
        EditorPrefs.DeleteKey(ProjectExecutingTaskPref);
        EditorPrefs.DeleteKey(ProjectPlanningEndPref);
        _planningHistoryEnd = 0;

        // Restore input area and hide done bar
        _projectInputArea?.RemoveFromClassList("project-input-area--hidden");
        _projectDoneBar?.AddToClassList("project-start-bar--hidden");

        // Clear project chat and go back to idle splash
        if (_projectChatContainer != null) _projectChatContainer.Clear();
        if (_projectFileInfo != null) _projectFileInfo.text = "";
        _projectActiveArea?.AddToClassList("project-active-area--hidden");
        _projectIdleSplash?.RemoveFromClassList("project-idle-splash--hidden");

        ShowProjectChatPhase();
    }

    private IEnumerator PostProjectTask(int taskId)
    {
        var task = FindTask(taskId);
        if (task == null) yield break;

        bool isRetry = task.status == "in_progress";
        task.status = "in_progress";
        RefreshProjectBoard();
        SaveProjectPlan();
        SetProjectBoardStatus(isRetry ? $"🔁  Retrying: {task.name}" : $"🔄  {task.name}");
        StartProjectLoading(task.name);

        _taskHistoryStart = _projectHistory.Count; // per-task history window starts here
        EditorPrefs.SetBool(ProjectExecutingPref, true);
        EditorPrefs.SetInt(ProjectExecutingTaskPref, taskId);
        yield return PostProjectRequest(BuildTaskPrompt(task), taskId, null);
        EditorPrefs.DeleteKey(ProjectExecutingPref);
        EditorPrefs.DeleteKey(ProjectExecutingTaskPref);
    }

    private IEnumerator PostProjectRequest(string message, int taskId, string image)
    {
        if (!CheckClientRateLimit()) { MarkProjectTaskFailed(taskId, "Rate limit protection triggered"); yield break; }

        _projectHistory.Add(new HistoryMessage { role = "user", content = message });

        // Build project_files context with completed tasks list
        var projectFiles = _projectContext ?? "";
        var doneTasks = GetAllTasks().Where(t => t.status == "done").Select(t => t.name).ToArray();
        if (doneTasks.Length > 0)
            projectFiles += "\n\n=== COMPLETED TASKS ===\n" + string.Join("\n", doneTasks.Select(n => "✅ " + n));

        var taskHistory = _projectHistory
            .Skip(_taskHistoryStart)
            .Take(_projectHistory.Count - _taskHistoryStart - 1)
            .Where(h => h.role == "user" || h.role == "assistant")
            .ToArray();

        var payload = new ChatRequest
        {
            message       = message,
            project_files = projectFiles,
            project_path  = System.IO.Path.GetDirectoryName(Application.dataPath),
            mode          = "project_execution",
            image         = image ?? "",
            history       = taskHistory,
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
            MarkProjectTaskFailed(taskId, req.error);
            yield break;
        }

        var response = JsonUtility.FromJson<ChatResponse>(req.downloadHandler.text);
        if (response == null) { MarkProjectTaskFailed(taskId, "Invalid response from server"); yield break; }

        TrackTokenUsage(response.input_tokens, response.output_tokens);

        // ── Claude Code streaming path ─────────────────────────────────────
        if (response.type == "streaming")
        {
            EditorPrefs.SetString("Claunity_StreamResume", response.session_id);
            EditorPrefs.SetString(StreamContextPref, "project_execution");
            EditorPrefs.SetInt(StreamTaskIdPref, taskId);
            yield return PollStreamingForProjectExecution(response.session_id, taskId);
            EditorPrefs.DeleteKey("Claunity_StreamResume");
            EditorPrefs.DeleteKey(StreamContextPref);
            EditorPrefs.DeleteKey(StreamTaskIdPref);
            yield break;
        }

        // ── Agentic tool loop ──────────────────────────────────────────────
        int safetyLimit = 40;
        while (response.type == "tool_request" && safetyLimit-- > 0)
        {
            var toolName  = response.tool_name;
            var toolInput = response.tool_input_json ?? "{}";

            // Show Claude's narration as a project message
            if (!string.IsNullOrEmpty(response.narration))
                AddProjectMessage(response.narration, false);

            // Special workflow tools
            if (toolName == "task_complete")
            {
                MarkProjectTaskDone(taskId);
                yield break;
            }
            if (toolName == "ask_user")
            {
                // Extract question from tool input JSON
                var q = ExtractStringField(toolInput, "question");
                ShowProjectQuestion(taskId, q);
                yield break;
            }

            // Execute Unity tool
            var action     = BuildActionFromTool(toolName, toolInput);
            var toolResult = ClaunityActionExecutor.Execute(action);

            // Handle recompile → domain reload will resume via EditorPrefs
            if (toolName == "recompile_scripts")
            {
                EditorPrefs.SetBool(ProjectResumingPref, true);
                EditorPrefs.SetInt(ProjectResumeTaskPref, taskId);
                SaveProjectPlan();
                // Send result before yielding so session stays alive
                var recompileResult = toolResult ?? "✓ Recompile triggered";
                yield return SendToolContinue(response.session_id, response.tool_use_id, recompileResult,
                    r => response = r);
                yield break; // domain reload will resume
            }

            var resultStr = (toolResult != null && toolResult.StartsWith("[IMAGE:"))
                ? "[Screenshot captured]" : (toolResult ?? "✓ done");

            yield return SendToolContinue(response.session_id, response.tool_use_id, resultStr,
                r => response = r);

            if (response == null) { MarkProjectTaskFailed(taskId, "Lost server connection"); yield break; }
            TrackTokenUsage(response.input_tokens, response.output_tokens);
        }

        // ── Final text response ────────────────────────────────────────────
        var finalReply = response.reply ?? "";
        _projectHistory.Add(new HistoryMessage { role = "assistant", content = finalReply });
        SaveProjectPlan();

        // Any final text = task done (Claude used tools to execute, then wrote summary)
        MarkProjectTaskDone(taskId);
    }

    // Send /chat/continue and update response reference
    private IEnumerator SendToolContinue(string sessionId, string toolUseId, string toolResult,
        System.Action<ChatResponse> setResponse)
    {
        var payload = new ChatContinueRequest
        {
            session_id  = sessionId,
            tool_use_id = toolUseId,
            tool_result = toolResult,
        };
        var body = JsonUtility.ToJson(payload);
        using var req = new UnityWebRequest($"{ServerUrl}/chat/continue", "POST");
        req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 1200;
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            setResponse(null);
            yield break;
        }
        var next = JsonUtility.FromJson<ChatResponse>(req.downloadHandler.text);
        setResponse(next);
    }

    // Extract a string field from a simple JSON object (no full JSON parser needed)
    private static string ExtractStringField(string json, string field)
    {
        var key = "\"" + field + "\"";
        var idx = json.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return "";
        var colon = json.IndexOf(':', idx + key.Length);
        if (colon < 0) return "";
        var quote1 = json.IndexOf('"', colon + 1);
        if (quote1 < 0) return "";
        var quote2 = json.IndexOf('"', quote1 + 1);
        if (quote2 < 0) return "";
        return json.Substring(quote1 + 1, quote2 - quote1 - 1);
    }

    private string BuildTaskPrompt(ProjectTask task)
    {
        var allTasks = GetAllTasks().ToArray();
        var idx      = Array.IndexOf(allTasks, task) + 1;
        var sb       = new StringBuilder();
        sb.AppendLine($"Execute task {idx} of {allTasks.Length}: **{task.name}**");
        if (!string.IsNullOrEmpty(task.description))
            sb.AppendLine($"Description: {task.description}");
        sb.AppendLine();
        sb.AppendLine("Check the current project state first. Skip steps that are already done.");
        return sb.ToString().TrimEnd();
    }

    private void MarkProjectTaskDone(int taskId)
    {
        StopProjectLoading();
        if (taskId == VerificationTaskId)
        {
            CompleteProject();
            return;
        }
        var task = FindTask(taskId);
        if (task != null) task.status = "done";
        RefreshProjectBoard();
        SaveProjectPlan();
        EditorApplication.delayCall += () =>
            EditorCoroutineUtility.StartCoroutineOwnerless(StartNextProjectTask());
    }

    private void MarkProjectTaskFailed(int taskId, string error)
    {
        StopProjectLoading();
        if (taskId == VerificationTaskId)
        {
            _projectVerifying = false;
            _projectExecuting = false;
            // All tasks are done — complete the project even if verification call failed
            CompleteProject($"⚠️ Verification step failed ({error}), but all tasks were executed.");
            return;
        }
        var task = FindTask(taskId);
        if (task != null) task.status = "failed";
        RefreshProjectBoard();
        SaveProjectPlan();
        SetProjectBoardStatus($"❌  Task failed: {error}");
        _projectExecuting = false;
        UpdateProjectControls();
    }

    // ── Question block ─────────────────────────────────────────────────────────

    private int _projectQuestionTaskId = -1;

    private void ShowProjectQuestion(int taskId, string question)
    {
        StopProjectLoading();
        _projectQuestionTaskId = taskId;
        if (_projectQuestionText  != null) _projectQuestionText.text = $"❓ {question}";
        if (_projectAnswerField   != null) _projectAnswerField.value = "";
        _projectQuestionBlock?.RemoveFromClassList("project-question-block--hidden");
        _projectAnswerField?.Focus();
        SetProjectBoardStatus("💬  Claunity has a question");
    }

    private void HideProjectQuestion()
    {
        _projectQuestionBlock?.AddToClassList("project-question-block--hidden");
        _projectQuestionTaskId = -1;
    }

    private void OnProjectAnswerSubmit()
    {
        var answer = _projectAnswerField?.value?.Trim();
        if (string.IsNullOrEmpty(answer)) return;
        if (_projectAnswerField != null) _projectAnswerField.value = "";

        var taskId = _projectQuestionTaskId;
        HideProjectQuestion();

        _projectHistory.Add(new HistoryMessage { role = "user", content = answer });
        SetProjectBoardStatus($"🔄  Continuing...");
        EditorCoroutineUtility.StartCoroutineOwnerless(PostProjectRequest(answer, taskId, null));
    }

    // ── Pause / Stop ──────────────────────────────────────────────────────────

    private void OnProjectPauseClicked()
    {
        if (_plan == null) return;

        if (_plan.state == "paused" || _plan.state == "stopped")
        {
            // Resume
            _plan.state = "executing";
            SaveProjectPlan();
            _projectPauseRequested = false;
            EditorPrefs.DeleteKey(ProjectPausePref);
            _projectExecuting      = true;
            UpdateProjectControls();
            EditorCoroutineUtility.StartCoroutineOwnerless(StartNextProjectTask());
        }
        else
        {
            // Request pause after current task
            _projectPauseRequested = true;
            EditorPrefs.SetBool(ProjectPausePref, true);
            SetProjectBoardStatus("⏸  Pause requested — finishing current task...");
            // Immediately update button so user sees feedback right away
            if (_projectPauseBtn != null)
            {
                _projectPauseBtn.text = "⏸  Pausing...";
                _projectPauseBtn.SetEnabled(false);
            }
        }
    }

    private void OnProjectContinueClicked()
    {
        if (_plan == null || _plan.state != "executing") return;
        // Force-reset any stuck in_progress task back to pending so it gets retried
        foreach (var t in GetAllTasks())
            if (t.status == "in_progress") t.status = "pending";
        RefreshProjectBoard();
        SaveProjectPlan();
        _projectExecuting = true;
        UpdateProjectControls();
        EditorCoroutineUtility.StartCoroutineOwnerless(StartNextProjectTask());
    }

    private void UpdateProjectControls()
    {
        if (_plan == null) return;

        bool planReady = _plan.state == "plan_ready";
        bool canResume = _plan.state == "paused" || _plan.state == "stopped";
        bool executing = _plan.state == "executing";
        bool done      = _plan.state == "completed";

        if (_projectBoardStartBtn != null)
            _projectBoardStartBtn.EnableInClassList("project-board-start-btn--hidden", !planReady);

        if (_projectPauseBtn != null)
        {
            _projectPauseBtn.SetEnabled(!planReady && !done);
            if (canResume)
            {
                _projectPauseBtn.text = "▶  Resume";
                _projectPauseBtn.RemoveFromClassList("project-pause-btn");
                _projectPauseBtn.AddToClassList("project-resume-btn");
            }
            else
            {
                _projectPauseBtn.text = "⏸  Pause";
                _projectPauseBtn.RemoveFromClassList("project-resume-btn");
                _projectPauseBtn.AddToClassList("project-pause-btn");
            }
        }
        // Continue: available any time while executing (lets user unstick a hung task)
        if (_projectContinueBtn != null)
            _projectContinueBtn.SetEnabled(!planReady && executing && !done);
    }

    // ── Board rendering ───────────────────────────────────────────────────────

    private void RebuildProjectBoard()
    {
        if (_projectBoardContent == null || _plan?.epics == null) return;
        _projectBoardContent.Clear();
        _projectTaskRows.Clear();

        foreach (var epic in _plan.epics)
        {
            var header = new Label(epic.name.ToUpperInvariant());
            header.AddToClassList("project-epic-header");
            _projectBoardContent.Add(header);

            if (epic.tasks == null) continue;
            foreach (var task in epic.tasks)
            {
                var row = BuildTaskRow(task);
                _projectTaskRows[task.id] = row;
                _projectBoardContent.Add(row);
            }
        }
    }

    private void RefreshProjectBoard()
    {
        if (_projectTaskRows.Count == 0) { RebuildProjectBoard(); return; }
        if (_plan?.epics == null) return;

        foreach (var epic in _plan.epics)
        {
            if (epic.tasks == null) continue;
            foreach (var task in epic.tasks)
            {
                if (!_projectTaskRows.TryGetValue(task.id, out var row)) continue;
                UpdateTaskRow(row, task);
            }
        }
    }

    private static VisualElement BuildTaskRow(ProjectTask task)
    {
        var row = new VisualElement();
        row.AddToClassList("project-task-row");
        ApplyTaskRowStyle(row, task);

        var statusLbl = new Label(TaskStatusIcon(task.status));
        statusLbl.name = "task-status";
        statusLbl.AddToClassList("project-task-status");

        var nameLbl = new Label(task.name);
        nameLbl.name = "task-name";
        nameLbl.AddToClassList("project-task-name");
        ApplyTaskNameStyle(nameLbl, task.status);

        row.Add(statusLbl);
        row.Add(nameLbl);
        return row;
    }

    private static void UpdateTaskRow(VisualElement row, ProjectTask task)
    {
        var statusLbl = row.Q<Label>("task-status");
        var nameLbl   = row.Q<Label>("task-name");
        if (statusLbl != null) statusLbl.text = TaskStatusIcon(task.status);
        if (nameLbl   != null)
        {
            nameLbl.RemoveFromClassList("project-task-name--active");
            nameLbl.RemoveFromClassList("project-task-name--done");
            nameLbl.RemoveFromClassList("project-task-name--failed");
            ApplyTaskNameStyle(nameLbl, task.status);
        }
        ApplyTaskRowStyle(row, task);
    }

    private static void ApplyTaskRowStyle(VisualElement row, ProjectTask task)
    {
        row.EnableInClassList("project-task-row--active", task.status == "in_progress");
        row.EnableInClassList("project-task-row--done",   task.status == "done");
    }

    private static void ApplyTaskNameStyle(Label lbl, string status)
    {
        if (status == "in_progress") lbl.AddToClassList("project-task-name--active");
        else if (status == "done")   lbl.AddToClassList("project-task-name--done");
        else if (status == "failed") lbl.AddToClassList("project-task-name--failed");
    }

    private static string TaskStatusIcon(string status)
    {
        if (status == "done")        return "✅";
        if (status == "in_progress") return "🔄";
        if (status == "failed")      return "❌";
        return "⬜";
    }

    private void SetProjectBoardStatus(string text)
    {
        if (_projectBoardStatus != null) _projectBoardStatus.text = text;
    }

    private void StartProjectLoading(string taskName)
    {
        if (_projectLoadingBar == null) return;
        if (_projectLoadingText != null) _projectLoadingText.text = taskName;
        _projectLoadingBar.RemoveFromClassList("loading-bar--hidden");
        _projectLoadingStep = 0;
        _projectLoadingAnimation?.Pause();
        _projectLoadingAnimation = _projectLoadingBar.schedule
            .Execute(() =>
            {
                _projectLoadingStep = (_projectLoadingStep + 3) % 103;
                if (_projectLoadingFill != null)
                    _projectLoadingFill.style.width = Length.Percent(_projectLoadingStep);
            })
            .Every(50);
    }

    private void StopProjectLoading()
    {
        _projectLoadingAnimation?.Pause();
        _projectLoadingBar?.AddToClassList("loading-bar--hidden");
        if (_projectLoadingFill != null)
            _projectLoadingFill.style.width = Length.Percent(0);
    }

    private void ShowProjectThinking()
    {
        if (_projectThinkingIndicator == null) return;
        _projectDotFrame = 0;
        _projectThinkingIndicator.AddToClassList("thinking-indicator--visible");
        _projectThinkingAnimation?.Pause();
        _projectThinkingAnimation = _projectThinkingDots.schedule
            .Execute(() => _projectThinkingDots.text = DotFrames[_projectDotFrame++ % 3])
            .Every(500);
    }

    private void HideProjectThinking()
    {
        _projectThinkingAnimation?.Pause();
        _projectThinkingAnimation = null;
        _projectThinkingIndicator?.RemoveFromClassList("thinking-indicator--visible");
    }

    // ── Domain reload resume ──────────────────────────────────────────────────

    private void ResumeProjectAfterReload(int taskId)
    {
        LoadProjectPlan();
        if (_plan == null) return;

        if (_activeTab != "project") SwitchTab("project");

        ShowProjectBoardPhase();
        RebuildProjectBoard();

        _projectExecuting = true;
        _plan.state = "executing";
        SaveProjectPlan();

        SetProjectBoardStatus("⚙  Compilation done — resuming...");
        StartProjectLoading("Continuing...");
        UpdateProjectControls();

        // Don't restart the whole task — just tell Claude compilation succeeded and ask it to continue
        const string continueMsg = "Compilation succeeded. Continue with any remaining steps for this task, or confirm completion with task_complete: true.";
        EditorApplication.delayCall += () =>
            EditorCoroutineUtility.StartCoroutineOwnerless(PostProjectRequest(continueMsg, taskId, null));
    }

    private void HandleProjectCompileFailed(string[] errors)
    {
        bool resuming  = EditorPrefs.GetBool(ProjectResumingPref, false);
        bool executing = EditorPrefs.GetBool(ProjectExecutingPref, false);
        if (!resuming && !executing) return;

        EditorPrefs.DeleteKey(ProjectResumingPref);
        EditorPrefs.DeleteKey(ProjectExecutingPref);

        var taskId = resuming
            ? EditorPrefs.GetInt(ProjectResumeTaskPref, -1)
            : EditorPrefs.GetInt(ProjectExecutingTaskPref, -1);
        if (taskId < 0 || _plan == null) return;

        if (_activeTab != "project") SwitchTab("project");
        ShowProjectBoardPhase();
        RebuildProjectBoard();

        _projectExecuting = true;
        _plan.state = "executing";
        SaveProjectPlan();

        var errorList = string.Join("\n", errors.Select(e => $"• {e}"));
        var msg = $"Compilation failed with the following errors. Please fix them:\n{errorList}";

        SetProjectBoardStatus("❌  Compile errors — fixing...");
        StartProjectLoading("Fixing compile errors...");
        UpdateProjectControls();

        EditorCoroutineUtility.StartCoroutineOwnerless(PostProjectRequest(msg, taskId, null));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void EnsurePlan()
    {
        if (_plan == null)
            _plan = new ProjectPlanFile();
    }

    private ProjectTask FindTask(int id)
    {
        if (_plan?.epics == null) return null;
        foreach (var epic in _plan.epics)
            if (epic.tasks != null)
                foreach (var t in epic.tasks)
                    if (t.id == id) return t;
        return null;
    }

    private IEnumerable<ProjectTask> GetAllTasks()
    {
        if (_plan?.epics == null) yield break;
        foreach (var epic in _plan.epics)
            if (epic.tasks != null)
                foreach (var t in epic.tasks)
                    yield return t;
    }

    private ProjectTask GetNextPendingTask() =>
        // In-progress tasks take priority — they are stuck and need a retry
        GetAllTasks().FirstOrDefault(t => t.status == "in_progress")
        ?? GetAllTasks().FirstOrDefault(t => t.status == "pending");

    // ── Persistence ───────────────────────────────────────────────────────────

    private void SaveProjectPlan()
    {
        if (_plan == null) return;
        try
        {
            _plan.chatHistory = _projectHistory
                .Where(h => h.role == "user" || h.role == "assistant")
                .TakeLast(60)
                .Select(h => new HistoryMessage { role = h.role, content = h.content })
                .ToArray();

            var dir = System.IO.Path.GetDirectoryName(
                System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "../" + ProjectPlanPath)));
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var fullPath = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "../" + ProjectPlanPath));
            File.WriteAllText(fullPath, JsonUtility.ToJson(_plan, true));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Claunity] Could not save project plan: {e.Message}");
        }
    }

    private void LoadProjectPlan()
    {
        try
        {
            var fullPath = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "../" + ProjectPlanPath));
            if (!File.Exists(fullPath)) return;

            _plan = JsonUtility.FromJson<ProjectPlanFile>(File.ReadAllText(fullPath));
            if (_plan == null) return;

            _projectHistory.Clear();
            if (_plan.chatHistory != null)
                _projectHistory.AddRange(_plan.chatHistory);
            _planningHistoryEnd = EditorPrefs.GetInt(ProjectPlanningEndPref, _projectHistory.Count);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Claunity] Could not load project plan: {e.Message}");
        }
    }
}

}