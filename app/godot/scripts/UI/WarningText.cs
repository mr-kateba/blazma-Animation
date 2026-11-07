using Blazma.Commands;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Translated text for core warnings. Warnings are matched by their stable code first; codes whose English text carries data
/// fall back to the generic sentence below, and unknown codes fall back to the (translated, if known) core message.
/// </summary>
public static class WarningText
{
    static string Tr(string s) => TranslationServer.Translate(s);

    public static string Describe(Diagnostic w) => w.Code switch
    {
        "clamped_by_limits" => Tr("The value was limited to the joint range."),
        "clip_extended" => Tr("The clip was made longer to fit the new keys."),
        "keys_replaced" => Tr("Existing keys at these frames were replaced."),
        "no_feet" => Tr("This character has no humanoid feet, so the foot phase could not be matched."),
        "no_contacts" => Tr("No foot contact was found, so the feet were not locked."),
        "track_locked" => Tr("Locked tracks were skipped."),
        "track_muted" => Tr("Muted rotation tracks were skipped."),
        "bones_missing" => Tr("Some bones of this preset are not mapped in this character and were skipped."),
        "ik_driven" => Tr("Arm and leg bones that an IK control drives are overridden while that control is pinned."),
        "ignored_entries" or "ignored_targets" => Tr("Some entries do not belong to this character and were ignored."),
        "prop_keys_space" => Tr("Existing keys of this tool are now read in a different space (relative to the bone or to the world)."),
        "prop_keys_kept" => Tr("Clips still contain keys for this tool."),
        "prop_tracks" => Tr("The tools keep their keys in the new clip."),
        "prop_attached_elsewhere" => Tr("This tool was attached to another bone and was moved."),
        "no_hitboxes" => Tr("This clip has no hitbox yet, so the attack cannot hit anything. Add one in the Combat panel."),
        "non_finite" => Tr("Some frames had invalid values and were skipped."),
        "estimated_mass" => Tr("This character has no bone masses, so standard body proportions were used."),
        "still_unbalanced" => Tr("Some frames could not be fully balanced: the hips can only shift so far, or the legs cannot reach."),
        "feet_moved" => Tr("The supporting feet had to move a little because the legs could not reach. Try a smaller margin."),
        "foot_slide" => Tr("Some supporting feet could not stay planted (no leg IK, or locked or muted leg tracks), so they may slide."),
        "degenerate_support" => Tr("Some frames stand on a single point or edge (tiptoe or heel), where the centre of mass cannot be kept well inside."),
        "untouched_frames_changed" => Tr("Frames that were not corrected moved slightly because of the new keys."),
        "may_have_missed_contacts" => Tr("This attack moves in a way the test cannot follow exactly, so a very short touch may have been missed. Try again with a slightly bigger hitbox."),
        "procedural" => Tr("Procedural provider: a keyword-based demo cycle, not AI generation."),
        _ => Tr(w.Message),
    };
}
