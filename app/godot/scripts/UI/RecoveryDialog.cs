using System.Globalization;
using Blazma.Core.Persistence;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>Startup dialog listing projects with autosaved changes from a session that did not close cleanly.</summary>
public partial class RecoveryDialog : AcceptDialog
{
    readonly List<RecoveryCandidate> _candidates;
    readonly List<Control> _rows = new();
    readonly VBoxContainer _list = new();

    public IReadOnlyList<RecoveryCandidate> Candidates => _candidates;
    public IReadOnlyList<string> RowTexts => _rows.Select(r => string.Join(" | ", r.FindChildren("*", "Label", true, false).Cast<Label>().Select(l => l.Text))).ToList();

    public RecoveryDialog(IReadOnlyList<RecoveryCandidate> candidates)
    {
        _candidates = candidates.ToList();
        Title = "Recover unsaved work";
        Exclusive = true;
        Transient = true;
        OkButtonText = "Decide later";
        Unresizable = false;
        MinSize = new Vector2I(560, 0);

        var box = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 10);
        var intro = W.Label("Blazma Animation did not close properly last time. These projects have autosaved changes you can recover.", "Dim");
        intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        intro.CustomMinimumSize = new Vector2(520, 0);
        box.AddChild(intro);
        _list.AddThemeConstantOverride("separation", 8);
        box.AddChild(_list);
        AddChild(box);

        for (var i = 0; i < _candidates.Count; i++) AddRow(i);
    }

    void AddRow(int index)
    {
        var c = _candidates[index];
        var row = new PanelContainer { ThemeTypeVariation = "Row" };
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 10);

        var info = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        info.AddThemeConstantOverride("separation", 2);
        var name = W.Label(c.ProjectName, "Heading", false);
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        info.AddChild(name);
        var path = c.ProjectPath ?? (string)TranslationServer.Translate("Unsaved project");
        var pathLabel = W.Label(path, "Faint", false);
        pathLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        pathLabel.TextDirection = Control.TextDirection.Auto;
        info.AddChild(pathLabel);
        var when = c.AutosaveUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var whenLabel = W.Label(string.Format(TranslationServer.Translate("Autosaved {0}"), when), "Faint", false);
        info.AddChild(whenLabel);
        h.AddChild(info);

        var recover = new Button { Text = "Recover", ThemeTypeVariation = "Primary", Name = $"Recover{index}" };
        recover.Pressed += () => Recover(c);
        var discard = new Button { Text = "Discard", Name = $"Discard{index}" };
        discard.Pressed += () => Discard(c);
        h.AddChild(recover);
        h.AddChild(discard);

        row.AddChild(W.Margin(h, 4, 4, 4, 4));
        _list.AddChild(row);
        _rows.Add(row);
    }

    public bool Recover(RecoveryCandidate c)
    {
        if (!AppController.Instance.RestoreFrom(c)) return false;
        Hide();
        QueueFree();
        return true;
    }

    public bool RecoverAt(int index) => Recover(_candidates[index]);

    public void Discard(RecoveryCandidate c)
    {
        var i = _candidates.IndexOf(c);
        if (i < 0) return;
        AppController.Instance.Recovery.Discard(c);
        _candidates.RemoveAt(i);
        _list.RemoveChild(_rows[i]);
        _rows[i].QueueFree();
        _rows.RemoveAt(i);
        if (_candidates.Count == 0) { Hide(); QueueFree(); }
        else Size = new Vector2I(Size.X, 0);
    }
}
