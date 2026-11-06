using System.IO;
using Godot;

namespace Blazma.UI.App;

/// <summary>Where per-user data lives: user:// normally, or a "userdata" folder beside the executable in portable mode (portable.txt next to the exe).</summary>
static class AppPaths
{
    static readonly string ExeDir = Path.GetDirectoryName(OS.GetExecutablePath())!;
    public static readonly bool Portable = !OS.HasFeature("editor") && File.Exists(Path.Combine(ExeDir, "portable.txt"));
    public static string UserDir => Portable ? Path.Combine(ExeDir, "userdata") : ProjectSettings.GlobalizePath("user://");
    public static string Combine(string rel) => Path.Combine(UserDir, rel);
}
