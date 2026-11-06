using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Model;
using Godot;

namespace Blazma.UI.App;

/// <summary>What the last hit test found, kept so the viewport can show the contact point and the hurtboxes at the tested placement.</summary>
public sealed record HitTestView(string AttackerId, string AttackClipId, string DefenderId, string? DefenderClipId, double X, double Z, double Facing, JsonObject Result)
{
    public JsonObject? FirstHit => Result["firstHit"] as JsonObject;
    public JsonObject? ReactionArgs => Result["reactionArgs"] as JsonObject;
}

/// <summary>
/// Phase 9 part of the app controller: the combat view toggles and the latest hit test. Combat data itself (events, phases, hitboxes,
/// hurtboxes, combos) is project data changed only through commands; this class only holds UI state. A hit test result describes the
/// project as it was when it ran, so any change of clips, combat data or characters drops it.
/// </summary>
public partial class AppController
{
    /// <summary>The latest hit test (null when none ran or the project changed since).</summary>
    public HitTestView? HitView { get; private set; }

    /// <summary>Raised when <see cref="HitView"/> was set or dropped (the overlay redraws).</summary>
    public event Action? HitViewChanged;

    public void SetShowCombat(bool show)
    {
        Settings.ShowCombat = show;
        Settings.Save();
        SettingsChanged?.Invoke();
    }

    public void SetCombatPanel(bool open)
    {
        Settings.CombatPanel = open;
        Settings.Save();
        SettingsChanged?.Invoke();
    }

    /// <summary>Run a read-only command without reporting a failure as an error toast (the caller shows a friendly message).</summary>
    public CommandResult Query(string commandId, JsonObject args)
    {
        try { return Session.Execute(Registry.Create(commandId, args), CommandOrigin.Ui); }
        catch (CommandException e) { return CommandResult.Fail(e.Code, e.Message); }
    }

    public void SetHitView(HitTestView? view)
    {
        HitView = view;
        HitViewChanged?.Invoke();
    }

    void Phase9After(SessionChangedArgs args)
    {
        if (HitView is null) return;
        if (args.Changes.Any(c => c.Kind is Change.ClipChanged or Change.CombatChanged or Change.ClipRemoved or Change.CharacterRemoved
            or Change.CharacterAdded or Change.ProjectReplaced or Change.RigChanged or Change.PropChanged or Change.SequenceChanged))
            SetHitView(null);
    }
}
