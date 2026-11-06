using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>Phase 6 part of the shell: balance overlay layer, Physics panel, secondary motion dialog, toolbar buttons and the Physics menu.</summary>
public partial class MainWindow
{
    const int MPhysShow = 45, MPhysPaths = 46, MPhysPanel = 47, MPhysFix = 48, MPhysSecondary = 49;

    public PhysicsOverlay PhysicsView { get; private set; } = null!;
    public PhysicsPanel PhysicsPanelNode { get; private set; } = null!;
    public SecondaryMotionDialog SecondaryDlg { get; private set; } = null!;
    public Button PhysicsButton { get; private set; } = null!;
    public Button PhysicsPanelButton { get; private set; } = null!;
    PopupMenu _mPhysics = null!;
    VBoxContainer _rightDock = null!;

    // ---- construction ------------------------------------------------------------------------------------------------

    /// <summary>Overlay under the motion layer; the panel is created here and docked by BuildUi into the inline-end column.</summary>
    void BuildPhysicsLayers()
    {
        PhysicsView = new PhysicsOverlay(Overlay);
        W.Fill(PhysicsView);
        _center.AddChild(PhysicsView);
        _center.MoveChild(PhysicsView, Motion.GetIndex());

        PhysicsPanelNode = new PhysicsPanel(this);
    }

    /// <summary>One more toggle in the view toolbar: the balance overlays and the timeline strip.</summary>
    void AddPhysicsToolbar(HBoxContainer row)
    {
        PhysicsButton = ViewToggle("Balance", "Show the centre of mass, support polygon and contact points in the viewport, and the balance strip under the timeline.",
            "<circle cx='8' cy='5' r='2.6'/><path d='M8 8v3'/><path d='M2.5 13.5h11' stroke-width='1.6'/><path d='M4 11l8 0' />", on => App.SetShowPhysics(on));
        row.AddChild(PhysicsButton);
    }

    /// <summary>Top-bar toggle for the Physics panel, shown in Advanced mode (Simple mode uses the Physics menu).</summary>
    Control BuildPhysicsPanelToggle()
    {
        PhysicsPanelButton = new Button
        {
            Text = "Physics", ToggleMode = true, ThemeTypeVariation = "Transport", FocusMode = FocusModeEnum.None,
            SizeFlagsVertical = SizeFlags.ShrinkCenter, TooltipText = "Show or hide the Physics panel: balance of this frame, auto-balance and secondary motion.",
        };
        PhysicsPanelButton.Toggled += on => App.SetPhysicsPanel(on);
        return PhysicsPanelButton;
    }

    void BuildPhysicsMenu()
    {
        var m = _mPhysics;
        m.AddCheckItem("Show Balance in Viewport", MPhysShow);
        m.AddCheckItem("Show COM, Hip and Foot Paths", MPhysPaths);
        m.AddCheckItem("Physics Panel", MPhysPanel);
        m.AddSeparator();
        m.AddItem("Auto-balance Unbalanced Frames", MPhysFix);
        m.AddItem("Secondary Motion…", MPhysSecondary);
        m.AboutToPopup += UpdateMenus;
        m.IdPressed += id => OnMenu((int)id);
    }

    void BuildPhysicsDialogs()
    {
        SecondaryDlg = new SecondaryMotionDialog(this);
        AddChild(SecondaryDlg);
    }

    // ---- actions --------------------------------------------------------------------------------------------------------

    void OnPhysicsMenu(int id)
    {
        switch (id)
        {
            case MPhysShow: App.SetShowPhysics(!App.Settings.ShowPhysics); break;
            case MPhysPaths: App.SetShowPhysicsPaths(!App.Settings.ShowPhysicsPaths); break;
            case MPhysPanel: App.SetPhysicsPanel(!App.Settings.PhysicsPanel); break;
            case MPhysFix:
                if (!App.Settings.PhysicsPanel) App.SetPhysicsPanel(true);
                PhysicsPanelNode.RunAutoBalance();
                break;
            case MPhysSecondary: OpenSecondaryMotionDialog(); break;
        }
    }

    /// <summary>Open the Secondary Motion dialog for the active clip (Physics menu, Physics panel).</summary>
    public void OpenSecondaryMotionDialog()
    {
        if (SecondaryDlg.Visible) return;
        if (!App.Physics.TryResolve(out _, out _, out _))
        {
            _toast.Notify(Tr("Key a clip for this character first, then add secondary motion."), _palette.Warn);
            return;
        }
        Inspector.Flush();
        SecondaryDlg.Open();
    }

    // ---- sync ---------------------------------------------------------------------------------------------------------------

    void UpdatePhysicsMenu()
    {
        if (_mPhysics is null || PhysicsPanelNode is null) return;
        var s = App.Settings;
        void Check(int id, bool on) => _mPhysics.SetItemChecked(_mPhysics.GetItemIndex(id), on);
        Check(MPhysShow, s.ShowPhysics); Check(MPhysPaths, s.ShowPhysicsPaths); Check(MPhysPanel, s.PhysicsPanel);
        var has = App.Physics.TryResolve(out _, out _, out _);
        _mPhysics.SetItemDisabled(_mPhysics.GetItemIndex(MPhysFix), !has);
        _mPhysics.SetItemDisabled(_mPhysics.GetItemIndex(MPhysSecondary), !has);
    }

    /// <summary>Settings changed: toolbar toggles, panel placement and visibility, overlay and strip redraw.</summary>
    void SyncPhysicsUi()
    {
        if (PhysicsPanelNode is null) return;
        var s = App.Settings;
        PhysicsButton.SetPressedNoSignal(s.ShowPhysics);
        PhysicsPanelButton.SetPressedNoSignal(s.PhysicsPanel);
        PhysicsPanelButton.Visible = s.AdvancedMode; // Simple mode reaches the panel from the Physics menu
        PhysicsPanelNode.SyncVisible();
        _rightDock.Visible = PhysicsPanelNode.Visible || HistoryPanel.Visible || PropsPanelNode.Visible || CombatPanelOpen;
        PhysicsPanelNode.ReserveBelow = (HistoryPanel.Visible ? 170 : 0) + (PropsPanelNode.Visible ? 180 : 0) + (CombatPanelOpen ? 180 : 0);
        CombatPanelNode.ReserveBelow = HistoryPanel.Visible ? 170 : 0;
        PhysicsView.QueueRedraw();
        Timeline.Balance.Sync();
        UpdatePhysicsMenu();
    }
}
