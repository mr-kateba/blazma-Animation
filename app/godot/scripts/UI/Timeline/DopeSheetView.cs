using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Dope sheet: groups (Body, Arms, Legs, IK, Other) and one row per bone or control, with the union of its channels'
/// key times. Always left to right; the name column stays on the left even in Arabic. Every edit is a command.
/// </summary>
public partial class DopeSheetView : Control
{
    public const float NameW = 196, RulerH = 24, RowH = 22;
    static readonly string[] GroupOrder = { "Body", "Arms", "Legs", "IK", "Other" };

    public sealed class Row
    {
        public bool IsGroup;
        public string Group = "";
        public string? TargetId;
        public string Name = "";
        public List<Track> Tracks = new();
        public List<string> Members = new();
        public SortedSet<long> Frames = new();
        public bool Locked, Muted;
    }

    readonly MainWindow _host;
    readonly FrameAxis _axis = new();
    readonly PopupMenu _menu = new();
    readonly PopupMenu _interp = new() { Name = "Interpolation" };
    readonly HashSet<string> _collapsed = new();
    readonly HashSet<(string target, long frame)> _sel = new();
    List<Row> _rows = new();
    ulong _builtVersion = ulong.MaxValue;
    string? _builtClip;
    bool _rowsDirty = true, _userZoom;
    float _scrollY;
    string? _clipboard;

    enum Mode { None, Scrub, Move, Box, Pan }
    Mode _mode;
    Vector2 _pressPos;
    Vector2 _boxEnd;
    bool _boxAdd;
    int _delta;
    long _anchorFrame;
    (int row, long frame)? _hover;

    static AppController App => AppController.Instance;
    Palette P => _host.Palette;

    public IReadOnlyList<Row> Rows { get { EnsureRows(); return _rows; } }
    public IReadOnlyCollection<(string target, long frame)> Selection => _sel;
    public int Delta => _delta;
    public PopupMenu InterpMenu => _interp;
    public float RowCenter(int i) => RowY(i) + RowH / 2;
    public float ScrollY => _scrollY;
    public void EnsureRowVisible(int i)
    {
        EnsureRows();
        var top = i * RowH;
        if (top < _scrollY) _scrollY = top;
        else if (top + RowH > _scrollY + Size.Y - RulerH) _scrollY = top + RowH - (Size.Y - RulerH);
        QueueRedraw();
    }
    public FrameAxis Axis => _axis;

    public DopeSheetView(MainWindow host)
    {
        _host = host;
        LayoutDirection = LayoutDirectionEnum.Ltr;
        FocusMode = FocusModeEnum.Click;
        ClipContents = true;
        CustomMinimumSize = new Vector2(300, 120);
        _menu.AddChild(_interp);
        _interp.AddItem("Step", 1);
        _interp.AddItem("Linear", 2);
        _interp.AddItem("Cubic", 3);
        _interp.IdPressed += id => ApplyInterpolation(id switch { 1 => Interpolation.Step, 2 => Interpolation.Linear, _ => Interpolation.Cubic });
        _menu.AddSubmenuNodeItem("Interpolation", _interp);
        _menu.AddItem("Copy keys", 10);
        _menu.AddItem("Paste keys at playhead", 11);
        _menu.AddItem("Delete", 20);
        _menu.IdPressed += id =>
        {
            if (id == 10) CopySelection();
            else if (id == 11) PasteAtPlayhead();
            else if (id == 20) DeleteSelection();
        };
        AddChild(_menu);
        App.PlaybackChanged += QueueRedraw;
        App.ActiveClipChanged += () => { _sel.Clear(); _rowsDirty = true; QueueRedraw(); };
        App.SessionChanged += a =>
        {
            if (a.Changes.Any(c => c.Kind is Change.ClipChanged or Change.ClipAdded or Change.ClipRemoved or Change.ProjectReplaced
                or Change.RigChanged or Change.CharacterAdded)) { _rowsDirty = true; QueueRedraw(); }
        };
        _host.Selection.Changed += () => { _rowsDirty = true; QueueRedraw(); };
        MouseEntered += () => { if (!HasFocus() && GetViewport().GuiGetFocusOwner() is not LineEdit) GrabFocus(); };
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged) { _rowsDirty = true; QueueRedraw(); }
    }

    // ---- model --------------------------------------------------------------------------------------------------------------

    void EnsureRows()
    {
        var clip = App.ActiveClip;
        var id = clip?.Id.ToString();
        if (!_rowsDirty && _builtVersion == App.ClipVersion && _builtClip == id) return;
        _rowsDirty = false; _builtVersion = App.ClipVersion; _builtClip = id;
        _rows = new List<Row>();
        if (clip is null) return;
        var rig = _host.SelectedRig ?? _host.Characters.Views.Values.Select(v => v.Rig).FirstOrDefault(r => r.Id == clip.Skeleton);
        string NameOf(Track t) =>
            t.Target.Kind == TargetKind.Control ? rig?.FindControl(t.Target.Id)?.Name ?? "?" : rig?.FindBone(t.Target.Id)?.Name ?? "?";
        int OrderOf(Track t) => t.Target.Kind == TargetKind.Control
            ? 10000 + (rig?.Controls.FindIndex(c => c.Id == t.Target.Id) ?? 0)
            : rig?.Bones.FindIndex(b => b.Id == t.Target.Id) ?? 0;

        var byTarget = clip.Tracks.GroupBy(t => t.Target.Id.ToString()).Select(g => (id: g.Key, tracks: g.ToList())).ToList();
        foreach (var group in GroupOrder)
        {
            var members = byTarget.Where(x => (x.tracks[0].Group is { Length: > 0 } g ? g : "Other") is var gn && (GroupOrder.Contains(gn) ? gn : "Other") == group)
                .OrderBy(x => OrderOf(x.tracks[0])).ToList();
            if (members.Count == 0) continue;
            var header = new Row { IsGroup = true, Group = group, Name = group };
            foreach (var m in members)
            {
                header.Tracks.AddRange(m.tracks);
                header.Members.Add(m.id);
            }
            header.Frames = ClipKeys.Frames(clip, header.Tracks);
            header.Locked = header.Tracks.All(t => t.Locked);
            header.Muted = header.Tracks.All(t => t.Muted);
            _rows.Add(header);
            if (_collapsed.Contains(group)) continue;
            foreach (var m in members)
                _rows.Add(new Row
                {
                    Group = group, TargetId = m.id, Name = NameOf(m.tracks[0]), Tracks = m.tracks,
                    Frames = ClipKeys.Frames(clip, m.tracks), Locked = m.tracks.All(t => t.Locked), Muted = m.tracks.All(t => t.Muted),
                });
        }
        _sel.RemoveWhere(s => !clip.Tracks.Any(t => t.Target.Id.ToString() == s.target && t.Keys.Any(k => ClipKeys.FrameOf(clip, k) == s.frame)));
    }

    void Layout()
    {
        _axis.Left = NameW;
        var last = Math.Max(1, App.FrameCount - 1);
        if (!_userZoom) _axis.Fit(0, last, Size.X - NameW, 18);
        else _axis.Left = NameW;
    }

    float RowY(int i) => RulerH + i * RowH - _scrollY;
    float MaxScroll => Math.Max(0, _rows.Count * RowH - (Size.Y - RulerH) + 4);

    // ---- drawing ----------------------------------------------------------------------------------------------------------

    public override void _Draw()
    {
        EnsureRows();
        Layout();
        var p = P;
        var font = BlazmaTheme.Regular;
        var mono = BlazmaTheme.Mono;
        var clip = App.ActiveClip;
        var w = Size.X; var h = Size.Y;
        _scrollY = Mathf.Clamp(_scrollY, 0, MaxScroll);
        var last = Math.Max(0, App.FrameCount - 1);

        DrawRect(new Rect2(0, 0, w, h), p.Panel2 with { A = 0.35f });
        DrawRect(new Rect2(_axis.X(0), RulerH, Math.Max(1, _axis.X(last) - _axis.X(0)), h), p.Panel with { A = 0.55f });

        // vertical frame grid
        var step = _axis.TickStep();
        for (var f = 0L; f <= last; f++)
        {
            var x = _axis.X(f);
            if (x < NameW || x > w) continue;
            var major = f % step == 0;
            if (major) DrawLine(new Vector2(x, RulerH), new Vector2(x, h), p.Line with { A = 0.55f });
            else if (_axis.Ppf >= 9) DrawLine(new Vector2(x, RulerH), new Vector2(x, h), p.Line with { A = 0.18f });
        }

        // rows
        var selTarget = _host.Selection.BoneId ?? _host.Selection.ControlId;
        if (clip is null)
        {
            DrawString(font, new Vector2(NameW + 14, RulerH + 26), TranslationServer.Translate("No keys yet. Press Key to record the pose."), HorizontalAlignment.Left, -1, 12, p.Text3);
        }
        for (var i = 0; i < _rows.Count; i++)
        {
            var y = RowY(i);
            if (y + RowH < RulerH || y > h) continue;
            var row = _rows[i];
            var isSelRow = !row.IsGroup && row.TargetId == selTarget;
            if (row.IsGroup) DrawRect(new Rect2(0, y, w, RowH), p.Raised with { A = 0.7f });
            else if (isSelRow) DrawRect(new Rect2(0, y, w, RowH), p.AccentSoft);
            DrawLine(new Vector2(0, y + RowH), new Vector2(w, y + RowH), p.Line with { A = 0.5f });

            // name column
            DrawRect(new Rect2(0, y, NameW - 4, RowH), (row.IsGroup ? p.Raised : isSelRow ? p.AccentSoft.Lerp(p.Panel, 0.2f) : p.Panel));
            var dim = row.Locked || row.Muted;
            if (row.IsGroup)
            {
                var open = !_collapsed.Contains(row.Group);
                var c = new Vector2(12, y + RowH / 2);
                var tri = open
                    ? new[] { c + new Vector2(-4, -2.5f), c + new Vector2(4, -2.5f), c + new Vector2(0, 3) }
                    : new[] { c + new Vector2(-2.5f, -4), c + new Vector2(3, 0), c + new Vector2(-2.5f, 4) };
                DrawColoredPolygon(tri, p.Text2);
                DrawString(font, new Vector2(24, y + 15), TlText.Fit(font, TranslationServer.Translate(row.Group), 12, NameW - 40), HorizontalAlignment.Left, -1, 12, p.Text);
            }
            else
            {
                DrawString(font, new Vector2(24, y + 15), TlText.Fit(font, row.Name, 12, NameW - 80), HorizontalAlignment.Left, -1, 12,
                    isSelRow ? p.Text : (dim ? p.Text3 : p.Text2), direction: TextServer.Direction.Ltr);
                DrawIcon(UiIcons.Make(UiIcons.Lock, false), new Vector2(NameW - 50, y + 3), row.Locked ? p.Warn : p.Text3 with { A = 0.55f });
                DrawIcon(UiIcons.Make(row.Muted ? UiIcons.MuteOff : UiIcons.Mute, false), new Vector2(NameW - 28, y + 3), row.Muted ? p.Warn : p.Text3 with { A = 0.55f });
            }

            // keys
            foreach (var f in row.Frames)
            {
                var selected = IsSelected(row, f);
                var drawF = selected && _mode == Mode.Move ? f + _delta : f;
                var x = _axis.X(drawF);
                if (x < NameW - 6 || x > w + 6) continue;
                var interp = ClipKeys.InterpAt(clip!, row.Tracks, f);
                var alpha = dim ? 0.45f : 1f;
                var fill = selected ? p.Accent : (row.IsGroup ? p.Text : p.Text2);
                var hover = _hover is { } hv && hv.row == i && hv.frame == f;
                if (hover && !selected) fill = fill.Lightened(0.25f);
                KeyGlyph.Draw(this, new Vector2(x, y + RowH / 2), selected ? 5.6f : 5f, interp, fill with { A = alpha }, (selected ? p.Text : p.Bg) with { A = alpha }, selected ? 1.5f : 1.2f);
            }
        }

        // ruler (drawn last so scrolled rows go under it)
        DrawRect(new Rect2(0, 0, w, RulerH), p.Panel);
        DrawRect(new Rect2(0, 0, NameW - 4, RulerH), p.Panel);
        DrawLine(new Vector2(0, RulerH), new Vector2(w, RulerH), p.LineStrong);
        DrawLine(new Vector2(NameW - 4, 0), new Vector2(NameW - 4, h), p.Line);
        for (var f = 0L; f <= last; f++)
        {
            var x = _axis.X(f);
            if (x < NameW || x > w) continue;
            var major = f % step == 0;
            if (!major && _axis.Ppf < 7) continue;
            DrawLine(new Vector2(x, RulerH - (major ? 7 : 3)), new Vector2(x, RulerH), p.LineStrong);
            if (major) DrawString(mono, new Vector2(x + 3, 12), TlText.Frame(f), HorizontalAlignment.Left, -1, 10, p.Text3);
        }
        DrawString(font, new Vector2(12, 16), TranslationServer.Translate(_sel.Count == 0 ? "Channels" : "Keys selected") + (_sel.Count == 0 ? "" : ": " + _sel.Count), HorizontalAlignment.Left, -1, 11, p.Text3);

        if (clip is { Loop.Enabled: true })
        {
            var (ls, le) = AppController.LoopFrames(clip);
            var lx0 = _axis.X(ls); var lx1 = _axis.X(Math.Max(ls, le - 1));
            DrawRect(new Rect2(lx0, RulerH - 4, Math.Max(2, lx1 - lx0), 4), p.Accent with { A = 0.7f });
        }

        // box selection
        if (_mode == Mode.Box)
        {
            var r = BoxRect();
            DrawRect(r, p.Accent with { A = 0.12f });
            DrawRect(r, p.Accent, false, 1f);
        }

        // playhead
        var px = _axis.X(App.CurrentFrameExact);
        if (px >= NameW - 1 && px <= w)
        {
            DrawLine(new Vector2(px, 0), new Vector2(px, h), p.Accent, 1.5f);
            var label = TlText.Frame(App.CurrentFrame);
            var tw = mono.GetStringSize(label, HorizontalAlignment.Left, -1, 10).X;
            DrawStyleBox(BlazmaTheme.Box(p.Accent, null, 3, 0, 0, 0, 0, 0), new Rect2(px - tw / 2 - 4, 1, tw + 8, 15));
            DrawString(mono, new Vector2(px - tw / 2, 12.5f), label, HorizontalAlignment.Left, -1, 10, p.OnAccent);
        }
    }

    void DrawIcon(Texture2D tex, Vector2 pos, Color color) => DrawTexture(tex, pos, color);

    bool IsSelected(Row row, long frame)
    {
        if (!row.IsGroup) return row.TargetId is not null && _sel.Contains((row.TargetId, frame));
        var any = false;
        foreach (var m in row.Members)
        {
            var clip = App.ActiveClip!;
            var has = clip.Tracks.Any(t => t.Target.Id.ToString() == m && t.Keys.Any(k => ClipKeys.FrameOf(clip, k) == frame));
            if (!has) continue;
            if (!_sel.Contains((m, frame))) return false;
            any = true;
        }
        return any;
    }

    Rect2 BoxRect()
    {
        var a = _pressPos; var b = _boxEnd;
        return new Rect2(new Vector2(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Vector2(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)));
    }

    // ---- hit testing --------------------------------------------------------------------------------------------------------

    (int row, long frame)? KeyAt(Vector2 pos)
    {
        if (pos.X < NameW - 4 || pos.Y < RulerH) return null;
        for (var i = 0; i < _rows.Count; i++)
        {
            var y = RowY(i);
            if (pos.Y < y || pos.Y >= y + RowH) continue;
            foreach (var f in _rows[i].Frames)
            {
                var x = _axis.X(IsSelected(_rows[i], f) && _mode == Mode.Move ? f + _delta : f);
                if (Math.Abs(x - pos.X) <= 7) return (i, f);
            }
        }
        return null;
    }

    int RowAt(float y)
    {
        var i = (int)Math.Floor((y - RulerH + _scrollY) / RowH);
        return i >= 0 && i < _rows.Count ? i : -1;
    }

    // ---- selection ------------------------------------------------------------------------------------------------------------

    IEnumerable<(string target, long frame)> KeysOf(Row row, long frame)
    {
        var clip = App.ActiveClip!;
        var targets = row.IsGroup ? row.Members : new List<string> { row.TargetId! };
        foreach (var t in targets)
            if (clip.Tracks.Any(tr => tr.Target.Id.ToString() == t && tr.Keys.Any(k => ClipKeys.FrameOf(clip, k) == frame)))
                yield return (t, frame);
    }

    public void SelectKey(string targetId, long frame, bool add)
    {
        if (!add) _sel.Clear();
        _sel.Add((targetId, frame));
        QueueRedraw();
    }

    public void ClearSelection() { _sel.Clear(); QueueRedraw(); }

    public void SelectAll()
    {
        EnsureRows();
        _sel.Clear();
        foreach (var r in _rows.Where(r => !r.IsGroup))
            foreach (var f in r.Frames) _sel.Add((r.TargetId!, f));
        QueueRedraw();
    }

    // ---- input -----------------------------------------------------------------------------------------------------------------------

    public override void _GuiInput(InputEvent e)
    {
        EnsureRows();
        Layout();
        switch (e)
        {
            case InputEventKey k when k.Pressed && !k.Echo:
                if (GetViewport().GuiGetFocusOwner() is LineEdit) return;
                if (HandleKey(k)) AcceptEvent();
                break;
            case InputEventMouseButton mb:
                HandleButton(mb);
                break;
            case InputEventMouseMotion mm:
                HandleMotion(mm);
                break;
        }
    }

    bool HandleKey(InputEventKey k)
    {
        if (k.CtrlPressed || k.MetaPressed)
        {
            switch (k.Keycode)
            {
                case Godot.Key.C: CopySelection(); return true;
                case Godot.Key.V: PasteAtPlayhead(); return true;
                case Godot.Key.A: SelectAll(); return true;
            }
            return false;
        }
        if (k.Keycode == Godot.Key.Delete || k.Keycode == Godot.Key.Backspace) { DeleteSelection(); return true; }
        if (k.Keycode == Godot.Key.F && !k.ShiftPressed && !k.AltPressed) { _userZoom = false; QueueRedraw(); return true; }
        return TransportKeys.Handle(_host, k);
    }

    void HandleButton(InputEventMouseButton mb)
    {
        var pos = mb.Position;
        switch (mb.ButtonIndex)
        {
            case MouseButton.WheelUp or MouseButton.WheelDown:
            {
                var dir = mb.ButtonIndex == MouseButton.WheelUp ? 1 : -1;
                if (mb.CtrlPressed) { _axis.ZoomAt(pos.X, dir > 0 ? 1.15 : 1 / 1.15); _userZoom = true; }
                else if (mb.ShiftPressed) { _axis.Pan(dir * 40); _userZoom = true; }
                else _scrollY = Mathf.Clamp(_scrollY - dir * RowH * 2, 0, MaxScroll);
                QueueRedraw();
                AcceptEvent();
                break;
            }
            case MouseButton.Middle:
                if (mb.Pressed) { _mode = Mode.Pan; _pressPos = pos; } else if (_mode == Mode.Pan) _mode = Mode.None;
                AcceptEvent();
                break;
            case MouseButton.Left:
                if (mb.Pressed) Press(mb); else Release();
                AcceptEvent();
                break;
            case MouseButton.Right when mb.Pressed:
                ContextAt(mb);
                AcceptEvent();
                break;
        }
    }

    void Press(InputEventMouseButton mb)
    {
        GrabFocus();
        var pos = mb.Position;
        _pressPos = pos;
        var add = mb.CtrlPressed || mb.ShiftPressed || mb.MetaPressed;
        if (pos.Y < RulerH)
        {
            if (pos.X >= NameW) { _mode = Mode.Scrub; App.GoToFrame(FrameFromX(pos.X)); }
            return;
        }
        if (pos.X < NameW - 4) { NamePress(pos); return; }
        if (KeyAt(pos) is { } hit)
        {
            var row = _rows[hit.row];
            var keys = KeysOf(row, hit.frame).ToList();
            var already = keys.All(k => _sel.Contains(k));
            if (add && already) { foreach (var k in keys) _sel.Remove(k); }
            else
            {
                if (!add && !already) _sel.Clear();
                foreach (var k in keys) _sel.Add(k);
                _mode = Mode.Move;
                _anchorFrame = hit.frame;
                _delta = 0;
            }
            QueueRedraw();
            return;
        }
        _mode = Mode.Box;
        _boxEnd = pos;
        _boxAdd = add;
        QueueRedraw();
    }

    long FrameFromX(float x) => Math.Clamp((long)Math.Round(_axis.FrameAt(x)), 0, Math.Max(0, App.FrameCount - 1));

    void NamePress(Vector2 pos)
    {
        var i = RowAt(pos.Y);
        if (i < 0) return;
        var row = _rows[i];
        if (row.IsGroup)
        {
            if (!_collapsed.Remove(row.Group)) _collapsed.Add(row.Group);
            _rowsDirty = true;
            QueueRedraw();
            return;
        }
        if (pos.X >= NameW - 54 && pos.X < NameW - 32) { ToggleFlag(row, locked: true); return; }
        if (pos.X >= NameW - 32 && pos.X < NameW - 8) { ToggleFlag(row, locked: false); return; }
        SelectRowTarget(row);
    }

    void SelectRowTarget(Row row)
    {
        if (row.TargetId is null || _host.Selection.CharacterId is not { } ch) return;
        var rig = _host.SelectedRig;
        if (rig?.FindBone(Blazma.Core.Ids.ObjectId.Parse(row.TargetId)) is not null) _host.Selection.SelectBone(ch, row.TargetId);
        else _host.Selection.SelectControl(ch, row.TargetId);
    }

    /// <summary>Lock or mute every channel of a bone/control (track.set_flags per track, one undo step).</summary>
    public bool ToggleFlag(Row row, bool locked)
    {
        var clip = App.ActiveClip;
        if (clip is null) return false;
        var value = locked ? !row.Locked : !row.Muted;
        using var tx = App.BeginTransaction(locked ? "Lock channels" : "Mute channels");
        foreach (var t in row.Tracks)
            App.Execute("track.set_flags", new JsonObject { ["clipId"] = clip.Id.ToString(), ["trackId"] = t.Id.ToString(), [locked ? "locked" : "muted"] = value });
        tx.Commit();
        _rowsDirty = true;
        if (!locked) App.ApplyFrameIfClean();
        QueueRedraw();
        return true;
    }

    void HandleMotion(InputEventMouseMotion mm)
    {
        var pos = mm.Position;
        switch (_mode)
        {
            case Mode.Scrub:
                if ((mm.ButtonMask & MouseButtonMask.Left) == 0) { _mode = Mode.None; break; }
                App.GoToFrame(FrameFromX(pos.X));
                break;
            case Mode.Move:
                if ((mm.ButtonMask & MouseButtonMask.Left) == 0) { Release(); break; }
                var minFrame = _sel.Min(s => s.frame);
                _delta = (int)Math.Max(-minFrame, Math.Round(_axis.FrameAt(pos.X) - _anchorFrame));
                QueueRedraw();
                break;
            case Mode.Box:
                _boxEnd = pos;
                QueueRedraw();
                break;
            case Mode.Pan:
                _axis.Pan(pos.X - _pressPos.X);
                _scrollY = Mathf.Clamp(_scrollY - (pos.Y - _pressPos.Y), 0, MaxScroll);
                _pressPos = pos;
                _userZoom = true;
                QueueRedraw();
                break;
            default:
                var h = KeyAt(pos);
                if (h != _hover) { _hover = h; QueueRedraw(); }
                break;
        }
    }

    void Release()
    {
        var mode = _mode;
        _mode = Mode.None;
        if (mode == Mode.Move)
        {
            var d = _delta; _delta = 0;
            if (d != 0) MoveSelection(d);
        }
        else if (mode == Mode.Box)
        {
            var r = BoxRect();
            if (r.Size.X < 3 && r.Size.Y < 3) { if (!_boxAdd) _sel.Clear(); }
            else
            {
                if (!_boxAdd) _sel.Clear();
                for (var i = 0; i < _rows.Count; i++)
                {
                    var y = RowY(i);
                    if (y + RowH < r.Position.Y || y > r.End.Y) continue;
                    foreach (var f in _rows[i].Frames)
                    {
                        var x = _axis.X(f);
                        if (x >= r.Position.X && x <= r.End.X) foreach (var k in KeysOf(_rows[i], f)) _sel.Add(k);
                    }
                }
            }
        }
        QueueRedraw();
    }

    void ContextAt(InputEventMouseButton mb)
    {
        if (KeyAt(mb.Position) is { } hit)
        {
            var keys = KeysOf(_rows[hit.row], hit.frame).ToList();
            if (!keys.All(k => _sel.Contains(k))) { _sel.Clear(); foreach (var k in keys) _sel.Add(k); QueueRedraw(); }
        }
        else if (_sel.Count == 0) return;
        _menu.SetItemDisabled(_menu.GetItemIndex(11), _clipboard is null);
        _menu.Position = (Vector2I)mb.GlobalPosition + GetWindow().Position;
        _menu.Popup();
    }

    // ---- operations (shared by input, the context menu and tests) --------------------------------------------------------

    /// <summary>Selected keys by bone/control, skipping locked channels.</summary>
    Dictionary<string, List<long>> SelectionByTarget()
    {
        var clip = App.ActiveClip;
        var result = new Dictionary<string, List<long>>();
        if (clip is null) return result;
        foreach (var (target, frame) in _sel)
        {
            var tracks = clip.Tracks.Where(t => t.Target.Id.ToString() == target).ToList();
            if (tracks.Count == 0 || tracks.All(t => t.Locked)) continue;
            if (!result.TryGetValue(target, out var list)) result[target] = list = new List<long>();
            list.Add(frame);
        }
        return result;
    }

    /// <summary>One command per distinct set of frames (key.* commands take frames x bones, so equal sets share a call).</summary>
    IEnumerable<(string frames, string bones, List<long> list)> Batches()
    {
        foreach (var g in SelectionByTarget().GroupBy(kv => string.Join(",", kv.Value.Distinct().OrderBy(f => f))))
            yield return (g.Key, string.Join(",", g.Select(kv => kv.Key)), g.First().Value.Distinct().OrderBy(f => f).ToList());
    }

    public bool MoveSelection(int delta)
    {
        var clip = App.ActiveClip;
        if (clip is null || delta == 0 || _sel.Count == 0) return false;
        var batches = Batches().ToList();
        if (batches.Count == 0) return false;
        var ok = true;
        using (var tx = App.BeginTransaction("Move keys"))
        {
            foreach (var b in batches)
                ok &= App.Execute("key.move", new JsonObject { ["clipId"] = clip.Id.ToString(), ["frames"] = b.frames, ["delta"] = delta, ["bones"] = b.bones }).Ok;
            tx.Commit();
        }
        var moved = _sel.Where(s => SelectionByTarget().ContainsKey(s.target)).ToList();
        foreach (var s in moved) { _sel.Remove(s); _sel.Add((s.target, s.frame + delta)); }
        _rowsDirty = true;
        App.ApplyFrameIfClean();
        QueueRedraw();
        return ok;
    }

    public bool DeleteSelection()
    {
        var clip = App.ActiveClip;
        if (clip is null || _sel.Count == 0) return false;
        var batches = Batches().ToList();
        if (batches.Count == 0) { App.RaiseInfo(TranslationServer.Translate("The selected keys are on locked channels.")); return false; }
        var ok = true;
        using (var tx = App.BeginTransaction("Delete keys"))
        {
            foreach (var b in batches)
                ok &= App.Execute("key.delete", new JsonObject { ["clipId"] = clip.Id.ToString(), ["frames"] = b.frames, ["bones"] = b.bones }).Ok;
            tx.Commit();
        }
        _sel.Clear();
        _rowsDirty = true;
        App.ApplyFrameIfClean();
        QueueRedraw();
        return ok;
    }

    public bool ApplyInterpolation(Interpolation interp)
    {
        var clip = App.ActiveClip;
        if (clip is null || _sel.Count == 0) return false;
        var name = interp switch { Interpolation.Step => "step", Interpolation.Linear => "linear", _ => "cubic" };
        var ok = true;
        using (var tx = App.BeginTransaction("Set interpolation"))
        {
            foreach (var b in Batches())
                ok &= App.Execute("key.set_interpolation", new JsonObject { ["clipId"] = clip.Id.ToString(), ["interpolation"] = name, ["frames"] = b.frames, ["bones"] = b.bones }).Ok;
            tx.Commit();
        }
        _rowsDirty = true;
        App.ApplyFrameIfClean();
        QueueRedraw();
        return ok;
    }

    public bool CopySelection()
    {
        var clip = App.ActiveClip;
        if (clip is null || _sel.Count == 0)
        {
            App.RaiseInfo(TranslationServer.Translate("Select some keys first."));
            return false;
        }
        var batches = Batches().ToList();
        if (batches.Count == 0) return false;
        var globalMin = batches.Min(b => b.list.Min());
        var merged = new JsonArray();
        string fps = "";
        foreach (var b in batches)
        {
            var r = App.Execute("key.copy", new JsonObject { ["clipId"] = clip.Id.ToString(), ["frames"] = b.frames, ["bones"] = b.bones });
            if (!r.Ok || (string?)r.Data?["keysJson"] is not { } json) return false;
            var doc = JsonNode.Parse(json)!.AsObject();
            fps = (string)doc["fps"]!;
            var offset = b.list.Min() - globalMin;
            foreach (var key in doc["keys"]!.AsArray().ToList())
            {
                var o = key!.AsObject();
                o["frame"] = (long)o["frame"]! + offset;
                doc["keys"]!.AsArray().Remove(key);
                merged.Add(key);
            }
        }
        _clipboard = new JsonObject { ["fps"] = fps, ["keys"] = merged }.ToJsonString();
        App.RaiseInfo(string.Format(TranslationServer.Translate("Copied {0} keys."), merged.Count));
        return true;
    }

    public bool PasteAtPlayhead()
    {
        var clip = App.ActiveClip;
        if (clip is null) return false;
        if (_clipboard is null) { App.RaiseInfo(TranslationServer.Translate("Nothing copied yet. Select keys and press Ctrl+C.")); return false; }
        var r = App.Execute("key.paste", new JsonObject { ["clipId"] = clip.Id.ToString(), ["keysJson"] = _clipboard, ["frame"] = App.CurrentFrame });
        _rowsDirty = true;
        if (r.Ok) App.ApplyFrameIfClean();
        QueueRedraw();
        return r.Ok;
    }

    public bool HasClipboard => _clipboard is not null;

    // ---- test hooks ---------------------------------------------------------------------------------------------------------------

    public Vector2? KeyScreenPos(string targetId, long frame)
    {
        EnsureRows(); Layout();
        for (var i = 0; i < _rows.Count; i++)
            if (!_rows[i].IsGroup && _rows[i].TargetId == targetId && _rows[i].Frames.Contains(frame))
                return new Vector2(_axis.X(frame), RowY(i) + RowH / 2);
        return null;
    }

    public void EmptySpacePos(out Vector2 pos) { pos = new Vector2(NameW + 20, RulerH + RowH * _rows.Count + 12); }
}
