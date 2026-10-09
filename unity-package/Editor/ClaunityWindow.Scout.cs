using System;
using System.Collections;
using System.Collections.Generic;
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

    // ── Play & Test Mode ───────────────────────────────────────────────────────

    private void StartTestRun()
    {
        if (ClaunityTestRunner.IsPending) return;
        SetTestButtonsEnabled(false);
        ShowTestLoading("Entering Play Mode...");
        ClaunityTestRunner.StartTest();
    }

    private void HandleTestStatus(string status)
    {
        if (_testLoadingText != null) _testLoadingText.text = status;
        if (_testContent == null) return;
        var lbl = _testContent.Q<UnityEngine.UIElements.Label>();
        if (lbl != null) lbl.text = status;
    }

    private void HandleTestComplete(string screenshot, string consoleData)
    {
        if (_activeTab != "test") SwitchTab("test");

        if (_testContent != null)
        {
            _testContent.Clear();
            var lbl = new Label("🔍 Analyzing...");
            lbl.AddToClassList("test-run-header");
            _testContent.Add(lbl);
        }

        _history.Clear();

        var message = "Analyze the game test results: review the screenshot and console output, provide a full bug report.";
        _history.Add(new HistoryMessage { role = "user", content = message });
        _requestTab = _activeTab;

        _waitingForResponse = true;
        UpdateSendButton();

        var context = _projectContext ?? "";
        if (!string.IsNullOrEmpty(consoleData))
            context += "\n\n" + consoleData;
        if (screenshot == null)
            context += "\n\n[NOTE: Game View screenshot could not be captured — no active camera found]";

        EditorCoroutineUtility.StartCoroutineOwnerless(PostChat(message, isContinuation: true, context: context, image: screenshot));
    }

    // ── Play & Test Mode Dashboard ─────────────────────────────────────────────

    private void BuildTestDashboard(string reportText)
    {
        if (_testContent == null) return;
        HideTestLoading();
        _testContent.Clear();

        _testEmptySplash?.AddToClassList("test-empty-splash--hidden");
        _testScroll?.RemoveFromClassList("test-scroll--hidden");

        _testRunCount++;
        EditorPrefs.SetInt("Claunity_TestRunCount", _testRunCount);

        var header = new Label($"Test #{_testRunCount} — {DateTime.Now:d MMM yyyy, HH:mm}");
        header.AddToClassList("test-run-header");
        _testContent.Add(header);

        var report = ParseTestReport(reportText);

        if (!string.IsNullOrEmpty(report.working))
        {
            var (card, body) = MakeTestCard("✅ Working", "test-card--working");
            var lbl = new Label(report.working); lbl.AddToClassList("test-card-text");
            body.Add(lbl);
            _testContent.Add(card);
        }

        if (report.errors.Count > 0)
        {
            var (card, body) = MakeTestCard($"❌ Errors ({report.errors.Count})", "test-card--errors");
            foreach (var e in report.errors) { var l = new Label($"• {e}"); l.AddToClassList("test-issue-item"); body.Add(l); }
            var fp = "Fix these errors found during game testing:\n" + string.Join("\n", report.errors.Select(e => $"- {e}"));
            var fb = new Button(() => SendFeatureMessage(fp)); fb.text = "🔧 Fix all";
            fb.AddToClassList("test-card-btn"); fb.AddToClassList("test-card-btn--fix");
            body.Add(fb);
            _testContent.Add(card);
        }

        if (report.warnings.Count > 0)
        {
            var (card, body) = MakeTestCard($"⚠️ Warnings ({report.warnings.Count})", "test-card--warnings");
            foreach (var w in report.warnings) { var l = new Label($"• {w}"); l.AddToClassList("test-issue-item"); body.Add(l); }
            var wp = "Fix these warnings found during game testing:\n" + string.Join("\n", report.warnings.Select(w => $"- {w}"));
            var wb = new Button(() => SendFeatureMessage(wp)); wb.text = "🔧 Fix all";
            wb.AddToClassList("test-card-btn"); wb.AddToClassList("test-card-btn--fix");
            body.Add(wb);
            _testContent.Add(card);
        }

        if (!string.IsNullOrEmpty(report.visual))
        {
            var (card, body) = MakeTestCard("📸 Visual", "test-card--visual");
            var lbl = new Label(report.visual); lbl.AddToClassList("test-card-text");
            body.Add(lbl);
            _testContent.Add(card);
        }

        if (report.recommendations.Count > 0)
        {
            var (card, body) = MakeTestCard($"💡 Recommendations ({report.recommendations.Count})", "test-card--recs");
            foreach (var rec in report.recommendations)
            {
                var prompt = rec[1];
                var sub = new VisualElement(); sub.AddToClassList("test-rec-item");
                var tl = new Label(rec[0]); tl.AddToClassList("test-rec-title"); sub.Add(tl);
                var rb = new Button(() => SendFeatureMessage(prompt)); rb.text = "▶ Do this";
                rb.AddToClassList("test-card-btn"); sub.Add(rb);
                body.Add(sub);
            }
            _testContent.Add(card);
        }

        if (_testRunBarBtn != null) _testRunBarBtn.text = "🔁 Run Test Again";
    }

    private static TestReport ParseTestReport(string text)
    {
        var r = new TestReport();
        string current = null;
        var lines = new List<string>();

        void Flush()
        {
            if (current == null) return;
            var content = string.Join("\n", lines).Trim();
            switch (current)
            {
                case "WORKING": r.working = content; break;
                case "VISUAL":  r.visual  = content; break;
                case "ERRORS":
                    r.errors.AddRange(content.Split('\n')
                        .Select(l => l.TrimStart('-', '*', ' ', '\u2022'))
                        .Where(l => l.Length > 2 && !string.Equals(l, "None", StringComparison.OrdinalIgnoreCase)));
                    break;
                case "WARNINGS":
                    r.warnings.AddRange(content.Split('\n')
                        .Select(l => l.TrimStart('-', '*', ' ', '\u2022'))
                        .Where(l => l.Length > 2 && !string.Equals(l, "None", StringComparison.OrdinalIgnoreCase)));
                    break;
                case "RECOMMENDATIONS":
                    foreach (var line in content.Split('\n'))
                    {
                        var m = Regex.Match(line.Trim(), @"^\d+\.\s*\[([^\]]+)\]\s*(.+)$");
                        if (m.Success)
                            r.recommendations.Add(new[] { m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim() });
                        else
                        {
                            var s = Regex.Replace(line.Trim(), @"^\d+\.\s*", "").TrimStart('-', ' ');
                            if (s.Length > 3)
                                r.recommendations.Add(new[] { s.Length > 50 ? s.Substring(0, 50) + "…" : s, s });
                        }
                    }
                    break;
            }
            lines.Clear();
        }

        foreach (var line in text.Split('\n'))
        {
            var up = line.Trim().ToUpperInvariant();
            if (up == "WORKING:" || up == "ERRORS:" || up == "WARNINGS:" || up == "VISUAL:" || up == "RECOMMENDATIONS:")
            { Flush(); current = up.TrimEnd(':'); }
            else lines.Add(line);
        }
        Flush();
        return r;
    }

    private static (VisualElement card, VisualElement body) MakeTestCard(string title, string modifier)
    {
        var card = new VisualElement(); card.AddToClassList("test-card"); card.AddToClassList(modifier);
        var t = new Label(title); t.AddToClassList("test-card-title"); card.Add(t);
        var body = new VisualElement(); body.AddToClassList("test-card-body"); card.Add(body);
        return (card, body);
    }

    private void SetTestButtonsEnabled(bool enabled)
    {
        _testRunBarBtn?.SetEnabled(enabled);
    }

    private void ShowTestLoading(string text = "Running test...")
    {
        if (_testLoadingBar == null) return;
        if (_testLoadingText != null) _testLoadingText.text = text;
        _testLoadingBar.RemoveFromClassList("test-loading-bar--hidden");
        _testRunBarBtn?.AddToClassList("test-run-bar-btn--hidden");
        _testLoadingStep = 0;
        _testLoadingAnimation?.Pause();
        _testLoadingAnimation = _testLoadingFill?.schedule
            .Execute(() =>
            {
                _testLoadingStep = (_testLoadingStep + 1) % 40;
                if (_testLoadingFill != null)
                    _testLoadingFill.style.width = Length.Percent(_testLoadingStep * 2.5f);
            })
            .Every(50);
    }

    private void HideTestLoading()
    {
        _testLoadingAnimation?.Pause();
        _testLoadingAnimation = null;
        _testLoadingBar?.AddToClassList("test-loading-bar--hidden");
        _testRunBarBtn?.RemoveFromClassList("test-run-bar-btn--hidden");
    }

    private void ShowScoutLoading()
    {
        _scoutLoading?.RemoveFromClassList("scout-loading--hidden");

        _scoutLoadingStep = 0;
        _scoutLoadingAnimation?.Pause();
        _scoutLoadingAnimation = _scoutLoadingFill?.schedule
            .Execute(() =>
            {
                _scoutLoadingStep = (_scoutLoadingStep + 1) % 40;
                if (_scoutLoadingFill != null)
                    _scoutLoadingFill.style.width = Length.Percent(_scoutLoadingStep * 2.5f);
            })
            .Every(50);

        _scoutStatusStep = 0;
        if (_scoutLoadingText != null)
            _scoutLoadingText.text = ScoutStatusMessages[0];
        _scoutStatusAnimation?.Pause();
        _scoutStatusAnimation = _scoutLoading?.schedule
            .Execute(() =>
            {
                _scoutStatusStep = (_scoutStatusStep + 1) % ScoutStatusMessages.Length;
                if (_scoutLoadingText != null)
                    _scoutLoadingText.text = ScoutStatusMessages[_scoutStatusStep];
            })
            .Every(5000);
    }

    private void HideScoutLoading()
    {
        _scoutLoadingAnimation?.Pause();
        _scoutLoadingAnimation = null;
        _scoutStatusAnimation?.Pause();
        _scoutStatusAnimation = null;
        _scoutLoading?.AddToClassList("scout-loading--hidden");
    }

    // ── Scout ──────────────────────────────────────────────────────────────────

    private void OnScoutSearch()
    {
        var description = _scoutInputField?.value?.Trim();
        if (string.IsNullOrEmpty(description)) return;

        _scoutIdleSplash?.AddToClassList("project-idle-splash--hidden");
        _scoutScroll?.AddToClassList("scout-scroll--hidden");
        _scoutResults?.Clear();
        ShowScoutLoading();
        _scoutSearchBtn?.SetEnabled(false);

        EditorCoroutineUtility.StartCoroutineOwnerless(ScoutCoroutine(description, _scoutFreeOnly?.value ?? false));
    }

    private IEnumerator ScoutCoroutine(string description, bool freeOnly)
    {
        var payload = new ScoutRequest { description = description, free_only = _scoutFreeOnly?.value ?? false };
        var bytes   = Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload));

        using var req = new UnityWebRequest($"{ServerUrl}/scout", "POST");
        req.uploadHandler   = new UploadHandlerRaw(bytes);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 1200;
        yield return req.SendWebRequest();

        HideScoutLoading();
        _scoutSearchBtn?.SetEnabled(true);

        if (req.result != UnityWebRequest.Result.Success)
        {
            var errEl = new Label($"Search failed: {req.error}");
            errEl.AddToClassList("scout-error-label");
            _scoutResults?.Add(errEl);
            _scoutScroll?.RemoveFromClassList("scout-scroll--hidden");
            yield break;
        }

        ScoutResponse resp;
        try { resp = JsonUtility.FromJson<ScoutResponse>(req.downloadHandler.text); }
        catch { resp = null; }

        if (resp == null || resp.assets == null || resp.assets.Length == 0)
        {
            var emptyEl = new Label("No assets found.\nTry being more specific (e.g. \"low-poly medieval village\") or use broader terms.");
            emptyEl.AddToClassList("scout-error-label");
            _scoutResults?.Add(emptyEl);
            _scoutScroll?.RemoveFromClassList("scout-scroll--hidden");
            yield break;
        }

        var byCategory = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<ScoutAsset>>();
        foreach (var a in resp.assets)
        {
            var cat = string.IsNullOrEmpty(a.category) ? "General" : a.category;
            if (!byCategory.ContainsKey(cat))
                byCategory[cat] = new System.Collections.Generic.List<ScoutAsset>();
            byCategory[cat].Add(a);
        }

        foreach (var kv in byCategory)
        {
            var catLabel = new Label(kv.Key.ToUpper());
            catLabel.AddToClassList("scout-category-label");
            _scoutResults?.Add(catLabel);

            foreach (var asset in kv.Value)
            {
                var card = new VisualElement();
                card.AddToClassList("scout-card");

                var info = new VisualElement();
                info.AddToClassList("scout-card-info");

                var nameLabel = new Label(asset.name);
                nameLabel.AddToClassList("scout-card-name");
                info.Add(nameLabel);


                var btnRow = new VisualElement();
                btnRow.AddToClassList("scout-card-btn-row");

                var priceLabel = new Label(string.IsNullOrEmpty(asset.price) ? "" : asset.price);
                priceLabel.AddToClassList("scout-card-price");
                if (asset.price == "Free") priceLabel.AddToClassList("scout-card-price--free");

                var assetUrl = asset.url;
                var openBtn = new Button(() => Application.OpenURL(assetUrl));
                openBtn.text = "Open →";
                openBtn.AddToClassList("scout-add-btn");

                btnRow.Add(priceLabel);
                btnRow.Add(openBtn);

                card.Add(info);
                card.Add(btnRow);
                _scoutResults?.Add(card);
            }
        }

        _scoutScroll?.RemoveFromClassList("scout-scroll--hidden");
    }
}

}