using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>What the last automatic "job ready, add it" step did (kept for the status text and tests).</summary>
public sealed record MotionApplyInfo(string JobId, string ClipId, string ClipName, IReadOnlyList<string> Warnings);

/// <summary>
/// Phase 7 part of the shell: the Generate motion dialog and AI settings, the menu entries, applying finished jobs on the main thread
/// and accepting or discarding draft clips. Job events arrive on the main thread (AppController posts them).
/// </summary>
public partial class MainWindow
{
    const int MGenMotion = 93, MAiSettings = 94, MAcceptDraft = 95, MDiscardDraft = 96, MAiModels = 98;

    public GenerateMotionDialog GenerateDlg { get; private set; } = null!;
    public AiSettingsDialog AiSettingsDlg { get; private set; } = null!;
    public AiModelsDialog AiModelsDlg { get; private set; } = null!;
    public MotionApplyInfo? LastMotionApply { get; set; }
    public string? LastMotionApplyError { get; private set; }

    void BuildMotionDialogs()
    {
        GenerateDlg = new GenerateMotionDialog(this);
        AddChild(GenerateDlg);
        AiSettingsDlg = new AiSettingsDialog(this);
        AddChild(AiSettingsDlg);
        AiModelsDlg = new AiModelsDialog(this);
        AddChild(AiModelsDlg);
    }

    void BuildMotionMenu()
    {
        _mAnim.AddSeparator();
        _mAnim.AddItem("Generate Motion…", MGenMotion);
        _mAnim.AddItem("Accept Draft", MAcceptDraft);
        _mAnim.AddItem("Discard Draft", MDiscardDraft);
    }

    void BuildMotionSettingsMenu()
    {
        _mSettings.AddItem("Motion Generation (AI)…", MAiSettings);
        _mSettings.AddItem("AI Models…", MAiModels);
    }

    void OnMotionMenu(int id)
    {
        switch (id)
        {
            case MGenMotion: OpenGenerateDialog(); break;
            case MAiSettings: OpenAiSettings(); break;
            case MAiModels: OpenAiModels(); break;
            case MAcceptDraft: if (SelectedClip is { IsDraft: true } a) AcceptDraft(a.Id.ToString()); break;
            case MDiscardDraft: if (SelectedClip is { IsDraft: true } d) DiscardDraft(d.Id.ToString()); break;
        }
    }

    void UpdateMotionMenu()
    {
        if (_mAnim is null || GenerateDlg is null) return;
        var draft = SelectedClip is { IsDraft: true };
        _mAnim.SetItemDisabled(_mAnim.GetItemIndex(MGenMotion), App.Session.Project.Characters.Count == 0);
        _mAnim.SetItemDisabled(_mAnim.GetItemIndex(MAcceptDraft), !draft);
        _mAnim.SetItemDisabled(_mAnim.GetItemIndex(MDiscardDraft), !draft);
    }

    // ---- dialogs ------------------------------------------------------------------------------------------------------

    /// <summary>Open the Generate motion dialog (Animation menu, timeline button).</summary>
    public void OpenGenerateDialog()
    {
        if (GenerateDlg.Visible) return;
        if (App.Session.Project.Characters.Count == 0)
        {
            _toast.Notify(Tr("Add a character first, then generate motion for it."), _palette.Warn);
            return;
        }
        Inspector.Flush();
        GenerateDlg.Open();
    }

    public void OpenAiModels()
    {
        if (AiModelsDlg.Visible) return;
        AiModelsDlg.Open();
    }

    public void OpenAiSettings()
    {
        if (AiSettingsDlg.Visible) return;
        AiSettingsDlg.Open();
    }

    // ---- jobs ---------------------------------------------------------------------------------------------------------

    void OnMotionJobChanged(MotionJobSnapshot s)
    {
        if (s.State == MotionJobState.Ready && App.TakeAutoApply(s.JobId))
        {
            ApplyGenerated(s.JobId);
            return;
        }
        if (App.IsUiJob(s.JobId))
        {
            if (s.State == MotionJobState.Failed && !GenerateDlg.Visible)
                _toast.Notify(AiText.Failure(s.ErrorCode, s.Message), _palette.Err);
            return;
        }
        // A job an agent started: it applies it itself; just tell the user when the draft shows up.
        if (s.State == MotionJobState.Applied)
            _toast.Notify(string.Format(CultureInfo.InvariantCulture, Tr("An AI agent added a draft clip: {0}"), s.ClipName), _palette.Ok);
    }

    /// <summary>Adds a finished job as a draft clip, selects it and starts playing it. Returns the clip id.</summary>
    public string? ApplyGenerated(string jobId)
    {
        Inspector.Flush();
        LastMotionApplyError = null;
        var r = ExecuteQuiet("motion.job_apply", new JsonObject { ["jobId"] = jobId });
        if (!r.Ok)
        {
            var msg = r.Error?.Code == "rig_changed"
                ? Tr("The character's rig was changed while the motion was being generated. Generate it again.")
                : string.Format(CultureInfo.InvariantCulture, Tr("The generated motion could not be added: {0}"), r.Error?.Message ?? "");
            LastMotionApplyError = msg;
            _toast.Notify(msg, _palette.Err);
            return null;
        }
        var clipId = (string)r.Data!["clipId"]!;
        var name = (string?)r.Data["name"] ?? "";
        var warnings = r.Warnings.Select(w => w.Message).ToList();
        SelectClip(clipId);
        App.GoToStart();
        App.Play();
        LastMotionApply = new MotionApplyInfo(jobId, clipId, name, warnings);
        GenerateDlg.ShowApplied(jobId, name, warnings);
        _toast.Notify(warnings.Count > 0
            ? string.Format(CultureInfo.InvariantCulture, Tr("Draft added: {0} ({1} notes)"), name, warnings.Count)
            : string.Format(CultureInfo.InvariantCulture, Tr("Draft added: {0}"), name), _palette.Ok);
        return clipId;
    }

    // ---- drafts -------------------------------------------------------------------------------------------------------

    public bool AcceptDraft(string clipId)
    {
        var ok = ExecuteQuiet("clip.accept_draft", new JsonObject { ["clipId"] = clipId }).Ok;
        if (ok) _toast.Notify(Tr("Draft accepted. It is now a normal clip."), _palette.Ok);
        return ok;
    }

    /// <summary>Removes a draft clip (clip.delete, one undo step). The toast offers Undo.</summary>
    public bool DiscardDraft(string clipId)
    {
        if (App.Playing) App.Pause();
        var ok = DeleteClipNow(clipId);
        if (ok) _toast.NotifyAction(Tr("Draft discarded."), _palette.Warn, Tr("Undo"), DoUndo);
        return ok;
    }
}
