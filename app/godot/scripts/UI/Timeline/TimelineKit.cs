using System.Globalization;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Godot;

namespace Blazma.UI.Views;

/// <summary>Key diamonds: step = square, linear = diamond, cubic = round.</summary>
public static class KeyGlyph
{
    public static void Draw(CanvasItem ci, Vector2 c, float r, Interpolation interp, Color fill, Color outline, float outlineWidth = 1.3f)
    {
        switch (interp)
        {
            case Interpolation.Step:
            {
                var h = r * 0.82f;
                var rect = new Rect2(c - new Vector2(h, h), new Vector2(h * 2, h * 2));
                ci.DrawRect(rect, fill, true);
                ci.DrawRect(rect, outline, false, outlineWidth);
                break;
            }
            case Interpolation.Linear:
            {
                var pts = new[] { c + new Vector2(0, -r), c + new Vector2(r, 0), c + new Vector2(0, r), c + new Vector2(-r, 0) };
                ci.DrawColoredPolygon(pts, fill);
                ci.DrawPolyline(new[] { pts[0], pts[1], pts[2], pts[3], pts[0] }, outline, outlineWidth, true);
                break;
            }
            default:
                ci.DrawCircle(c, r * 0.92f, fill);
                ci.DrawArc(c, r * 0.92f, 0, Mathf.Tau, 20, outline, outlineWidth, true);
                break;
        }
    }
}

/// <summary>Maps frames to pixels along a horizontal axis (time always runs left to right).</summary>
public sealed class FrameAxis
{
    public double Start;
    public double Ppf = 10;   // pixels per frame
    public float Left;        // x of Start

    public float X(double frame) => Left + (float)((frame - Start) * Ppf);
    public double FrameAt(float x) => Start + (x - Left) / Ppf;

    public void Fit(double first, double last, float width, float pad = 14)
    {
        var span = Math.Max(1, last - first);
        Ppf = Math.Max(0.5, (width - 2 * pad) / span);
        Start = first - pad / Ppf;
    }

    public void ZoomAt(float x, double factor, double minPpf = 0.4, double maxPpf = 200)
    {
        var frame = FrameAt(x);
        Ppf = Math.Clamp(Ppf * factor, minPpf, maxPpf);
        Start = frame - (x - Left) / Ppf;
    }

    public void Pan(float dxPixels) => Start -= dxPixels / Ppf;

    /// <summary>Distance between labelled ticks (1, 2, 5, 10, ...) so labels stay at least <paramref name="minPixels"/> apart.</summary>
    public int TickStep(float minPixels = 38)
    {
        var raw = minPixels / Ppf;
        foreach (var s in new[] { 1, 2, 5, 10, 20, 25, 50, 100, 200, 500, 1000 })
            if (s >= raw) return s;
        return 1000;
    }
}

/// <summary>Small SVG icons tinted by the theme's icon colours (drawn white, multiplied).</summary>
public static class UiIcons
{
    static readonly Dictionary<string, ImageTexture> Cache = new();

    public static ImageTexture Make(string body, bool filled = true, int size = 16)
    {
        var key = body + filled + size;
        if (Cache.TryGetValue(key, out var t)) return t;
        var attrs = filled
            ? "fill='white'"
            : "fill='none' stroke='white' stroke-width='1.6' stroke-linecap='round' stroke-linejoin='round'";
        var svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='{size}' height='{size}' viewBox='0 0 16 16' {attrs}>{body}</svg>";
        var img = new Image();
        img.LoadSvgFromString(svg, 1.0f);
        return Cache[key] = ImageTexture.CreateFromImage(img);
    }

    public const string Start = "<path d='M2.8 3h1.7v10H2.8zM13.2 3v10L5.8 8z'/>";
    public const string End = "<path d='M11.5 3h1.7v10h-1.7zM2.8 3v10L10.2 8z'/>";
    public const string PrevKey = "<path d='M13 3v10L6.8 8z'/><path d='M3.4 5l2.4 3-2.4 3-2.4-3z'/>";
    public const string NextKey = "<path d='M3 3v10L9.2 8z'/><path d='M12.6 5l2.4 3-2.4 3-2.4-3z'/>";
    public const string Play = "<path d='M4.6 2.6v10.8L13.2 8z'/>";
    public const string Pause = "<path d='M3.8 3h3v10h-3zM9.2 3h3v10h-3z'/>";
    public const string Loop = "<path d='M2.5 7.5V7a2.5 2.5 0 0 1 2.5-2.5h7.5M10.3 2.2l2.3 2.3-2.3 2.3M13.5 8.5V9A2.5 2.5 0 0 1 11 11.5H3.5M5.7 9.2l-2.3 2.3 2.3 2.3'/>";
    public const string Lock = "<rect x='3.5' y='7' width='9' height='6.5' rx='1.4'/><path d='M5.5 7V5.4a2.5 2.5 0 0 1 5 0V7'/>";
    public const string Mute = "<path d='M1.8 8s2.3-4.2 6.2-4.2S14.2 8 14.2 8s-2.3 4.2-6.2 4.2S1.8 8 1.8 8z'/><circle cx='8' cy='8' r='1.8'/>";
    public const string MuteOff = "<path d='M1.8 8s2.3-4.2 6.2-4.2S14.2 8 14.2 8s-2.3 4.2-6.2 4.2S1.8 8 1.8 8z'/><path d='M3 13L13 3'/>";
    public const string Chevron = "<path d='M6 4l4 4-4 4'/>";
}

public static class TlText
{
    /// <summary>Shorten text with an ellipsis so it fits <paramref name="maxWidth"/> pixels.</summary>
    public static string Fit(Font font, string text, int size, float maxWidth)
    {
        if (maxWidth <= 8) return "";
        if (font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X <= maxWidth) return text;
        var lo = 0; var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (font.GetStringSize(text[..mid] + "…", HorizontalAlignment.Left, -1, size).X <= maxWidth) lo = mid; else hi = mid - 1;
        }
        return lo == 0 ? "" : text[..lo] + "…";
    }

    public static string PhaseName(KeyPhase phase) => phase switch
    {
        KeyPhase.Initial => TranslationServer.Translate("Initial Pose"),
        KeyPhase.Anticipation => TranslationServer.Translate("Anticipation"),
        KeyPhase.MainAction => TranslationServer.Translate("Main Action"),
        KeyPhase.Contact => TranslationServer.Translate("Contact"),
        KeyPhase.FollowThrough => TranslationServer.Translate("Follow-through"),
        KeyPhase.Recovery => TranslationServer.Translate("Recovery"),
        KeyPhase.Final => TranslationServer.Translate("Final Pose"),
        _ => "",
    };

    /// <summary>Marker label: phase markers show the translated phase name (their stored name is the English default).</summary>
    public static string MarkerLabel(Marker m) => m.Phase != KeyPhase.None && IsDefaultPhaseName(m) ? PhaseName(m.Phase) : m.Name;

    static bool IsDefaultPhaseName(Marker m) => m.Name == m.Phase switch
    {
        KeyPhase.Initial => "Initial Pose",
        KeyPhase.Anticipation => "Anticipation",
        KeyPhase.MainAction => "Main Action",
        KeyPhase.Contact => "Contact",
        KeyPhase.FollowThrough => "Follow-through",
        KeyPhase.Recovery => "Recovery",
        KeyPhase.Final => "Final Pose",
        _ => "",
    };

    public static string Frame(long f) => f.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Key lookups on a clip, shared by the timeline, dope sheet and graph.</summary>
public static class ClipKeys
{
    public static long FrameOf(Clip clip, Keyframe k) => k.Time.ToFrame(clip.Fps);

    public static IEnumerable<Track> TracksOf(Clip clip, string? targetId) =>
        targetId is null ? Enumerable.Empty<Track>() : clip.Tracks.Where(t => t.Target.Id.ToString() == targetId);

    public static SortedSet<long> Frames(Clip clip, IEnumerable<Track> tracks)
    {
        var set = new SortedSet<long>();
        foreach (var t in tracks) foreach (var k in t.Keys) set.Add(FrameOf(clip, k));
        return set;
    }

    /// <summary>Interpolation shown for a target's keys at a frame: rotation first, else the first channel with a key there.</summary>
    public static Interpolation InterpAt(Clip clip, IEnumerable<Track> tracks, long frame)
    {
        Interpolation? any = null;
        foreach (var t in tracks.OrderBy(t => t.Channel == TrackChannels.Rotation ? 0 : 1))
        {
            if (t.Channel == TrackChannels.Pinned) continue;
            var k = t.Keys.Find(x => FrameOf(clip, x) == frame);
            if (k is null) continue;
            if (t.Channel == TrackChannels.Rotation) return k.Interp;
            any ??= k.Interp;
        }
        return any ?? Interpolation.Cubic;
    }
}
