using Blazma.AI;
using Blazma.Commands;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// D45 scenario: the motion prompt planner in the Generate dialog. Types the user's sentence «في واحد وراك اجري وبعدها موت», checks the
/// live (debounced) step list (look back → run → die, English captions, lengths, all understood), generates with the procedural provider
/// and checks that the draft clip has the three steps in that order. Then a step that is not understood is flagged and fixed by editing
/// its English text, a step length is edited, and the dialog is shown in Arabic (RTL). Screenshots plan-*.png. Shortcut: BLAZMA_ONLY_PLAN=1.
/// </summary>
public partial class SelfTest
{
    async Task RunPlan()
    {
        App.SetLanguage("en"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        _w.ShowAgentPanel(false);
        _w.NewProjectNow();
        await Settle();
        var dlg = _w.GenerateDlg;
        _w.OpenGenerateDialog();
        await Settle();
        dlg.SelectProvider(AiText.ProceduralId);
        dlg.Seed.Value = 5;
        dlg.Duration.Value = 6;

        // ---- typing: the plan follows the text after a short pause ---------------------------------------------------------
        const string sentence = "في واحد وراك اجري وبعدها موت";
        foreach (var cut in new[] { 6, 13, 18, sentence.Length })
        {
            dlg.Prompt.Text = sentence[..cut];
            dlg.Prompt.EmitSignal(TextEdit.SignalName.TextChanged);
            await Frames(2);
        }
        var planned = await WaitFor(() => dlg.PlanRows.Count == 3, 5);
        await Settle();
        Check("plan typing the user's sentence shows three steps in order: look back, run, die", () =>
        {
            if (!planned) return $"rows={dlg.PlanRows.Count}";
            var r = dlg.PlanRows;
            if (r[0].Text != "في واحد وراك" || r[1].Text != "اجري" || r[2].Text != "موت") return string.Join(" | ", r.Select(x => x.Text));
            if (!r[0].Caption.Contains("looks back") || r[1].Caption != "a person runs forward" || r[2].Caption != "a person falls to the ground and dies")
                return string.Join(" | ", r.Select(x => x.Caption));
            if (!r.All(x => x.Understood)) return "not understood";
            if (Math.Abs(r.Sum(x => x.Seconds) - 6) > 0.02 || !(r[1].Seconds > r[0].Seconds)) return string.Join(",", r.Select(x => x.Seconds));
            return dlg.PlanVisible && dlg.PlanSummary.Contains("3⁩ steps") && dlg.PlanStatuses.All(x => x == "Understood") ? null : dlg.PlanSummary + " / " + string.Join(",", dlg.PlanStatuses);
        });
        Shot(P("plan-en-dark-dialog"), dlg);

        // ---- generate: one draft clip, steps in order ------------------------------------------------------------------------
        _w.LastMotionApply = null;
        dlg.PressGenerate();
        var jobId = dlg.CurrentJobId;
        var applied = await WaitFor(() => _w.LastMotionApply is not null, 30);
        await Settle();
        Check("plan Generate makes ONE draft clip from the three steps, with a marker per step in order", () =>
        {
            if (!applied || jobId is null) return "not applied: " + _w.LastMotionApplyError + " " + App.MotionJobs.Get(jobId ?? "")?.Message;
            var snap = App.MotionJobs.Get(jobId)!;
            if (!snap.Segments.Select(x => x.Action).SequenceEqual(new[] { "look_back", "run", "die" })) return "segments: " + string.Join(",", snap.Segments.Select(x => x.Action));
            var clip = Proj.Clips.Find(c => c.Id.ToString() == _w.LastMotionApply!.ClipId);
            if (clip is not { IsDraft: true }) return "no draft";
            var markers = clip.Markers.Select(m => m.Name).ToList();
            if (!markers.SequenceEqual(new[] { "1. في واحد وراك", "2. اجري", "3. موت" })) return "markers: " + string.Join(" | ", markers);
            if (Math.Abs(clip.Duration.ToSeconds() - 6) > 0.2) return "duration " + clip.Duration.ToSeconds();
            return dlg.DetailsText.Contains("Steps in the clip:") && dlg.DetailsText.Contains("look back") ? null : dlg.DetailsText;
        });
        Shot(P("plan-en-dark-done"), dlg);
        dlg.Hide();
        await Settle();

        // ---- a step that is not understood, fixed by editing; editing a length --------------------------------------------
        _w.OpenGenerateDialog();
        await Settle();
        dlg.Prompt.Text = "يمشي ثم يتكلم في التلفون";
        dlg.Prompt.EmitSignal(TextEdit.SignalName.TextChanged);
        dlg.RefreshPlanNow();
        await Settle();
        Check("plan a step the dictionary does not know is flagged 'Not understood' with a hint", () =>
            dlg.PlanRows.Count == 2 && dlg.PlanRows[0].Understood && !dlg.PlanRows[1].Understood && dlg.PlanStatuses[1] == "Not understood" && dlg.PlanWarning.Length > 0
                ? null : string.Join(",", dlg.PlanStatuses) + " / " + dlg.PlanWarning);
        dlg.EditStep(1, caption: "a person talks on the phone");
        dlg.EditStep(0, seconds: 2.5);
        await Settle();
        Check("plan editing the English text marks the step 'Edited' (understood); editing a length updates the total", () =>
            dlg.PlanRows[1].Understood && dlg.PlanStatuses[1] == "Edited" && dlg.PlanWarning.Length == 0
            && Math.Abs(dlg.Duration.Value - dlg.PlanRows.Sum(x => x.Seconds)) < 1e-6 && Math.Abs(dlg.PlanRows[0].Seconds - 2.5) < 1e-6
                ? null : $"{string.Join(",", dlg.PlanStatuses)} total={dlg.Duration.Value} rows={string.Join(",", dlg.PlanRows.Select(x => x.Seconds))}");

        // ---- Arabic: RTL, translated labels, numbers left to right ----------------------------------------------------------
        App.SetLanguage("ar");
        await Settle();
        dlg.Prompt.Text = sentence;
        dlg.Prompt.EmitSignal(TextEdit.SignalName.TextChanged);
        dlg.RefreshPlanNow();
        await Settle();
        Check("plan Arabic: the step list is translated (summary, statuses) and the dialog is RTL", () =>
            App.IsRtl && HasArabic(dlg.PlanSummary) && dlg.PlanSummary.Contains("⁦") && dlg.PlanStatuses.All(HasArabic) ? null : dlg.PlanSummary + " / " + string.Join(",", dlg.PlanStatuses));
        Shot(P("plan-ar-dark-dialog"), dlg);
        dlg.Hide();
        App.SetLanguage("en");
        await Settle();
    }
}
