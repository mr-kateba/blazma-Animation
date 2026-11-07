using Blazma.AI;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Scripted tutorial recording (BLAZMA_DEMO=1..4 picks the prompt). Opens "Generate motion…", types the Arabic prompt
/// character by character, shows the understood steps, presses Generate and plays the result with the camera following the
/// character. Meant to run under --write-movie; does nothing unless the environment variable is set.
/// BLAZMA_DEMO_PROVIDER=procedural (default) | kimodo; BLAZMA_DEMO_NOTE adds a line to the caption.
/// </summary>
public partial class Demo : Node
{
    static readonly double[] Seconds = { 7, 8, 6, 5 };
    static readonly string[] Prompts =
    {
        "شخص يمشي ثم يلتفت ويلوّح بيده",
        "في واحد وراك، اجري، وبعدها موت",
        "يقفز ثم يجري للأمام ثم يقف",
        "يضرب لكمة قوية ثم يرجع لوضع الاستعداد",
    };

    readonly MainWindow _w;
    readonly int _n;
    Label _step = null!, _note = null!;
    bool _follow;
    Vector3 _target;
    static AppController App => AppController.Instance;

    public static int Requested => int.TryParse(System.Environment.GetEnvironmentVariable("BLAZMA_DEMO"), out var n) && n is >= 1 and <= 4 ? n : 0;
    public static Demo? TryCreate(MainWindow w) => Requested is > 0 and var n ? new Demo(w, n) : null;
    Demo(MainWindow w, int n) { _w = w; _n = n; }

    async Task Wait(double seconds)
    {
        var t = 0.0;
        while (t < seconds - 1e-6) { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); t += GetProcessDeltaTime(); }
    }

    public override void _Process(double delta)
    {
        if (!_follow || _w.Overlay.AllBounds() is not { } box) return;
        var c = box.GetCenter();
        var goal = new Vector3(c.X, 0.9f, c.Z);
        _target = _target.Lerp(goal, 0.25f);
        _w.Stage.Frame(new Aabb(_target - new Vector3(0.85f, 0.85f, 0.85f), new Vector3(1.7f, 1.7f, 1.7f)));
    }

    void Caption(string step)
    {
        _step.Text = step;
        _note.Text = System.Environment.GetEnvironmentVariable("BLAZMA_DEMO_NOTE") ?? "";
    }

    public override async void _Ready()
    {
        try { await Run(); }
        catch (Exception e) { GD.Print("DEMO ERROR " + e); GetTree().Quit(1); }
    }

    async Task Run()
    {
        var provider = System.Environment.GetEnvironmentVariable("BLAZMA_DEMO_PROVIDER") ?? "procedural";
        var prompt = Prompts[_n - 1];
        var bar = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 4000 };
        bar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        bar.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.72f), ContentMarginTop = 8, ContentMarginBottom = 8 });
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        bar.AddChild(box);
        _step = new Label { HorizontalAlignment = HorizontalAlignment.Center, TextDirection = Control.TextDirection.Rtl, AutoTranslateMode = AutoTranslateModeEnum.Disabled };
        _step.AddThemeFontSizeOverride("font_size", 30);
        _step.AddThemeColorOverride("font_color", Colors.White);
        _note = new Label { HorizontalAlignment = HorizontalAlignment.Center, TextDirection = Control.TextDirection.Rtl, AutoTranslateMode = AutoTranslateModeEnum.Disabled };
        _note.AddThemeFontSizeOverride("font_size", 17);
        _note.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.4f));
        box.AddChild(_step); box.AddChild(_note);
        _w.AddChild(bar);

        GetWindow().GuiEmbedSubwindows = true; // dialogs must be drawn inside the recorded window
        await Wait(0.3);
        App.SetLanguage("ar"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        _w.ShowAgentPanel(false);
        _w.NewProjectNow();
        await Wait(1.0);
        _w.Stage.FrameAll();
        _w.Stage.Orbit(-70, 0); // a little to the side: turns and travel read better
        _target = new Vector3(0, 0.9f, 0);
        _follow = true;
        Caption("١) كتابة الطلب");
        await Wait(1.0);

        _w.OpenGenerateDialog();
        var dlg = _w.GenerateDlg;
        await Wait(0.8);
        dlg.SelectProvider(provider == "kimodo" ? AiText.KimodoId : AiText.ProceduralId);
        await Wait(0.5);
        dlg.Duration.Value = Seconds[_n - 1];
        dlg.Prompt.GrabFocus();
        for (var i = 1; i <= prompt.Length; i++)
        {
            dlg.Prompt.Text = prompt[..i];
            dlg.Prompt.SetCaretColumn(i);
            dlg.Prompt.EmitSignal(TextEdit.SignalName.TextChanged);
            await Wait(1.0 / 12);
        }
        await Wait(0.4);
        dlg.RefreshPlanNow();
        Caption("٢) الخطة المفهومة");
        GD.Print("DEMO plan: " + string.Join(" | ", dlg.PlanRows.Select(r => $"{r.Text} => {r.Caption} {r.Seconds}s understood={r.Understood}")));
        await Wait(3.5);

        Caption("٣) التوليد");
        var t0 = Godot.Time.GetTicksMsec();
        _w.LastMotionApply = null;
        dlg.PressGenerate();
        var f0 = Engine.GetFramesDrawn();
        var tw = 0.0;
        while (_w.LastMotionApply is null && tw < 3600)
        {
            await Wait(1.0 / 30); tw += 1.0 / 30;
            if (provider == "kimodo" && Engine.GetFramesDrawn() - f0 == 30) Caption("٣) التوليد — …جاري التوليد (تم قص وقت الانتظار)");
        }
        GD.Print($"DEMO CUT {f0 + 30} {Engine.GetFramesDrawn()}"); // frames to drop from the video (Kimodo waiting time)
        GD.Print($"DEMO generation wall ms {Godot.Time.GetTicksMsec() - t0} provider={provider} applied={_w.LastMotionApply is not null}");
        await Wait(2.0);

        Caption("٤) التشغيل");
        dlg.Hide();
        await Wait(0.5);
        var clip = App.ActiveClip;
        var dur = clip is null ? 4.0 : clip.DurationFrames / clip.Fps.FramesPerSecond;
        GD.Print($"DEMO clip {clip?.Name} dur={dur:F2}s");
        var loops = dur < 6 ? 2 : 1;
        for (var l = 0; l < loops; l++)
        {
            App.GoToFrame(0);
            App.Play();
            await Wait(dur + 0.2);
            App.Pause();
            await Wait(l == loops - 1 ? 1.5 : 0.5);
        }
        GD.Print("DEMO DONE");
        App.ShutdownCleanly();
        GetTree().Quit(0);
    }
}
