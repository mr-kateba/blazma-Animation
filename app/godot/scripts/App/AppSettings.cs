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
    public int AutosaveMinutes { get; set; } = 3;
    public List<string> RecentFiles { get; } = new();

    /// <summary>Free-form layout values (panel sizes, window size) owned by the views.</summary>
    public Dictionary<string, Variant> Layout { get; } = new();

    public void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(Path) != Error.Ok) return;
        Language = (string)cfg.GetValue("ui", "language", Language) is "ar" ? "ar" : "en";
        Theme = (string)cfg.GetValue("ui", "theme", Theme) is "light" ? "light" : "dark";
        AdvancedMode = (bool)cfg.GetValue("ui", "advanced", AdvancedMode);
        AutosaveMinutes = Math.Clamp((int)cfg.GetValue("files", "autosave_minutes", AutosaveMinutes), 0, 60);
        RecentFiles.Clear();
        RecentFiles.AddRange(((string[])cfg.GetValue("files", "recent", Array.Empty<string>())).Take(MaxRecent));
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
        cfg.SetValue("files", "autosave_minutes", AutosaveMinutes);
        cfg.SetValue("files", "recent", RecentFiles.ToArray());
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
