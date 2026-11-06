using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Combat;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Godot;

namespace Blazma.UI.Views;

/// <summary>Colours, translated names and small builders shared by the Phase 9 panel, strip, overlay, combo editor and hit test dialog.</summary>
public static class CombatKit
{
    public static readonly EventKind[] EventKinds = { EventKind.Custom, EventKind.Hit, EventKind.Footstep, EventKind.Sound, EventKind.Vfx };

    public static string Tr(string s) => TranslationServer.Translate(s);

    public static string Ltr(long v) => P8.Ltr(v.ToString(CultureInfo.InvariantCulture));

    public static string KindId(EventKind k) => k.ToString().ToLowerInvariant();

    public static string KindLabel(EventKind k) => Tr(k switch
    {
        EventKind.Hit => "Hit",
        EventKind.Footstep => "Footstep",
        EventKind.Sound => "Sound",
        EventKind.Vfx => "Visual effect",
        _ => "Custom",
    });

    public static string ZoneLabel(HitZone z) => Tr(z switch { HitZone.High => "High", HitZone.Low => "Low", _ => "Middle" });

    public static string ZoneLabel(string? zone) => zone switch
    {
        "high" => ZoneLabel(HitZone.High),
        "low" => ZoneLabel(HitZone.Low),
        _ => ZoneLabel(HitZone.Mid),
    };

    /// <summary>The side a hit comes from, as the core reports it ("front", "back", "left", "right").</summary>
    public static string SideLabel(string? from) => Tr(from switch
    {
        "front" => "Front",
        "back" => "Back",
        "left" => "Left side",
        "right" => "Right side",
        _ => "Any side",
    });

    public static string ShapeLabel(CollisionShapeKind k) => Tr(k switch { CollisionShapeKind.Capsule => "Capsule", CollisionShapeKind.Box => "Box", _ => "Sphere" });

    public static string PhaseLabel(string phase) => Tr(phase switch { "startup" => "Startup", "active" => "Active", "recovery" => "Recovery", _ => "" });

    public static Color ZoneColor(Palette p, HitZone z) => z switch { HitZone.High => p.Warn, HitZone.Low => p.Y, _ => p.Z };

    public static Color ZoneColor(Palette p, string? zone) => zone switch { "high" => p.Warn, "low" => p.Y, _ => p.Z };

    public static Color PhaseColor(Palette p, string phase) => phase switch { "startup" => p.Z, "active" => p.Err, _ => p.Warn };

    public static Color KindColor(Palette p, EventKind k) => k switch
    {
        EventKind.Hit => p.Err,
        EventKind.Footstep => p.Y,
        EventKind.Sound => p.Warn,
        EventKind.Vfx => p.Accent,
        _ => p.Text2,
    };

    public static Color HitboxColor(Palette p) => p.Err;

    /// <summary>Active frames as the command takes them: "12-17,20-23" (end exclusive).</summary>
    public static string Ranges(Hitbox h, FrameRate fps) =>
        string.Join(",", CombatEvaluator.ActiveFrames(h, fps).Select(r => r.End - r.Start == 1 ? $"{r.Start}" : $"{r.Start}-{r.End}"));

    /// <summary>The same for display next to Arabic text: each range isolated left to right.</summary>
    public static string RangesDisplay(Hitbox h, FrameRate fps) =>
        string.Join(", ", CombatEvaluator.ActiveFrames(h, fps).Select(r => P8.Ltr(r.End - r.Start == 1 ? $"{r.Start}" : $"{r.Start}–{r.End}")));

    public static double ToDegrees(double radians) => radians * 180 / Math.PI;

    public static JsonObject Obj(params (string, JsonNode?)[] items)
    {
        var o = new JsonObject();
        foreach (var (k, v) in items) o[k] = v;
        return o;
    }

    public static Button Btn(string text, string tip = "", string? variation = null)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, TooltipText = tip };
        if (variation is not null) b.ThemeTypeVariation = variation;
        return b;
    }

    /// <summary>A drop-down whose fixed items are translated by Godot (kind names and the like).</summary>
    public static OptionButton StaticOption(params string[] items)
    {
        var o = new OptionButton { FitToLongestItem = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var i in items) o.AddItem(i);
        return o;
    }

    public static GridContainer Grid()
    {
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 5);
        return grid;
    }

    /// <summary>A JSON number (int or double) as double; 0 when missing.</summary>
    public static double D(System.Text.Json.Nodes.JsonNode? n) => n is null ? 0 : double.Parse(n.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    public static void Set(SpinBox box, double v)
    {
        if (!Mathf.IsEqualApprox((float)box.Value, (float)v) && !box.GetLineEdit().HasFocus()) box.SetValueNoSignal(v);
    }

    public static void Select(OptionButton o, int index)
    {
        if (index >= 0 && index < o.ItemCount && o.Selected != index) o.Select(index);
    }
}

/// <summary>
/// The fields of a collision shape: kind, radius, length (capsule) and size (box). Only the fields the kind needs are shown. Used for
/// hitboxes and hurtboxes alike; <see cref="Fill"/> writes them as the arguments of hitbox.* / hurtbox.* commands.
/// </summary>
public sealed class ShapeFields
{
    public OptionButton Kind { get; } = CombatKit.StaticOption("Sphere", "Capsule", "Box");
    public SpinBox Radius { get; } = P8.Spin(0.001, 10, 0.001, 0.1, " m");
    public SpinBox Length { get; } = P8.Spin(0, 10, 0.01, 0.5, " m");
    public SpinBox SizeX { get; } = P8.Spin(0.001, 10, 0.01, 0.2, " m");
    public SpinBox SizeY { get; } = P8.Spin(0.001, 10, 0.01, 0.2, " m");
    public SpinBox SizeZ { get; } = P8.Spin(0.001, 10, 0.01, 0.2, " m");
    readonly List<(Control label, Control field, Func<int, bool> shown)> _rows = new();
    public event Action? Edited;
    bool _loading;

    public ShapeFields()
    {
        Kind.TooltipText = "Sphere: a ball. Capsule: a rounded stick (a blade or a limb). Box: a block.";
        Radius.TooltipText = "Radius of the sphere or of the capsule, in metres.";
        Length.TooltipText = "Distance between the two ends of the capsule's round caps, in metres.";
        SizeX.TooltipText = "Width of the box, in metres."; SizeY.TooltipText = "Height of the box, in metres."; SizeZ.TooltipText = "Depth of the box, in metres.";
        Kind.ItemSelected += _ => { UpdateVisibility(); if (!_loading) Edited?.Invoke(); };
        foreach (var s in new[] { Radius, Length, SizeX, SizeY, SizeZ }) s.ValueChanged += _ => { if (!_loading) Edited?.Invoke(); };
    }

    public void AddTo(GridContainer grid)
    {
        void Row(string label, Control field, Func<int, bool> shown)
        {
            var l = W.Label(label, "Dim");
            grid.AddChild(l);
            field.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            grid.AddChild(field);
            _rows.Add((l, field, shown));
        }
        Row("Shape", Kind, _ => true);
        Row("Radius", Radius, k => k != 2);
        Row("Length", Length, k => k == 1);
        Row("Width", SizeX, k => k == 2);
        Row("Height", SizeY, k => k == 2);
        Row("Depth", SizeZ, k => k == 2);
        UpdateVisibility();
    }

    public void UpdateVisibility()
    {
        foreach (var (l, f, shown) in _rows) l.Visible = f.Visible = shown(Kind.Selected);
    }

    public void Load(CollisionShape s)
    {
        _loading = true;
        Kind.Select((int)s.Kind);
        Radius.SetValueNoSignal(Math.Round(s.Radius, 4));
        Length.SetValueNoSignal(Math.Round(s.Length, 4));
        SizeX.SetValueNoSignal(Math.Round(s.Size.X, 4)); SizeY.SetValueNoSignal(Math.Round(s.Size.Y, 4)); SizeZ.SetValueNoSignal(Math.Round(s.Size.Z, 4));
        _loading = false;
        UpdateVisibility();
    }

    public CollisionShapeKind Selected => (CollisionShapeKind)Math.Max(0, Kind.Selected);

    public void Fill(JsonObject a)
    {
        a["shape"] = Selected.ToString().ToLowerInvariant();
        if (Selected != CollisionShapeKind.Box) a["radius"] = Radius.Value;
        if (Selected == CollisionShapeKind.Capsule) a["length"] = Length.Value;
        if (Selected == CollisionShapeKind.Box) { a["sizeX"] = SizeX.Value; a["sizeY"] = SizeY.Value; a["sizeZ"] = SizeZ.Value; }
    }
}
