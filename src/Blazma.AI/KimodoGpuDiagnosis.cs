using System.Text;
using System.Text.RegularExpressions;

namespace Blazma.AI;

/// <summary>
/// One reason why kimodo-runner offered no graphics card. GGML loads its backends (ggml-vulkan, ggml-cuda) at run time and silently
/// skips one that does not load, so the runner itself cannot say why; <see cref="KimodoGpuDiagnosis"/> finds it from the files.
/// <see cref="Code"/>: backend_missing | missing_dependency (Windows error 126) | missing_function (Windows error 127) |
/// no_vulkan_loader | no_nvidia_driver | no_vulkan_device | no_cuda_device.
/// </summary>
public sealed record GpuProblem(string Backend, string Code, string? File = null, string? Dependency = null, string? Location = null, string? Function = null)
{
    /// <summary>The Windows error LoadLibrary gives for this problem (126 = a dependency is missing, 127 = a function is missing).</summary>
    public int? WindowsError => Code switch { "missing_dependency" or "no_vulkan_loader" or "no_nvidia_driver" => 126, "missing_function" => 127, _ => null };

    /// <summary>English, one sentence; <see cref="TryParse"/> reads it back (the UI translates from the fields).</summary>
    public string Message => Code switch
    {
        "backend_missing" => $"{File} is not next to kimodo-runner.",
        "missing_dependency" => $"{File} could not load: it needs {Dependency}, which is not on this PC (Windows error 126).",
        "missing_function" => $"{File} could not load: {Dependency} in {Location} is too old (it lacks {Function}; Windows error 127).",
        "no_vulkan_loader" => $"{File} could not load: vulkan-1.dll, the Vulkan loader that comes with the graphics driver, is missing (Windows error 126).",
        "no_nvidia_driver" => $"{File} could not load: nvcuda.dll, part of the NVIDIA driver, is missing (Windows error 126).",
        "no_vulkan_device" => "The Vulkan loader is installed, but no graphics driver offers a Vulkan device (the driver is too old or a generic one, or this is a remote desktop session).",
        "no_cuda_device" => "The CUDA backend is installed, but the NVIDIA driver offers no CUDA device (the driver is too old, or there is no NVIDIA card).",
        _ => Code,
    };

    static readonly (string Code, Regex Rx)[] Patterns =
    {
        ("backend_missing", new Regex(@"^(?<f>\S+) is not next to kimodo-runner\.$")),
        ("missing_dependency", new Regex(@"^(?<f>\S+) could not load: it needs (?<d>\S+), which is not on this PC \(Windows error 126\)\.$")),
        ("missing_function", new Regex(@"^(?<f>\S+) could not load: (?<d>\S+) in (?<l>.+) is too old \(it lacks (?<fn>.+); Windows error 127\)\.$")),
        ("no_vulkan_loader", new Regex(@"^(?<f>\S+) could not load: vulkan-1\.dll, the Vulkan loader")),
        ("no_nvidia_driver", new Regex(@"^(?<f>\S+) could not load: nvcuda\.dll, part of the NVIDIA driver")),
        ("no_vulkan_device", new Regex(@"^The Vulkan loader is installed, but no graphics driver offers a Vulkan device")),
        ("no_cuda_device", new Regex(@"^The CUDA backend is installed, but the NVIDIA driver offers no CUDA device")),
    };

    /// <summary>A problem from its <see cref="Message"/> (the backend is not part of the message and comes back empty).</summary>
    public static GpuProblem? TryParse(string message)
    {
        foreach (var (code, rx) in Patterns)
            if (rx.Match(message.Trim()) is { Success: true } m)
                return new GpuProblem("", code, G(m, "f"), G(m, "d"), G(m, "l"), G(m, "fn"));
        return null;
        static string? G(Match m, string name) => m.Groups[name] is { Success: true } g ? g.Value : null;
    }
}

/// <summary>
/// Why kimodo-runner found no graphics card: reads the import tables of the GGML backend libraries (no library is loaded into Blazma)
/// and checks that each dependency exists where Windows looks for it (the runner's folder, the CUDA folder, the system folder) and,
/// for the Visual C++ runtime, that it has the functions the backend needs. Then it tells "the files are fine, the driver offers no
/// device" apart from "a file is missing". Deterministic and offline; on Linux it only checks the files are there.
/// </summary>
public static class KimodoGpuDiagnosis
{
    /// <summary>The start of the warning a generation carries when it ran on the processor because there was no graphics card.</summary>
    public const string NoGpuWarning = "No usable graphics card was found, so the motion was generated on the processor.";
    const string Why = " Why: ";

    /// <summary>The warning text: <see cref="NoGpuWarning"/> plus each problem's message (<see cref="ParseWarning"/> reads it back).</summary>
    public static string Warning(IReadOnlyList<GpuProblem> problems) =>
        problems.Count == 0 ? NoGpuWarning : NoGpuWarning + Why + string.Join(" | ", problems.Select(p => p.Message));

    /// <summary>The problems named in a text built by <see cref="Warning"/> or <see cref="Describe"/> (empty when it names none).</summary>
    public static IReadOnlyList<GpuProblem> ParseWarning(string text)
    {
        var i = text.IndexOf(Why, StringComparison.Ordinal);
        if (i < 0) return Array.Empty<GpuProblem>();
        return text[(i + Why.Length)..].Split(" | ").Select(GpuProblem.TryParse).Where(p => p is not null).Select(p => p!).ToList();
    }

    /// <summary>" Why: …" for an error message (empty without problems).</summary>
    public static string Describe(IReadOnlyList<GpuProblem> problems) =>
        problems.Count == 0 ? "" : Why + string.Join(" | ", problems.Select(p => p.Message));

    static readonly string[] CrtPrefixes = { "msvcp140", "vcruntime140", "vcomp140", "concrt140" };

    /// <summary>
    /// The reasons for a runner that listed no graphics card.
    /// </summary>
    /// <param name="runnerPath">kimodo-runner(.exe); its folder holds the GGML backends.</param>
    /// <param name="cudaLibrary">The optional CUDA backend the user downloaded (ggml-cuda.dll), or null.</param>
    /// <param name="backends">The backends the runner registered (hello's "backends"), or null when unknown.</param>
    /// <param name="systemDirectory">Windows' System32 folder (null on Linux or in tests that leave it out).</param>
    /// <param name="windows">True for a Windows runner (PE files).</param>
    public static IReadOnlyList<GpuProblem> Diagnose(string runnerPath, string? cudaLibrary, IReadOnlyList<string>? backends, string? systemDirectory, bool windows)
    {
        var dir = Path.GetDirectoryName(runnerPath) ?? ".";
        var list = new List<GpuProblem>();
        bool Registered(string name) => backends?.Any(b => b.Equals(name, StringComparison.OrdinalIgnoreCase)) == true;
        if (!windows)
        {
            var so = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "libggml-vulkan.so*").FirstOrDefault() : null;
            list.Add(so is null ? new GpuProblem("Vulkan", "backend_missing", "libggml-vulkan.so") : new GpuProblem("Vulkan", "no_vulkan_device"));
            return list;
        }

        var searchDirs = new List<string> { dir };
        if (cudaLibrary is not null && Path.GetDirectoryName(cudaLibrary) is { } cd) searchDirs.Add(cd);
        if (systemDirectory is not null) searchDirs.Add(systemDirectory);

        var vulkan = Path.Combine(dir, "ggml-vulkan.dll");
        if (!File.Exists(vulkan)) list.Add(new GpuProblem("Vulkan", "backend_missing", "ggml-vulkan.dll"));
        else
        {
            var found = Registered("Vulkan") ? new List<GpuProblem>() : CheckLibrary("Vulkan", vulkan, searchDirs, systemDirectory);
            list.AddRange(found.Count > 0 ? found : new[] { new GpuProblem("Vulkan", "no_vulkan_device") });
        }
        if (cudaLibrary is not null && File.Exists(cudaLibrary))
        {
            var found = Registered("CUDA") ? new List<GpuProblem>() : CheckLibrary("CUDA", cudaLibrary, searchDirs, systemDirectory);
            list.AddRange(found.Count > 0 ? found : new[] { new GpuProblem("CUDA", "no_cuda_device") });
        }
        return list;
    }

    /// <summary>Missing dependencies of <paramref name="library"/>, two levels deep through the libraries shipped with the runner.</summary>
    static List<GpuProblem> CheckLibrary(string backend, string library, IReadOnlyList<string> searchDirs, string? systemDirectory)
    {
        var problems = new List<GpuProblem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Check(string file, int depth)
        {
            if (!seen.Add(file) || depth > 2) return;
            IReadOnlyList<PeImport> imports;
            try { imports = PeFile.Imports(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return; }
            var name = Path.GetFileName(file);
            foreach (var imp in imports)
            {
                var dll = imp.Library;
                if (dll.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase) || dll.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase)) continue;
                var at = searchDirs.Select(d => Path.Combine(d, dll)).FirstOrDefault(File.Exists);
                if (at is null)
                {
                    // Without a system folder (tests, or one that cannot be read) only the runner's own libraries can be judged.
                    if (systemDirectory is null && !IsShippedName(dll)) continue;
                    problems.Add(dll.Equals("vulkan-1.dll", StringComparison.OrdinalIgnoreCase) ? new GpuProblem(backend, "no_vulkan_loader", name, dll)
                        : dll.Equals("nvcuda.dll", StringComparison.OrdinalIgnoreCase) ? new GpuProblem(backend, "no_nvidia_driver", name, dll)
                        : new GpuProblem(backend, "missing_dependency", name, dll));
                    continue;
                }
                if (CrtPrefixes.Any(p => dll.StartsWith(p, StringComparison.OrdinalIgnoreCase)) && imp.Functions.Count > 0)
                {
                    HashSet<string> exports;
                    try { exports = PeFile.Exports(at); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { continue; }
                    if (imp.Functions.FirstOrDefault(f => !exports.Contains(f)) is { } lacking)
                    {
                        // The system folder by its path; Blazma's own folders by their name only (no user names in messages for agents).
                        var folder = Path.GetDirectoryName(at);
                        var shown = string.Equals(folder, systemDirectory, StringComparison.OrdinalIgnoreCase) ? folder : Path.GetFileName(folder);
                        problems.Add(new GpuProblem(backend, "missing_function", name, dll, shown, lacking));
                    }
                }
                else if (Path.GetDirectoryName(at) is { } where && !string.Equals(where, systemDirectory, StringComparison.OrdinalIgnoreCase))
                    Check(at, depth + 1);
            }
        }
        Check(library, 1);
        return problems.DistinctBy(p => (p.Code, p.File, p.Dependency)).ToList();
    }

    /// <summary>Libraries Blazma ships next to the runner (GGML, kimodo, the Visual C++ runtime, CUDA's runtime).</summary>
    static bool IsShippedName(string dll) =>
        dll.StartsWith("ggml", StringComparison.OrdinalIgnoreCase) || dll.StartsWith("kimodo", StringComparison.OrdinalIgnoreCase)
        || dll.StartsWith("cudart", StringComparison.OrdinalIgnoreCase) || dll.StartsWith("cublas", StringComparison.OrdinalIgnoreCase)
        || CrtPrefixes.Any(p => dll.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One imported library of a PE file and the functions imported from it by name (ordinal imports are left out).</summary>
public sealed record PeImport(string Library, IReadOnlyList<string> Functions);

/// <summary>A minimal reader of Windows PE files (PE32 and PE32+): the import table and the exported names. Reads the file only;
/// nothing is loaded or run, so it is safe on any platform. Malformed files throw <see cref="InvalidDataException"/>.</summary>
public static class PeFile
{
    sealed class Image(FileStream fs)
    {
        public readonly List<(uint Va, uint VSize, uint Raw, uint RawSize)> Sections = new();
        public bool Pe32Plus;
        public uint ExportRva, ImportRva;

        public byte[] Read(long offset, int count)
        {
            if (offset < 0 || offset + count > fs.Length) throw new InvalidDataException("PE file truncated");
            var b = new byte[count];
            fs.Position = offset;
            fs.ReadExactly(b);
            return b;
        }

        public long Offset(uint rva)
        {
            foreach (var s in Sections)
                if (rva >= s.Va && rva < s.Va + Math.Max(s.VSize, s.RawSize)) return s.Raw + (long)(rva - s.Va);
            throw new InvalidDataException($"RVA 0x{rva:x} is outside every section");
        }

        public uint U32(uint rva) => BitConverter.ToUInt32(Read(Offset(rva), 4));
        public ulong U64(uint rva) => BitConverter.ToUInt64(Read(Offset(rva), 8));

        public string Str(uint rva)
        {
            var off = Offset(rva);
            var sb = new StringBuilder();
            for (var i = 0; i < 512; i++)
            {
                var c = Read(off + i, 1)[0];
                if (c == 0) return sb.ToString();
                sb.Append((char)c);
            }
            throw new InvalidDataException("PE string too long");
        }
    }

    static T With<T>(string path, Func<Image, T> body)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var img = new Image(fs);
        var dos = img.Read(0, 64);
        if (dos[0] != 'M' || dos[1] != 'Z') throw new InvalidDataException("not a PE file (no MZ header)");
        var pe = BitConverter.ToUInt32(dos, 0x3C);
        var coff = img.Read(pe, 24);
        if (coff[0] != 'P' || coff[1] != 'E' || coff[2] != 0 || coff[3] != 0) throw new InvalidDataException("not a PE file (no PE signature)");
        int sections = BitConverter.ToUInt16(coff, 6);
        int optSize = BitConverter.ToUInt16(coff, 20);
        var opt = img.Read(pe + 24, optSize);
        var magic = BitConverter.ToUInt16(opt, 0);
        img.Pe32Plus = magic == 0x20b;
        if (magic is not (0x10b or 0x20b)) throw new InvalidDataException("unknown PE optional header");
        var dd = img.Pe32Plus ? 112 : 96;
        var count = BitConverter.ToUInt32(opt, dd - 4);
        if (count > 0 && dd + 8 <= optSize) img.ExportRva = BitConverter.ToUInt32(opt, dd);
        if (count > 1 && dd + 16 <= optSize) img.ImportRva = BitConverter.ToUInt32(opt, dd + 8);
        var table = img.Read(pe + 24 + optSize, 40 * Math.Min(sections, 96));
        for (var i = 0; i < Math.Min(sections, 96); i++)
            img.Sections.Add((BitConverter.ToUInt32(table, i * 40 + 12), BitConverter.ToUInt32(table, i * 40 + 8),
                BitConverter.ToUInt32(table, i * 40 + 20), BitConverter.ToUInt32(table, i * 40 + 16)));
        return body(img);
    }

    /// <summary>The libraries a PE file imports at load time (delay-loaded ones are not included) and the functions it takes by name.</summary>
    public static IReadOnlyList<PeImport> Imports(string path) => With(path, img =>
    {
        var list = new List<PeImport>();
        if (img.ImportRva == 0) return list;
        for (uint d = img.ImportRva; list.Count < 1024; d += 20)
        {
            var oft = img.U32(d);
            var nameRva = img.U32(d + 12);
            var ft = img.U32(d + 16);
            if (nameRva == 0 && oft == 0 && ft == 0) break;
            var functions = new List<string>();
            var thunk = oft != 0 ? oft : ft;
            for (var n = 0; n < 65536; n++)
            {
                ulong v = img.Pe32Plus ? img.U64(thunk) : img.U32(thunk);
                if (v == 0) break;
                var byOrdinal = img.Pe32Plus ? (v & 0x8000000000000000UL) != 0 : (v & 0x80000000UL) != 0;
                if (!byOrdinal) functions.Add(img.Str((uint)(v & 0x7fffffff) + 2));
                thunk += img.Pe32Plus ? 8u : 4u;
            }
            list.Add(new PeImport(img.Str(nameRva), functions));
        }
        return list;
    });

    /// <summary>The names a PE file exports.</summary>
    public static HashSet<string> Exports(string path) => With(path, img =>
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (img.ExportRva == 0) return set;
        var count = img.U32(img.ExportRva + 24);
        var names = img.U32(img.ExportRva + 32);
        for (uint i = 0; i < Math.Min(count, 200_000u); i++) set.Add(img.Str(img.U32(names + i * 4)));
        return set;
    });
}
