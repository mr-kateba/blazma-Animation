using System.Text.Json.Nodes;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>Floating inspector: project settings and the selected clip. Every edit is a command.</summary>
public partial class InspectorPanel : PanelContainer
{
    static readonly (string Label, int Num, int Den)[] Rates =
    {
        ("24", 24, 1), ("25", 25, 1), ("30", 30, 1), ("48", 48, 1), ("50", 50, 1), ("60", 60, 1),
        ("23.976", 24000, 1001), ("29.97", 30000, 1001),
    };

    readonly MainWindow _host;
    public LineEdit ProjectName { get; } = new() { SelectAllOnFocus = true };
    public OptionButton FrameRateBox { get; } = new() { FitToLongestItem = false };
    public LineEdit ClipName { get; } = new() { SelectAllOnFocus = true };
    public SpinBox Length { get; } = new() { MinValue = 1, MaxValue = 1_000_000, Step = 1, Rounded = true, AllowGreater = false };
    readonly Label _clipFps = W.Label("", "MonoLabel", false);
    readonly Label _noClip = W.Label("Select a clip in the strip below to edit it.", "Faint");
    readonly VBoxContainer _clipBox = new();
    readonly Label _clipHeader = W.Label("Selected clip", "Section");
    bool _busy, _typingProject, _typingClip;

    static AppController App => AppController.Instance;

    public InspectorPanel(MainWindow host)
    {
        _host = host;
        ThemeTypeVariation = "Floating";
        CustomMinimumSize = new Vector2(264, 0);
        SizeFlagsVertical = SizeFlags.ShrinkBegin;
        LocalizeNumeralSystem = false;

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        AddChild(W.Margin(root, 12, 10, 12, 12));

        // Project -------------------------------------------------------------------------------
        root.AddChild(W.Label("Project", "Section"));
        var g = Grid();
        g.AddChild(W.Label("Name", "Dim"));
        ProjectName.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        ProjectName.TextChanged += _ => { if (!_busy) _typingProject = true; };
        ProjectName.TextSubmitted += _ => CommitProjectName();
        ProjectName.FocusExited += CommitProjectName;
        ProjectName.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        g.AddChild(ProjectName);
        g.AddChild(W.Label("Frame rate", "Dim"));
        foreach (var r in Rates) FrameRateBox.AddItem(r.Label);
        FrameRateBox.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        FrameRateBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        FrameRateBox.ItemSelected += OnFrameRate;
        g.AddChild(FrameRateBox);
        root.AddChild(g);
        root.AddChild(new HSeparator());

        // Clip ----------------------------------------------------------------------------------
        root.AddChild(_clipHeader);
        root.AddChild(_noClip);
        _noClip.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _noClip.CustomMinimumSize = new Vector2(200, 0);
        _clipBox.AddThemeConstantOverride("separation", 10);
        var cg = Grid();
        cg.AddChild(W.Label("Name", "Dim"));
        ClipName.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        ClipName.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        ClipName.TextChanged += _ => { if (!_busy) _typingClip = true; };
        ClipName.TextSubmitted += _ => CommitClipName();
        ClipName.FocusExited += CommitClipName;
        cg.AddChild(ClipName);
        cg.AddChild(W.Label("Length (frames)", "Dim"));
        Length.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        Length.GetLineEdit().ThemeTypeVariation = "MonoEdit";
        Length.LocalizeNumeralSystem = false; // numbers stay Latin digits in Arabic
        Length.GetLineEdit().LocalizeNumeralSystem = false;
        Length.ValueChanged += OnLength;
        cg.AddChild(Length);
        cg.AddChild(W.Label("Frame rate", "Dim"));
        cg.AddChild(_clipFps);
        _clipBox.AddChild(cg);
        root.AddChild(_clipBox);
    }

    static GridContainer Grid()
    {
        var g = new GridContainer { Columns = 2 };
        g.AddThemeConstantOverride("h_separation", 10);
        g.AddThemeConstantOverride("v_separation", 8);
        return g;
    }

    Project Proj => App.Session.Project;

    public void Refresh(bool force = false)
    {
        _busy = true;
        try
        {
            var p = Proj;
            if (force || !_typingProject) { ProjectName.Text = p.Name; _typingProject = false; }
            var idx = Array.FindIndex(Rates, r => r.Num == p.Settings.DefaultFrameRate.Numerator && r.Den == p.Settings.DefaultFrameRate.Denominator);
            // Odd rates (set by a script or imported) get a temporary entry so the box never lies.
            while (FrameRateBox.ItemCount > Rates.Length) FrameRateBox.RemoveItem(FrameRateBox.ItemCount - 1);
            if (idx < 0) { FrameRateBox.AddItem(Fmt.Rate(p.Settings.DefaultFrameRate)); idx = FrameRateBox.ItemCount - 1; }
            FrameRateBox.Select(idx);

            var clip = _host.SelectedClip;
            _clipBox.Visible = clip is not null;
            _noClip.Visible = clip is null;
            if (clip is not null)
            {
                if (force || !_typingClip) { ClipName.Text = clip.Name; _typingClip = false; }
                Length.SetValueNoSignal(clip.DurationFrames);
                _clipFps.Text = string.Format(Tr("{0} fps"), Fmt.Rate(clip.Fps));
            }
        }
        finally { _busy = false; }
    }

    // ---- edits (all through commands) ------------------------------------------------------------

    /// <summary>Commit any text still being typed (called before the selection changes).</summary>
    public void Flush() { CommitProjectName(); CommitClipName(); }

    public void CommitProjectName()
    {
        _typingProject = false;
        var text = ProjectName.Text.Trim();
        if (text == Proj.Name) return;
        var r = App.Execute("project.rename", new JsonObject { ["name"] = text });
        if (!r.Ok) Refresh(true);
    }

    void OnFrameRate(long index)
    {
        if (_busy || index < 0 || index >= Rates.Length) return;
        var (_, n, d) = Rates[index];
        var r = App.Execute("project.set_frame_rate", new JsonObject { ["numerator"] = n, ["denominator"] = d });
        if (!r.Ok) Refresh(true);
    }

    public void CommitClipName()
    {
        _typingClip = false;
        var clip = _host.SelectedClip;
        if (clip is null) return;
        var text = ClipName.Text.Trim();
        if (text == clip.Name) return;
        var r = App.Execute("clip.rename", new JsonObject { ["clipId"] = clip.Id.ToString(), ["name"] = text });
        if (!r.Ok) Refresh(true);
    }

    void OnLength(double value)
    {
        if (_busy) return;
        var clip = _host.SelectedClip;
        if (clip is null || (long)value == clip.DurationFrames) return;
        var r = App.Execute("clip.set_duration", new JsonObject { ["clipId"] = clip.Id.ToString(), ["frames"] = (long)value });
        if (!r.Ok) Refresh(true);
    }
}
