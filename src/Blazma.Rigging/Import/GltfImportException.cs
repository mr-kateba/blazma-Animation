namespace Blazma.Rigging.Import;

/// <summary>
/// Thrown when a glTF file cannot be read or imported. <see cref="Code"/> is a stable machine-readable value:
/// "not_found", "invalid_format", "unsupported_version", "unsupported_extension", "no_skin", "invalid_reference".
/// </summary>
public sealed class GltfImportException : Exception
{
    public string Code { get; }

    public GltfImportException(string code, string message) : base(message) => Code = code;
}
