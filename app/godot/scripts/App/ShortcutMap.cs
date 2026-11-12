using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

namespace Blazma.UI.App;

/// <summary>Where a shortcut applies: everywhere, only while the 3D view has focus, or only while it does not (timeline and the rest).</summary>
public enum ShortcutScope { Global, Viewport, NotViewport }

/// <summary>One command the user can bind keys to. <see cref="Defaults"/> lists chords separated by "|"; the first is shown in menus and tooltips.</summary>
public sealed record ShortcutAction(string Id, string Category, string Label, string Defaults, ShortcutScope Scope = ShortcutScope.Global, bool AllowInText = false);

/// <summary>A key with modifiers, e.g. Ctrl+Shift+S. Ctrl means Command on macOS.</summary>
public readonly record struct Chord(Key Key, bool Ctrl, bool Shift, bool Alt)
{
    public static Chord? Parse(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        bool ctrl = false, shift = false, alt = false;
        Key key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "cmd" or "control": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt" or "option": alt = true; break;
                default:
                    if (raw == "?") { key = Key.Slash; shift = true; break; }
                    key = raw switch { "`" => Key.Quoteleft, "/" => Key.Slash, _ => OS.FindKeycodeFromString(raw) };
                    if (key == Key.None && raw.StartsWith("Num", StringComparison.OrdinalIgnoreCase))
                    {
                        var rest = raw[3..].Trim();
                        key = rest == "." ? Key.KpPeriod : rest.Length == 1 && char.IsDigit(rest[0]) ? Key.Kp0 + (rest[0] - '0') : Key.None;
                    }
                    if (key == Key.None) return null;
                    break;
            }
        }
        return key == Key.None ? null : new Chord(key, ctrl, shift, alt);
    }

    /// <summary>Stable text used in settings files and JSON ("Ctrl+Shift+S").</summary>
    public string Serialize()
    {
        var name = Key switch
        {
            Key.Quoteleft => "`", Key.Slash => "/", Key.KpPeriod => "Num .",
            >= Key.Kp0 and <= Key.Kp9 => "Num " + (char)('0' + (Key - Key.Kp0)),
            _ => OS.GetKeycodeString(Key).Replace(" ", ""),
        };
        return (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "") + name;
    }

    /// <summary>Text for people: arrows as symbols, numpad as "Num 1".</summary>
    public string Display()
    {
        var name = Key switch
        {
            Key.Quoteleft => "`", Key.Slash => "/", Key.Left => "←", Key.Right => "→", Key.Up => "↑", Key.Down => "↓",
            Key.Escape => "Esc", Key.Delete => "Del", Key.KpPeriod => "Num .",
            >= Key.Kp0 and <= Key.Kp9 => "Num " + (char)('0' + (Key - Key.Kp0)),
            _ => OS.GetKeycodeString(Key),
        };
        return (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "") + name;
    }

    public bool Matches(InputEventKey k)
    {
        var code = k.Keycode != Key.None ? k.Keycode : k.PhysicalKeycode;
        if (k.IsCommandOrControlPressed() != Ctrl || k.AltPressed != Alt) return false;
        if (Key == Key.Slash && Shift) return k.ShiftPressed && code is Key.Slash or Key.Question;
        return code == Key && k.ShiftPressed == Shift;
    }

    public InputEventKey ToEvent() => new() { Keycode = Key, CtrlPressed = Ctrl, ShiftPressed = Shift, AltPressed = Alt, CommandOrControlAutoremap = Ctrl };
}

/// <summary>
/// The one table of keyboard shortcuts: action id, category, label and default chords, plus the user's changes (kept in
/// <see cref="AppSettings.Shortcuts"/>). Menus, tooltips, the key dispatcher, the editor and the cheat sheet all read from here.
/// </summary>
public sealed class ShortcutMap
{
    public static ShortcutMap Instance { get; } = new();

    static ShortcutAction A(string id, string cat, string label, string keys, ShortcutScope scope = ShortcutScope.Global, bool text = false) => new(id, cat, label, keys, scope, text);

    /// <summary>Category names double as translation keys.</summary>
    public static readonly IReadOnlyList<string> Categories = new[] { "File", "Edit", "Playback", "Keys", "Tools", "Posing", "View", "AI", "Help" };

    public static readonly IReadOnlyList<ShortcutAction> All = new[]
    {
        A("file.new", "File", "New project", "Ctrl+N", text: true),
        A("file.open", "File", "Open project", "Ctrl+O", text: true),
        A("file.save", "File", "Save", "Ctrl+S", text: true),
        A("file.saveAs", "File", "Save as", "Ctrl+Shift+S", text: true),
        A("file.export", "File", "Export animation", "Ctrl+E", text: true),
        A("file.quit", "File", "Quit", "Ctrl+Q", text: true),

        A("edit.undo", "Edit", "Undo", "Ctrl+Z"),
        A("edit.redo", "Edit", "Redo", "Ctrl+Shift+Z|Ctrl+Y"),
        A("edit.delete", "Edit", "Delete selected prop or clip", "Delete"),
        A("edit.duplicate", "Edit", "Duplicate clip", "Ctrl+D"),
        A("edit.selectAll", "Edit", "Select all keys", "A|Ctrl+A"),

        A("play.toggle", "Playback", "Play or pause", "Space"),
        A("play.next", "Playback", "Next frame", "Right"),
        A("play.prev", "Playback", "Previous frame", "Left"),
        A("play.next10", "Playback", "Jump 10 frames forward", "Shift+Right"),
        A("play.prev10", "Playback", "Jump 10 frames back", "Shift+Left"),
        A("play.nextKey", "Playback", "Next key", "Up|Alt+Right"),
        A("play.prevKey", "Playback", "Previous key", "Down|Alt+Left"),
        A("play.start", "Playback", "Go to the first frame", "Home", ShortcutScope.NotViewport),
        A("play.end", "Playback", "Go to the last frame", "End"),
        A("play.loop", "Playback", "Loop on or off", "L"),

        A("key.insert", "Keys", "Key the whole pose", "I|K"),
        A("key.insertSel", "Keys", "Key only the selected bone or control", "Shift+K|Shift+I"),
        A("key.delete", "Keys", "Delete keys at this frame", "Alt+I|Alt+K"),
        A("key.autoKey", "Keys", "Auto-key on or off", "Ctrl+K"),

        A("tool.select", "Tools", "Select tool", "Q"),
        A("tool.move", "Tools", "Move tool (IK control, pelvis)", "W"),
        A("tool.rotate", "Tools", "Rotate tool", "E"),
        A("tool.mirror", "Tools", "Mirror pose", "M"),
        A("tool.pin", "Tools", "Pin or unpin hand or foot", "Shift+P"),

        A("pose.resetBone", "Posing", "Reset the selected bone", "Alt+R"),
        A("pose.resetPose", "Posing", "Reset the whole pose", "Alt+G"),
        A("pose.copy", "Posing", "Copy pose", "Ctrl+C"),
        A("pose.paste", "Posing", "Paste pose", "Ctrl+V"),
        A("pose.pasteMirror", "Posing", "Paste pose mirrored", "Ctrl+Shift+V"),

        A("view.frameSel", "View", "Frame the selection", "F|Num .", ShortcutScope.Viewport),
        A("view.frameAll", "View", "Frame everything", "Home", ShortcutScope.Viewport),
        A("view.front", "View", "Front view", "Num 1", ShortcutScope.Viewport),
        A("view.side", "View", "Side view", "Num 3", ShortcutScope.Viewport),
        A("view.top", "View", "Top view", "Num 7", ShortcutScope.Viewport),
        A("view.ortho", "View", "Perspective or orthographic", "Num 5", ShortcutScope.Viewport),
        A("view.panels", "View", "Show or hide the side panels", "T"),
        A("view.inspector", "View", "Show or hide the inspector", "N"),
        A("view.mode", "View", "Switch Simple and Advanced", "`|Tab"),
        A("view.fullscreen", "View", "Full screen", "F11"),
        A("view.balance", "View", "Balance overlay", "B"),
        A("view.paths", "View", "Motion paths", "P"),
        A("view.onion", "View", "Onion skin", "O"),

        A("ai.generate", "AI", "Generate motion…", "Ctrl+G"),
        A("ai.templates", "AI", "Templates…", "Ctrl+T"),

        A("help.shortcuts", "Help", "Keyboard shortcuts", "F1|?", text: true),
        A("help.tutorial", "Help", "Tutorial", "Shift+F1", text: true),
        A("help.welcome", "Help", "Welcome", "", text: true),
    };

    static readonly Dictionary<string, ShortcutAction> ById = All.ToDictionary(a => a.Id);
    public static ShortcutAction? Get(string id) => ById.GetValueOrDefault(id);

    AppSettings? _settings;
    readonly Dictionary<string, List<Chord>> _chords = new();
    readonly List<(WeakRef c, string key, string action)> _hints = new();

    /// <summary>Raised after any binding changed (menus, tooltips and the dispatcher refresh).</summary>
    public event Action? Changed;

    /// <summary>True while the editor waits for a key to bind: the dispatcher stays quiet.</summary>
    public bool Capturing { get; set; }

    public void Attach(AppSettings settings) { _settings = settings; Rebuild(); }

    Dictionary<string, string> Overrides => _settings?.Shortcuts ?? _fallback;
    readonly Dictionary<string, string> _fallback = new();

    void Rebuild()
    {
        _chords.Clear();
        foreach (var a in All) _chords[a.Id] = Parse(Overrides.TryGetValue(a.Id, out var o) ? o : a.Defaults);
    }

    static List<Chord> Parse(string text) =>
        text.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(Chord.Parse).Where(c => c is not null).Select(c => c!.Value).ToList();

    public IReadOnlyList<Chord> ChordsOf(string id) => _chords.TryGetValue(id, out var l) ? l : Array.Empty<Chord>();
    public bool IsCustom(string id) => Overrides.ContainsKey(id);
    public string DefaultText(string id) => Get(id)?.Defaults ?? "";

    /// <summary>Shortcut text for the first chord ("Ctrl+S"), or "" when unbound.</summary>
    public string Display(string id) => ChordsOf(id) is { Count: > 0 } l ? l[0].Display() : "";

    /// <summary>All chords of an action as text, e.g. "Ctrl+Shift+Z / Ctrl+Y".</summary>
    public string DisplayAll(string id) => string.Join("  /  ", ChordsOf(id).Select(c => c.Display()));

    /// <summary>"Text (Ctrl+S)" or just "Text" when the action has no key.</summary>
    public string Tip(string text, string id) => Display(id) is { Length: > 0 } d ? $"{text} ({d})" : text;

    public Shortcut? ShortcutFor(string id)
    {
        if (ChordsOf(id).Count == 0) return null;
        var sc = new Shortcut();
        foreach (var c in ChordsOf(id)) sc.Events.Add(c.ToEvent());
        return sc;
    }

    /// <summary>The action a key press means in this focus context (a scope-specific action wins over a global one).</summary>
    public ShortcutAction? Find(InputEventKey k, bool inViewport)
    {
        ShortcutAction? best = null;
        foreach (var a in All)
        {
            if (a.Scope == ShortcutScope.Viewport && !inViewport) continue;
            if (a.Scope == ShortcutScope.NotViewport && inViewport) continue;
            if (!ChordsOf(a.Id).Any(c => c.Matches(k))) continue;
            if (best is null || (best.Scope == ShortcutScope.Global && a.Scope != ShortcutScope.Global)) best = a;
        }
        return best;
    }

    static bool Overlap(ShortcutScope a, ShortcutScope b) => a == ShortcutScope.Global || b == ShortcutScope.Global || a == b;

    /// <summary>Other actions that already use this chord in an overlapping context.</summary>
    public List<ShortcutAction> Conflicts(string id, Chord chord)
    {
        var scope = Get(id)?.Scope ?? ShortcutScope.Global;
        return All.Where(a => a.Id != id && Overlap(a.Scope, scope) && ChordsOf(a.Id).Contains(chord)).ToList();
    }

    /// <summary>Bind one chord to an action (replacing its others), or unbind with null. Actions that conflict lose that chord when <paramref name="replaceConflicts"/>.</summary>
    public void Set(string id, Chord? chord, bool replaceConflicts = false)
    {
        if (Get(id) is null) return;
        if (chord is { } c && replaceConflicts)
            foreach (var other in Conflicts(id, c))
                Overrides[other.Id] = string.Join("|", ChordsOf(other.Id).Where(x => x != c).Select(x => x.Serialize()));
        Overrides[id] = chord is { } ch ? ch.Serialize() : "";
        Commit();
    }

    public void Reset(string id) { Overrides.Remove(id); Commit(); }
    public void ResetAll() { Overrides.Clear(); Commit(); }

    void Commit()
    {
        Rebuild();
        _settings?.Save();
        Changed?.Invoke();
        RefreshHints();
    }

    /// <summary>Every pair of actions that share a chord in an overlapping context (for import warnings).</summary>
    public List<(ShortcutAction a, ShortcutAction b, Chord chord)> AllConflicts()
    {
        var list = new List<(ShortcutAction, ShortcutAction, Chord)>();
        for (var i = 0; i < All.Count; i++)
            for (var j = i + 1; j < All.Count; j++)
                if (Overlap(All[i].Scope, All[j].Scope))
                    foreach (var c in ChordsOf(All[i].Id)) if (ChordsOf(All[j].Id).Contains(c)) list.Add((All[i], All[j], c));
        return list;
    }

    // ---- JSON -------------------------------------------------------------------------------------

    public string ExportJson()
    {
        var map = new JsonObject();
        foreach (var a in All) map[a.Id] = string.Join("|", ChordsOf(a.Id).Select(c => c.Serialize()));
        return new JsonObject { ["format"] = "blazma-shortcuts", ["version"] = 1, ["shortcuts"] = map }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Import a file made by <see cref="ExportJson"/>. Returns the number of actions set, or -1 with an error when the file is not a shortcuts file.</summary>
    public int ImportJson(string json, out string error)
    {
        error = "";
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["shortcuts"] is not JsonObject map) { error = "not a shortcuts file"; return -1; }
            var staged = new Dictionary<string, string>();
            foreach (var (id, v) in map)
            {
                if (Get(id) is null || v is null) continue;
                var text = (string?)v ?? "";
                var chords = Parse(text);
                if (text.Length > 0 && chords.Count == 0) continue; // unreadable chord: keep what we have
                staged[id] = string.Join("|", chords.Select(c => c.Serialize()));
            }
            foreach (var (id, text) in staged)
            {
                if (text == string.Join("|", Parse(Get(id)!.Defaults).Select(c => c.Serialize()))) Overrides.Remove(id); else Overrides[id] = text;
            }
            Commit();
            return staged.Count;
        }
        catch (Exception e) when (e is JsonException or InvalidCastException or FormatException)
        {
            error = e.Message;
            return -1;
        }
    }

    // ---- tooltips that follow the bindings ----------------------------------------------------------

    /// <summary>Give a control a tooltip "text (shortcut)" that is rebuilt when the keys or the language change. <paramref name="textKey"/> is the English source text.</summary>
    public void Hint(Control c, string textKey, string actionId)
    {
        _hints.Add((GodotObject.WeakRef(c)!, textKey, actionId));
        c.TooltipText = Tip(TranslationServer.Translate(textKey), actionId);
    }

    public void RefreshHints()
    {
        _hints.RemoveAll(h => h.c.GetRef().AsGodotObject() is not Control c || !GodotObject.IsInstanceValid(c));
        foreach (var (w, key, action) in _hints)
            if (w.GetRef().AsGodotObject() is Control c) c.TooltipText = Tip(TranslationServer.Translate(key), action);
    }
}
