using System.Text.RegularExpressions;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;

namespace Blazma.Rigging;

/// <summary>
/// Mirrors a pose across the character's YZ plane (+X = left ↔ −X = right).
/// Works on each bone's model-space change from rest, so rigs whose left and right rest
/// orientations differ (common in imported rigs) still mirror correctly.
/// Counterparts come from the humanoid map, then from side naming (<see cref="SideNaming"/>).
/// </summary>
public static partial class PoseMirror
{
    public static Pose Mirror(Rig rig, Pose pose)
    {
        var ev = new RigEvaluator(rig);
        var restModel = ev.RestModel();
        var locals = ev.Locals(pose);
        var model = ev.Model(locals);
        var counterpart = Counterparts(rig, ev);

        // Model-space rotation change from rest, per bone.
        var deltaRot = new Quat[ev.Count];
        for (var i = 0; i < ev.Count; i++)
            deltaRot[i] = (model[i].Rotation * restModel[i].Rotation.Inverse()).Normalized();

        var result = new Pose();
        var newModel = new Transform[ev.Count];
        for (var i = 0; i < ev.Count; i++)
        {
            var src = counterpart[i];
            var bone = rig.Bones[i];
            var parent = ev.ParentOf(i);
            var parentModel = parent < 0 ? Transform.Identity : newModel[parent];

            var rot = (MirrorRotation(deltaRot[src]) * restModel[i].Rotation).Normalized();

            // Translation: mirror the source bone's offset from its rest, expressed in model space.
            var srcBone = rig.Bones[src];
            var srcParent = ev.ParentOf(src);
            var srcParentModel = srcParent < 0 ? Transform.Identity : model[srcParent];
            var offsetModel = srcParentModel.Rotation.Rotate(Mul(srcParentModel.Scale, locals[src].Translation - srcBone.Rest.Translation));
            var localTranslation = bone.Rest.Translation + Div(parentModel.Rotation.Inverse().Rotate(MirrorVector(offsetModel)), parentModel.Scale);

            var local = new Transform(localTranslation, (parentModel.Rotation.Inverse() * rot).Normalized(), locals[src].Scale);
            newModel[i] = parentModel * local;
            // Asymmetric rests can make even an untouched bone change, so compare with rest instead of
            // copying only the bones that were posed.
            if (!SameTransform(local, bone.Rest))
                result.Bones[bone.Id.ToString()] = local;
        }

        foreach (var control in rig.Controls)
        {
            var src = CounterpartControl(rig, control);
            if (!pose.Controls.TryGetValue(src.Id.ToString(), out var s)) continue;
            result.Controls[control.Id.ToString()] = new ControlState
            {
                Pinned = s.Pinned,
                Transform = MirrorControl(s.Transform, src.Rest, control.Rest),
            };
        }
        return result;
    }

    static Vec3 Mul(Vec3 s, Vec3 v) => new(s.X * v.X, s.Y * v.Y, s.Z * v.Z);
    static Vec3 Div(Vec3 v, Vec3 s) => new(v.X / s.X, v.Y / s.Y, v.Z / s.Z);

    static bool SameTransform(Transform a, Transform b) =>
        a.Translation.ApproxEquals(b.Translation, 1e-9) && a.Scale.ApproxEquals(b.Scale, 1e-9) && a.Rotation.ApproxEquals(b.Rotation, 1e-9);

    /// <summary>Reflection across the YZ plane applied to a rotation: (x, y, z, w) → (x, −y, −z, w).</summary>
    public static Quat MirrorRotation(Quat q) => new(q.X, -q.Y, -q.Z, q.W);

    public static Vec3 MirrorVector(Vec3 v) => new(-v.X, v.Y, v.Z);

    static Transform MirrorControl(Transform t, Transform srcRest, Transform dstRest)
    {
        var delta = (t.Rotation * srcRest.Rotation.Inverse()).Normalized();
        return new Transform(MirrorVector(t.Translation), (MirrorRotation(delta) * dstRest.Rotation).Normalized(), t.Scale);
    }

    /// <summary>For each bone index, the index of its mirror bone (itself for centre bones).</summary>
    public static int[] Counterparts(Rig rig, RigEvaluator ev)
    {
        var result = new int[rig.Bones.Count];
        for (var i = 0; i < rig.Bones.Count; i++)
        {
            result[i] = i;
            var bone = rig.Bones[i];
            if (rig.StandardNameOf(bone.Id) is { } std)
            {
                var other = rig.FindByStandardName(HumanoidStandard.Counterpart(std));
                if (other is not null) { result[i] = ev.IndexOf(other.Id); continue; }
            }
            if (SideNaming.Counterpart(bone.Name) is { } name && rig.FindBoneByName(name) is { } byName)
                result[i] = ev.IndexOf(byName.Id);
        }
        return result;
    }

    static RigControl CounterpartControl(Rig rig, RigControl control) =>
        SideNaming.Counterpart(control.Name) is { } name && rig.Controls.Find(c => c.Name == name) is { } other ? other : control;
}

/// <summary>Detects left/right in bone names: _l/_r, .L/.R, Left…/Right…, L_/R_, "Bip01 L ".</summary>
public static partial class SideNaming
{
    static readonly (Regex Pattern, Func<Match, string> Swap)[] Rules =
    {
        (SuffixRegex(), m => m.Groups[1].Value + m.Groups[2].Value + Flip(m.Groups[3].Value)),
        (WordRegex(), m => m.Groups[1].Value + FlipWord(m.Groups[2].Value) + m.Groups[3].Value),
        (PrefixRegex(), m => Flip(m.Groups[1].Value) + m.Groups[2].Value + m.Groups[3].Value),
        (SpacedRegex(), m => m.Groups[1].Value + Flip(m.Groups[2].Value) + m.Groups[3].Value),
    };

    /// <summary>The opposite-side name, or null if the name carries no side.</summary>
    public static string? Counterpart(string name)
    {
        foreach (var (pattern, swap) in Rules)
        {
            var m = pattern.Match(name);
            if (m.Success) return swap(m);
        }
        return null;
    }

    static string Flip(string s) => s switch
    {
        "l" => "r", "r" => "l", "L" => "R", "R" => "L",
        _ => s,
    };

    static string FlipWord(string s) => s switch
    {
        "Left" => "Right", "Right" => "Left", "left" => "right", "right" => "left",
        "LEFT" => "RIGHT", "RIGHT" => "LEFT",
        _ => s,
    };

    [GeneratedRegex(@"^(.*?)([_.\-])([lLrR])$")]
    private static partial Regex SuffixRegex();

    [GeneratedRegex(@"^(.*?)(Left|Right|left|right|LEFT|RIGHT)(.*)$")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"^([lLrR])([_.\-])(.+)$")]
    private static partial Regex PrefixRegex();

    [GeneratedRegex(@"^(.* )([LR])( .*)$")]
    private static partial Regex SpacedRegex();
}
