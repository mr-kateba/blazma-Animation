using Godot;

namespace Blazma.UI.App;

/// <summary>Per-user preferences stored in user://settings.cfg (never inside projects).</summary>
public sealed class AppSettings
{
    static string Path => AppPaths.Combine("settings.cfg");
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
    /// <summary>Phase 6: balance overlays in the viewport (COM, support polygon, contacts) and the timeline balance strip. Off by default.</summary>
    public bool ShowPhysics { get; set; }
    /// <summary>Phase 6: COM / hips / feet paths over the clip.</summary>
    public bool ShowPhysicsPaths { get; set; }
    /// <summary>Phase 6: the Physics panel is open.</summary>
    public bool PhysicsPanel { get; set; }
    /// <summary>Phase 8: foot contact spans as a thin strip under the timeline.</summary>
    public bool ShowContacts { get; set; }
    /// <summary>Phase 8: the Props panel is open.</summary>
    public bool PropsPanel { get; set; }
    /// <summary>Phase 9: hurtboxes and hitboxes drawn in the viewport, and the combat strip under the timeline. Off by default.</summary>
    public bool ShowCombat { get; set; }
    /// <summary>Phase 9: the Combat panel is open.</summary>
    public bool CombatPanel { get; set; }
    /// <summary>Viewport camera: fly speed in m/s (0.5-40), inverted vertical look, orbit around the selected bone, vertical field of view in degrees (30-100).</summary>
    public float CameraFlySpeed { get; set; } = 4f;
    public bool CameraInvertY { get; set; }
    public bool CameraOrbitSelection { get; set; }
    public float CameraFov { get; set; } = 45f;
    public List<string> RecentFiles { get; } = new();
    /// <summary>Folder of the last animation export (Phase 4).</summary>
    public string LastExportDir { get; set; } = "";

    /// <summary>Phase 5: AI agents (Claude Code etc.) may connect to this window over the local pipe.</summary>
    public bool AllowAgents { get; set; } = true;
    /// <summary>False = Assisted (every agent task waits for review), true = Autonomous.</summary>
    public bool AgentAutonomous { get; set; }
    /// <summary>Folders agents may import files from besides the project's own folder.</summary>
    public List<string> AgentReadRoots { get; } = new();

    /// <summary>Phase 7: where the user installed kimodo-runner and the model files (empty = not set; the BLAZMA_KIMODO_* environment variables still apply). Nothing is downloaded by the app.</summary>
    public string KimodoRunner { get; set; } = "";
    public string KimodoModels { get; set; } = "";
    /// <summary>auto | cpu | vulkan</summary>
    public string KimodoDevice { get; set; } = "auto";
    /// <summary>Text encoder file chosen by the model installer (empty = pick the best one found in the folder).</summary>
    public string KimodoTextModel { get; set; } = "";
    /// <summary>Longest a single Kimodo generation may take (0 = the default of 15 minutes).</summary>
    public int KimodoTimeoutSeconds { get; set; }

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
        ShowPhysics = (bool)cfg.GetValue("ui", "show_physics", ShowPhysics);
        ShowPhysicsPaths = (bool)cfg.GetValue("ui", "show_physics_paths", ShowPhysicsPaths);
        PhysicsPanel = (bool)cfg.GetValue("ui", "physics_panel", PhysicsPanel);
        ShowContacts = (bool)cfg.GetValue("ui", "show_contacts", ShowContacts);
        PropsPanel = (bool)cfg.GetValue("ui", "props_panel", PropsPanel);
        ShowCombat = (bool)cfg.GetValue("ui", "show_combat", ShowCombat);
        CombatPanel = (bool)cfg.GetValue("ui", "combat_panel", CombatPanel);
        CameraFlySpeed = Math.Clamp((float)(double)cfg.GetValue("camera", "fly_speed", (double)CameraFlySpeed), 0.5f, 40f);
        CameraInvertY = (bool)cfg.GetValue("camera", "invert_y", CameraInvertY);
        CameraOrbitSelection = (bool)cfg.GetValue("camera", "orbit_selection", CameraOrbitSelection);
        CameraFov = Math.Clamp((float)(double)cfg.GetValue("camera", "fov", (double)CameraFov), 30f, 100f);
        AutosaveMinutes = Math.Clamp((int)cfg.GetValue("files", "autosave_minutes", AutosaveMinutes), 0, 60);
        RecentFiles.Clear();
        RecentFiles.AddRange(((string[])cfg.GetValue("files", "recent", Array.Empty<string>())).Take(MaxRecent));
        LastExportDir = (string)cfg.GetValue("files", "last_export_dir", LastExportDir);
        AllowAgents = (bool)cfg.GetValue("agent", "allow", AllowAgents);
        AgentAutonomous = (bool)cfg.GetValue("agent", "autonomous", AgentAutonomous);
        AgentReadRoots.Clear();
        AgentReadRoots.AddRange((string[])cfg.GetValue("agent", "read_roots", Array.Empty<string>()));
        KimodoRunner = (string)cfg.GetValue("ai", "kimodo_runner", KimodoRunner);
        KimodoModels = (string)cfg.GetValue("ai", "kimodo_models", KimodoModels);
        KimodoTextModel = (string)cfg.GetValue("ai", "kimodo_text_model", KimodoTextModel);
        KimodoDevice = (string)cfg.GetValue("ai", "kimodo_device", KimodoDevice) is var dev && (dev == "cpu" || dev == "vulkan") ? dev : "auto";
        KimodoTimeoutSeconds = Math.Clamp((int)cfg.GetValue("ai", "kimodo_timeout_seconds", KimodoTimeoutSeconds), 0, 24 * 3600);
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
        cfg.SetValue("ui", "show_physics", ShowPhysics);
        cfg.SetValue("ui", "show_physics_paths", ShowPhysicsPaths);
        cfg.SetValue("ui", "physics_panel", PhysicsPanel);
        cfg.SetValue("ui", "show_contacts", ShowContacts);
        cfg.SetValue("ui", "props_panel", PropsPanel);
        cfg.SetValue("ui", "show_combat", ShowCombat);
        cfg.SetValue("ui", "combat_panel", CombatPanel);
        cfg.SetValue("camera", "fly_speed", (double)CameraFlySpeed);
        cfg.SetValue("camera", "invert_y", CameraInvertY);
        cfg.SetValue("camera", "orbit_selection", CameraOrbitSelection);
        cfg.SetValue("camera", "fov", (double)CameraFov);
        cfg.SetValue("files", "autosave_minutes", AutosaveMinutes);
        cfg.SetValue("files", "recent", RecentFiles.ToArray());
        cfg.SetValue("files", "last_export_dir", LastExportDir);
        cfg.SetValue("agent", "allow", AllowAgents);
        cfg.SetValue("agent", "autonomous", AgentAutonomous);
        cfg.SetValue("agent", "read_roots", AgentReadRoots.ToArray());
        cfg.SetValue("ai", "kimodo_runner", KimodoRunner);
        cfg.SetValue("ai", "kimodo_models", KimodoModels);
        cfg.SetValue("ai", "kimodo_device", KimodoDevice);
        cfg.SetValue("ai", "kimodo_text_model", KimodoTextModel);
        cfg.SetValue("ai", "kimodo_timeout_seconds", KimodoTimeoutSeconds);
        foreach (var (k, v) in Layout) cfg.SetValue("layout", k, v);
        System.IO.Directory.CreateDirectory(AppPaths.UserDir);
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
