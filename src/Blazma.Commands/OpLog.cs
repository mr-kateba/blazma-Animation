using System.Text.Json;

namespace Blazma.Commands;

public sealed record OpLogEntry(
    DateTimeOffset Time, string Origin, string Command, string Label, bool Ok, string? Error, string? TransactionId);

public interface IOpLog
{
    void Append(OpLogEntry entry);
}

/// <summary>
/// JSON-lines operation log kept in the session folder, never inside the project archive
/// (it may contain agent prompts and paths). Rotates to "&lt;file&gt;.1" past <paramref name="maxBytes"/>.
/// </summary>
public sealed class JsonlOpLog(string path, long maxBytes = 2 * 1024 * 1024) : IOpLog
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public void Append(OpLogEntry entry)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (File.Exists(Path) && new FileInfo(Path).Length > maxBytes)
                File.Move(Path, Path + ".1", overwrite: true);
            File.AppendAllText(Path, JsonSerializer.Serialize(entry, Options) + "\n");
        }
        catch (IOException)
        {
            // Logging must never break editing.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
