using Godot;

namespace Blazma.UI.Views;

public enum StageTool { Select, Rotate, Move }

/// <summary>
/// What the user has selected in the viewport / rig tree. Pure UI state: it is not part of the
/// project and not undoable. Ids are the string forms of the project's ObjectIds.
/// </summary>
public sealed class SelectionState
{
    public string? CharacterId { get; private set; }
    public string? BoneId { get; private set; }
    public string? ControlId { get; private set; }
    public StageTool Tool { get; private set; } = StageTool.Rotate;

    public event Action? Changed;
    public event Action? ToolChanged;

    public void SetTool(StageTool tool)
    {
        if (tool == Tool) return;
        Tool = tool;
        ToolChanged?.Invoke();
        Changed?.Invoke();
    }

    public void Set(string? characterId, string? boneId = null, string? controlId = null)
    {
        if (characterId == CharacterId && boneId == BoneId && controlId == ControlId) return;
        CharacterId = characterId;
        BoneId = boneId;
        ControlId = controlId;
        Changed?.Invoke();
    }

    public void SelectBone(string characterId, string boneId) => Set(characterId, boneId, null);
    public void SelectControl(string characterId, string controlId) => Set(characterId, null, controlId);
    public void SelectCharacter(string? characterId) => Set(characterId, characterId == CharacterId ? BoneId : null, characterId == CharacterId ? ControlId : null);
    public void ClearItem() => Set(CharacterId, null, null);
}
