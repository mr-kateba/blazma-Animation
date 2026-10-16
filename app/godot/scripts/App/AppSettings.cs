using Godot;

namespace Blazma.UI.App;

/// <summary>Per-user preferences stored in user://settings.cfg (never inside projects).</summary>
public sealed class AppSettings
{
    const string Path = "user://settings.cfg";
    const int MaxRecent = 10;

    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "dark";
    public bool AdvancedMode { get; set; }
    public bool ShowSkeleton { get; set; } = true;
    /// <summary>Draw the skeleton overlay on every character instead of only the selected one.</summary>
    public bool OverlayAll { get; set; }
    public int AutosaveMinutes { get; set; } = 3;
    /// <summary>Viewport extras (Phase 3): motion trail of the selected bone and onion-skin ghosts.</summary>
    public bool ShowTrail { get; set; }
    public bool ShowOnion { get; set; }
    public int OnionFrames { get; set; } = 5;
    public List<string> RecentFiles { get; } = new();

    /// <summary>Phase 5: AI agents (Claude Code etc.) may connect to this window over the local pipe.</summary>
    public bool AllowAgents { get; set; } = true;
    /// <summary>False = Assisted (every agent task waits for review), true = Autonomous.</summary>
    public bool AgentAutonomous { get; set; }
    /// <summary>Folders agents may import files from besides the project's own folder.</summary>
    public List<string> AgentReadRoots { get; } = new();

    /// <summary>Free-form layout values (panel sizes, window size) owned by the views.</summary>
    public Dictionary<string, Variant> Layout { get; } = new();

    public void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(Path) != Error.Ok) return;
        Language = (string)cfg.GetValue("ui", "language", Language) is "ar" ? "ar" : "en";
        Theme = (string)cfg.GetValue("ui", "theme", Theme) is "light" ? "light" : "dark";
        AdvancedMode = (bool)cfg.GetValue("ui", "advanced", AdvancedMode);
        ShowSkeleton = (bool)cfg.GetValue("ui", "show_skeleton", ShowSkeleton);
        OverlayAll = (bool)cfg.GetValue("ui", "overlay_all", OverlayAll);
        ShowTrail = (bool)cfg.GetValue("ui", "show_trail", ShowTrail);
        ShowOnion = (bool)cfg.GetValue("ui", "show_onion", ShowOnion);
        OnionFrames = Math.Clamp((int)cfg.GetValue("ui", "onion_frames", OnionFrames), 1, 30);
        AutosaveMinutes = Math.Clamp((int)cfg.GetValue("files", "autosave_minutes", AutosaveMinutes), 0, 60);
        RecentFiles.Clear();
        RecentFiles.AddRange(((string[])cfg.GetValue("files", "recent", Array.Empty<string>())).Take(MaxRecent));
        AllowAgents = (bool)cfg.GetValue("agent", "allow", AllowAgents);
        AgentAutonomous = (bool)cfg.GetValue("agent", "autonomous", AgentAutonomous);
        AgentReadRoots.Clear();
        AgentReadRoots.AddRange((string[])cfg.GetValue("agent", "read_roots", Array.Empty<string>()));
        if (cfg.HasSection("layout"))
            foreach (var key in cfg.GetSectionKeys("layout"))
                Layout[key] = cfg.GetValue("layout", key);
    }

    public void Save()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("ui", "language", Language);
        cfg.SetValue("ui", "theme", Theme);
        cfg.SetValue("ui", "advanced", AdvancedMode);
        cfg.SetValue("ui", "show_skeleton", ShowSkeleton);
        cfg.SetValue("ui", "overlay_all", OverlayAll);
        cfg.SetValue("ui", "show_trail", ShowTrail);
        cfg.SetValue("ui", "show_onion", ShowOnion);
        cfg.SetValue("ui", "onion_frames", OnionFrames);
        cfg.SetValue("files", "autosave_minutes", AutosaveMinutes);
        cfg.SetValue("files", "recent", RecentFiles.ToArray());
        cfg.SetValue("agent", "allow", AllowAgents);
        cfg.SetValue("agent", "autonomous", AgentAutonomous);
        cfg.SetValue("agent", "read_roots", AgentReadRoots.ToArray());
        foreach (var (k, v) in Layout) cfg.SetValue("layout", k, v);
        cfg.Save(Path);
    }

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecent) RecentFiles.RemoveRange(MaxRecent, RecentFiles.Count - MaxRecent);
        Save();
    }
}
