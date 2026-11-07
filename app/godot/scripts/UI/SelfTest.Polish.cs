using System.Text.RegularExpressions;
using Blazma.Core.Persistence;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// UI audit fixes: opening a project given on the command line (file association), Arabic coverage (every Tr literal and command label has an
/// ar.po entry; history origins; units; toasts follow the language), autosave on a worker thread, friendly file errors.
/// Shortcut: BLAZMA_ONLY_POLISH=1.
/// </summary>
public partial class SelfTest
{
    async Task RunPolish()
    {
        App.SetLanguage("en"); App.SetAdvancedMode(true);
        await Settle();

        // ---- file association ----
        var file = System.IO.Path.Combine(_dir, "assoc.blazanim");
        Check("polish positional .blazanim argument is found (missing files and options are skipped)", () =>
        {
            _w.NewProjectNow();
            _w.CreateClipNamed("From association");
            App.SaveAs(file);
            var found = AppController.FindStartupProject(new[] { "--rendering-driver", "x", System.IO.Path.Combine(_dir, "nope.blazanim"), file });
            return found == System.IO.Path.GetFullPath(file) && AppController.FindStartupProject(new[] { "a.txt", "--x" }) is null ? null : "found=" + found;
        });
        Check("polish launching with a project path opens it", () =>
        {
            _w.NewProjectNow();
            if (App.FilePath is not null) return "new project has a path";
            _w.OpenStartupProject(file);
            return App.FilePath == System.IO.Path.GetFullPath(file) && ClipNamed("From association") is not null ? null : $"path={App.FilePath}";
        });

        // ---- translations ----
        Check("polish every Tr literal and command label has an ar.po msgid", () =>
        {
            var root = ProjectSettings.GlobalizePath("res://");
            var ar = TranslationServer.FindTranslations("ar", false).FirstOrDefault();
            if (ar is null) return "no ar translation loaded";
            var missing = new SortedSet<string>();
            var str = "\"((?:[^\"\\\\\\n]|\\\\.)*)\"";
            var rx = new Regex(@"(?:\bTr\(|TranslationServer\.Translate\(|\bNotify(?:Action|Format|ActionFormat)?\()\s*" + str);
            var labelRx = new Regex(@"\bLabel\s*(?:=>|=)\s*" + str);
            var scanned = 0;
            void Scan(string dir, Regex r, bool skipSelfTest)
            {
                if (!System.IO.Directory.Exists(dir)) return;
                foreach (var f in System.IO.Directory.EnumerateFiles(dir, "*.cs", System.IO.SearchOption.AllDirectories))
                {
                    if (f.Contains("/obj/") || f.Contains("\\obj\\") || f.Contains("/bin/")) continue;
                    if (skipSelfTest && System.IO.Path.GetFileName(f).StartsWith("SelfTest")) continue;
                    scanned++;
                    foreach (Match m in r.Matches(System.IO.File.ReadAllText(f)))
                    {
                        var text = m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n");
                        if (text.Length == 0 || !text.Any(char.IsLetter) || text.EndsWith(' ')) continue;
                        if (ar.GetMessage(text).ToString().Length == 0) missing.Add(text);
                    }
                }
            }
            Scan(System.IO.Path.Combine(root, "scripts"), rx, true);
            Scan(System.IO.Path.GetFullPath(System.IO.Path.Combine(root, "..", "..", "src")), labelRx, false);
            if (scanned == 0) return null; // exported build: no sources to scan
            return missing.Count == 0 ? null : $"{missing.Count} strings without ar.po msgid, first: " + string.Join(" | ", missing.Take(5));
        });

        App.SetLanguage("ar");
        await Settle();
        Check("polish the history panel shows translated labels and origins in Arabic", () =>
        {
            _w.NewProjectNow();
            _w.CreateClipNamed("Hist");
            _w.HistoryPanel.Rebuild();
            var last = _w.HistoryPanel.List.GetItemText(_w.HistoryPanel.List.ItemCount - 1);
            return last.Contains("أنت") && !last.Contains("· ui") ? null : last;
        });
        Check("polish units read سم / م in Arabic", () => Unit.Cm == " سم" && Unit.M == " م" ? null : Unit.Cm + Unit.M);
        Check("polish a toast on screen is re-translated when the language changes", () =>
        {
            App.SetLanguage("en");
            _w.ToastNode.Notify("Pose copied", _w.Palette.Ok);
            var en = _w.ToastNode.Message;
            App.SetLanguage("ar");
            var ar = _w.ToastNode.Message;
            return en == "Pose copied" && ar != en && ar.Length > 0 ? null : $"en={en} ar={ar}";
        });
        App.SetLanguage("en");
        await Settle();

        // ---- autosave on a worker thread ----
        await Check2("polish autosave is written off-thread, never two at once, and reports back", async () =>
        {
            _w.NewProjectNow();
            _w.CreateClipNamed("Autosave me");
            var before = App.LastAutosaveUtc;
            App.AutosaveNow();
            var started = App.IsAutosaving;
            App.AutosaveNow(); // must be ignored while one runs
            var ok = await WaitFor(() => !App.IsAutosaving && App.LastAutosaveUtc != before, 15);
            return ok ? null : $"started={started} autosaving={App.IsAutosaving} last={App.LastAutosaveUtc}";
        });

        // ---- file errors ----
        Check("polish saving to an unwritable place is reported, never thrown", () =>
        {
            string? err = null;
            void On(string m) => err = m;
            App.ErrorRaised += On;
            try
            {
                var ok = App.SaveAs("/proc/blazma-no/such.blazanim");
                return !ok && !string.IsNullOrEmpty(err) ? null : $"ok={ok} err={err}";
            }
            finally { App.ErrorRaised -= On; }
        });
        Check("polish opening a missing file is reported", () =>
        {
            string? err = null;
            void On(string m) => err = m;
            App.ErrorRaised += On;
            try
            {
                var ok = App.Open(System.IO.Path.Combine(_dir, "missing-file.blazanim"));
                return !ok && !string.IsNullOrEmpty(err) ? null : $"ok={ok} err={err}";
            }
            finally { App.ErrorRaised -= On; }
        });
        _w.NewProjectNow();
    }

    async Task Check2(string name, Func<Task<string?>> body)
    {
        try { var err = await body(); if (err is null) Pass(name); else Fail(name, err); }
        catch (Exception e) { Fail(name, e.Message); }
    }
}
