using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>Unit suffixes: Latin in English, Arabic abbreviations (م, سم, ث, د) in Arabic. Digits stay Latin.</summary>
public static class Unit
{
    static bool Ar => AppController.Instance is { IsRtl: true };

    public static string Localize(string suffix) => !Ar ? suffix : suffix.Trim() switch
    {
        "m" => " م",
        "cm" => " سم",
        "m/s" => " م/ث",
        "s" => " ث",
        "min" => " د",
        _ => suffix,
    };

    public static string Cm => Localize(" cm");
    public static string M => Localize(" m");
}

/// <summary>A SpinBox whose unit suffix follows the language (the English suffix is kept and re-localized on a language change).</summary>
public partial class UnitSpinBox : SpinBox
{
    string _unit = "";
    public string UnitEn { get => _unit; set { _unit = value; Suffix = Unit.Localize(value); } }
    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged) Suffix = Unit.Localize(_unit);
    }
}

/// <summary>Small builders shared by the Phase 8 dialogs and panels (labelled rows, numeric fields that stay left to right in Arabic).</summary>
public static class P8
{
    /// <summary>Keeps numbers and paths in left-to-right order inside right-to-left text.</summary>
    public static string Ltr(string s) => "⁦" + s + "⁩";

    public static string Num(double v, string format = "0.###") => v.ToString(format, System.Globalization.CultureInfo.InvariantCulture);

    public static void Row(GridContainer grid, string label, Control field, bool top = false)
    {
        var l = W.Label(label, "Dim");
        l.SizeFlagsVertical = top ? Control.SizeFlags.ShrinkBegin : Control.SizeFlags.ShrinkCenter;
        grid.AddChild(l);
        field.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        grid.AddChild(field);
    }

    public static GridContainer Grid()
    {
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 10);
        return grid;
    }

    public static void Wrap(Label l, float width = 460)
    {
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(width, 0);
        l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        l.TextDirection = Control.TextDirection.Auto;
    }

    /// <summary>A number box whose digits are always Latin and left to right.</summary>
    public static SpinBox Spin(double min, double max, double step, double value, string suffix = "", bool integer = false)
    {
        var s = new UnitSpinBox
        {
            MinValue = min, MaxValue = max, Step = step, Value = value, UnitEn = suffix, Rounded = integer, CustomArrowStep = step,
            LocalizeNumeralSystem = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
        };
        s.GetLineEdit().LocalizeNumeralSystem = false;
        s.GetLineEdit().TextDirection = Control.TextDirection.Ltr;
        s.GetLineEdit().ThemeTypeVariation = "MonoEdit";
        return s;
    }

    public static OptionButton Option(float minWidth = 0) =>
        new() { AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled, FitToLongestItem = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(minWidth, 0) };

    public static void Press(BaseButton b) => b.EmitSignal(BaseButton.SignalName.Pressed);
}
