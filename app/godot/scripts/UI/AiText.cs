using System.Globalization;
using System.Text.RegularExpressions;
using Blazma.AI;
using Blazma.Commands;
using Blazma.Core.Text;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 7 text helpers: the core speaks short English; the common messages get a translatable version here and anything else is
/// shown as is. Numbers, paths and bone names are isolated left-to-right so they read correctly inside Arabic sentences.
/// </summary>
public static partial class AiText
{
    static string T(string s) => TranslationServer.Translate(s);

    public static string Ltr(string s) => "\u2066" + s + "\u2069";

    /// <summary>First-strong isolate (FSI … PDI): text whose direction is not known (the user's prompt, a message the app could not
    /// translate) keeps its own direction and does not reorder the Arabic sentence around it.</summary>
    public static string Isolate(string s) => "\u2068" + s + "\u2069";

    public const string KimodoId = KimodoProvider.ProviderId;
    public const string ProceduralId = ProceduralMotionProvider.ProviderId;

    /// <summary>The Kimodo device modes in the order the device lists show them (<see cref="KimodoSettings.DeviceModes"/>).</summary>
    public static readonly string[] DeviceModes = { "auto", "gpu", "cpu", "hybrid" };

    /// <summary>Fills a device list with the four modes (the item text is translated by the control).</summary>
    public static void AddDeviceItems(OptionButton box)
    {
        box.AddItem("Automatic (GPU first)", 0);
        box.AddItem("Graphics card only", 1);
        box.AddItem("Processor only", 2);
        box.AddItem("Graphics card + processor", 3);
        box.TooltipText = "Automatic tries the graphics card first (CUDA, then Vulkan), then the graphics card with the text encoder on the processor, then the processor alone. \"Graphics card + processor\" helps when the card has too little memory.";
    }

    public static int DeviceIndex(string mode) => Math.Max(0, Array.IndexOf(DeviceModes, KimodoSettings.NormalizeDevice(mode)));

    /// <summary>"Generated on: …" from the device a generation ran on (mode gpu | hybrid | cpu and the motion model's device).</summary>
    public static string? GeneratedOn(string? mode, string? motionDevice) => mode switch
    {
        null or "" => null,
        "gpu" when motionDevice is { Length: > 0 } => string.Format(CultureInfo.InvariantCulture, T("Generated on: {0}"), Ltr(motionDevice)),
        "hybrid" when motionDevice is { Length: > 0 } => string.Format(CultureInfo.InvariantCulture, T("Generated on: {0} + the processor (text encoder)"), Ltr(motionDevice)),
        "cpu" => T("Generated on: the processor (CPU)"),
        _ => motionDevice is { Length: > 0 } ? string.Format(CultureInfo.InvariantCulture, T("Generated on: {0}"), Ltr(motionDevice)) : null,
    };

    /// <summary>Shown under "Generated on" when an NVIDIA card ran through Vulkan because the CUDA backend is not installed.</summary>
    public const string CudaHint = "Tip: this NVIDIA card is usually faster with GPU acceleration (CUDA). Download it in Settings → AI.";

    /// <summary>One line per device kimodo-runner listed: "NVIDIA GeForce RTX 4070 (CUDA, 12 GB)".</summary>
    public static string Devices(IReadOnlyList<KimodoDevice> devices) =>
        string.Join("\n", devices.Select(d => "• " + Ltr(d.MemoryTotalBytes > 0 && d.IsGpu ? $"{d.Label}, {Memory(d.MemoryTotalBytes)}" : d.Label)));

    public static string Keywords => string.Join(", ", ProceduralMotionProvider.SupportedKeywords);

    /// <summary>Why Kimodo cannot be used, in the user's language.</summary>
    public static string Reason(string reason)
    {
        if (reason.StartsWith("kimodo-runner is not configured", StringComparison.Ordinal))
            return T("The Kimodo runner is not set up. Install it yourself, then choose its file in the AI settings.");
        if (reason.StartsWith("kimodo-runner was not found", StringComparison.Ordinal))
            return T("The Kimodo runner file was not found at the chosen path.");
        if (reason.StartsWith("The Kimodo model folder is not configured", StringComparison.Ordinal))
            return T("The Kimodo model folder is not set. Download the model files yourself, then choose their folder in the AI settings.");
        if (reason.Contains("was not found in", StringComparison.Ordinal))
            return T("The Kimodo motion model file was not found in the models folder.");
        if (reason.StartsWith("No Kimodo text encoder", StringComparison.Ordinal))
            return T("The Kimodo text encoder file was not found in the models folder.");
        if (reason.StartsWith("'tokenizer.gguf'", StringComparison.Ordinal))
            return T("tokenizer.gguf must be next to the text encoder file.");
        return reason;
    }

    public static string Stage(string stage) => stage switch
    {
        "queued" => T("Waiting to start"),
        "start" => T("Starting the runner"),
        "generate" or "generating" or "sampling" => T("Generating the motion"),
        "load" or "loading" or "load_model" => T("Loading the models"),
        "decode" => T("Decoding the motion"),
        "encode" or "text" => T("Reading the prompt"),
        "retarget" => T("Fitting the motion to the character"),
        "assemble" => T("Joining the steps"),
        "done" => T("Finishing"),
        "ready" => T("Ready"),
        "applied" => T("Added to the project"),
        "cancelled" => T("Cancelled"),
        "failed" => T("Failed"),
        _ => stage,
    };

    public static string State(MotionJobState s) => s switch
    {
        MotionJobState.Running => T("Running"),
        MotionJobState.Ready => T("Ready"),
        MotionJobState.Applied => T("Added"),
        MotionJobState.Cancelled => T("Cancelled"),
        _ => T("Failed"),
    };

    [GeneratedRegex(@"^Step (\d+) \(""(.*?)""\): (.*)$", RegexOptions.Singleline)]
    private static partial Regex StepRx();

    /// <summary>A job failure in the user's language. "Step 2 ("يتشقلب"): …" (a planned generation) becomes «فشل التوليد في الخطوة 2
    /// («يتشقلب»): …»; known codes and messages are translated, anything else is shown isolated so it cannot garble the line.</summary>
    public static string Failure(string? code, string? message)
    {
        var msg = message ?? "";
        if (StepRx().Match(msg) is { Success: true } m)
            return string.Format(CultureInfo.InvariantCulture, T("Generation failed at step {0} (“{1}”): {2}"),
                Ltr(m.Groups[1].Value), Isolate(m.Groups[2].Value), Error(code, m.Groups[3].Value) ?? Isolate(m.Groups[3].Value));
        return Error(code, msg) ?? string.Format(CultureInfo.InvariantCulture, T("Generation failed: {0}"), Isolate(msg.Length > 0 ? msg : code ?? ""));
    }

    static bool OutOfMemory(string m) =>
        m.Contains("out of memory", StringComparison.OrdinalIgnoreCase) || m.Contains("out of device memory", StringComparison.OrdinalIgnoreCase)
        || m.Contains("cannot allocate", StringComparison.OrdinalIgnoreCase) || m.Contains("allocation failed", StringComparison.OrdinalIgnoreCase)
        || m.Contains("ErrorOutOfDeviceMemory", StringComparison.Ordinal) || m.Contains("bad_alloc", StringComparison.Ordinal);

    static string F(string template, params object[] args) => string.Format(CultureInfo.InvariantCulture, T(template), args);

    /// <summary>A provider error (code + English message) as one translated sentence; null when it is not one of the known ones.</summary>
    public static string? Error(string? code, string message)
    {
        switch (code)
        {
            case "unsupported_prompt":
                return F("The built-in provider only understands these words: {0}. Try one of them.", Keywords);
            case "runner_timeout":
                return T("The Kimodo runner took too long and was stopped. Raise the timeout in the AI settings, or try a shorter motion.");
            case "runner_crashed":
                if (message.Contains("0xC0000135", StringComparison.Ordinal))
                    return T("The Kimodo runner could not start: a library it needs is missing (usually the Microsoft Visual C++ runtime, which Blazma ships next to it). Reinstall Blazma Animation.");
                if (message.Contains("0xC0000139", StringComparison.Ordinal))
                    return T("The Kimodo runner could not start: a library it uses is too old (usually the Microsoft Visual C++ runtime). Reinstall Blazma Animation.");
                if (OutOfMemory(message))
                    return T("The Kimodo runner ran out of memory and stopped. Choose “Graphics card + processor” or “Processor only” in the AI settings, a shorter motion, or a lighter model package.");
                return T("The Kimodo runner stopped unexpectedly. Open the runner log in the AI settings for details.");
            case "runner_start_failed":
                return F("The Kimodo runner could not be started: {0}", Isolate(message));
            case "runner_stopped":
                return T("The Kimodo runner was stopped (the settings changed or the app is closing). Try again.");
            case "protocol_mismatch" or "protocol_error" or "message_too_large":
                return F("This kimodo-runner does not match this version of Blazma. Reinstall Blazma Animation. Details: {0}", Isolate(message));
            case "provider_unavailable":
                var reason = Reason(message);
                return ReferenceEquals(reason, message) ? Isolate(message) : reason;
            case "models_not_found":
                var at = message.IndexOf(" not found: ", StringComparison.Ordinal);
                return F("A Kimodo model file was not found: {0}. Download the models again in Settings → AI.", Ltr(at >= 0 ? message[(at + 12)..] : message));
            case "model_load_failed":
                return OutOfMemory(message)
                    ? T("There is not enough memory to load the Kimodo models. Close other programs, choose a lighter model package, or choose “Graphics card + processor” or “Processor only” in the AI settings.")
                    : F("The Kimodo models could not be loaded: {0}", Isolate(message));
            case "generation_failed":
                if (OutOfMemory(message))
                    return T("The graphics card or the computer ran out of memory while generating. Try “Graphics card + processor” or “Processor only” in the AI settings, a shorter motion, or a lighter model package.");
                if (message.Contains("CUDA", StringComparison.Ordinal))
                    return F("The NVIDIA driver reported an error (CUDA). Update the NVIDIA driver, or remove GPU acceleration (CUDA) in Settings → AI to use Vulkan. Details: {0}", Isolate(message));
                return F("Kimodo could not generate this motion: {0}", Isolate(message));
            case "device_unavailable":
                string? head = null;
                if (message.StartsWith("No graphics card usable by Kimodo was found", StringComparison.Ordinal))
                    head = T("No graphics card usable by Kimodo was found. Update the graphics driver, or choose “Processor only” in the AI settings.");
                else if (message.StartsWith("The graphics card '", StringComparison.Ordinal))
                    head = T("The chosen graphics card is not available any more. Choose another one, or “Automatic”, in the AI settings.");
                else if (message.StartsWith("This kimodo-runner is too old", StringComparison.Ordinal))
                    head = T("This kimodo-runner is too old for “Graphics card + processor”. Reinstall Blazma Animation, or choose another device.");
                else if (message.StartsWith("no GGML CPU backend", StringComparison.Ordinal))
                    head = T("The processor part of Kimodo (ggml-cpu) could not be loaded. Reinstall Blazma Animation.");
                if (head is null) return null;
                var problems = KimodoGpuDiagnosis.ParseWarning(message);
                return problems.Count == 0 ? head : head + "\n" + GpuProblems(problems);
            case "invalid_motion":
                return F("Kimodo returned a motion Blazma cannot use: {0}", Isolate(message));
            default:
                return null;
        }
    }

    /// <summary>Why no graphics card was found, one translated line per reason (KimodoGpuDiagnosis).</summary>
    public static string GpuProblems(IEnumerable<GpuProblem> problems) => string.Join("\n", problems.Select(p => "• " + GpuProblem(p)));

    public static string GpuProblem(GpuProblem p) => p.Code switch
    {
        "backend_missing" => F("{0} is missing next to kimodo-runner. Reinstall Blazma Animation.", Ltr(p.File ?? "")),
        "missing_dependency" => F("{0} could not load because {1} is missing (Windows error 126). Reinstall Blazma Animation.", Ltr(p.File ?? ""), Ltr(p.Dependency ?? "")),
        "missing_function" => F("{0} could not load because {1} in {2} is too old (Windows error 127). Reinstall Blazma Animation.", Ltr(p.File ?? ""), Ltr(p.Dependency ?? ""), Ltr(p.Location ?? "")),
        "no_vulkan_loader" => T("The graphics driver has no Vulkan support installed (vulkan-1.dll is missing). Install the latest driver from your graphics card maker (NVIDIA, AMD or Intel)."),
        "no_nvidia_driver" => T("The NVIDIA driver is not installed (nvcuda.dll is missing), so GPU acceleration (CUDA) cannot run. Install the latest NVIDIA driver."),
        "no_vulkan_device" => T("No graphics driver offers Vulkan: the driver is too old or a generic Windows driver, or this is a remote desktop session. Install the latest driver from your graphics card maker."),
        "no_cuda_device" => T("GPU acceleration (CUDA) is installed, but the NVIDIA driver offers no CUDA device. Update the NVIDIA driver."),
        _ => Isolate(p.Message),
    };

    [GeneratedRegex(@"^(\d+) bone\(s\) are not driven by the generated motion and stay at rest: (.*)$")]
    private static partial Regex UnmappedRx();
    [GeneratedRegex(@"^The rig has no bone mapped to '(.*)'; that part of the motion is lost\.$")]
    private static partial Regex NoBoneRx();
    [GeneratedRegex(@"^Foot cleanup skipped for the (left|right) leg")]
    private static partial Regex FootRx();

    /// <summary>One warning from a generation job in the user's language.</summary>
    public static string Warning(string w)
    {
        if (w.StartsWith("Kimodo generates the 30 SOMA body joints only", StringComparison.Ordinal))
            return T("Only the body was generated: fingers and face stay at their rest pose.");
        if (w.StartsWith("Constraints were ignored", StringComparison.Ordinal))
            return T("Constraints were ignored: Kimodo does not support them yet.");
        if (w.StartsWith("Loop: this motion starts and ends", StringComparison.Ordinal))
            return T("Loop: this motion starts and ends in the same standing pose.");
        if (w.StartsWith("Generated on ", StringComparison.Ordinal) && w.Contains("failed attempt", StringComparison.Ordinal))
            return string.Format(CultureInfo.InvariantCulture, T("The graphics card path failed, so Kimodo fell back to another device. Details: {0}"), Ltr(w));
        if (w.StartsWith(KimodoGpuDiagnosis.NoGpuWarning, StringComparison.Ordinal) || w.StartsWith(KimodoProvider.NoGpuCheck, StringComparison.Ordinal))
        {
            var head = w.StartsWith(KimodoProvider.NoGpuCheck, StringComparison.Ordinal)
                ? T("kimodo-runner found no usable graphics card: Kimodo runs on the processor (slower).")
                : T("No usable graphics card was found, so the motion was generated on the processor.");
            var why = KimodoGpuDiagnosis.ParseWarning(w);
            return why.Count == 0 ? head : head + "\n" + GpuProblems(why);
        }
        if (w.StartsWith("Procedural motion (not AI)", StringComparison.Ordinal))
            return T("Procedural motion (not AI): a simple keyword cycle for testing and demos.");
        if (UnmappedRx().Match(w) is { Success: true } m)
            return string.Format(CultureInfo.InvariantCulture, T("{0} bones of this character are not driven by the motion and stay at rest: {1}"), Ltr(m.Groups[1].Value), Ltr(m.Groups[2].Value.TrimEnd('.')));
        if (NoBoneRx().Match(w) is { Success: true } n)
            return string.Format(CultureInfo.InvariantCulture, T("This character has no bone mapped to “{0}”, so that part of the motion is lost."), Ltr(n.Groups[1].Value));
        if (FootRx().Match(w) is { Success: true } f)
            return string.Format(CultureInfo.InvariantCulture, T("Foot cleanup was skipped for the {0} leg: its bones are not a direct parent and child chain."), T(f.Groups[1].Value == "left" ? "left" : "right"));
        return w;
    }

    /// <summary>Elapsed time as m:ss.</summary>
    public static string Elapsed(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    /// <summary>A file or download size in decimal units (1 GB = 10^9 bytes, as Hugging Face and GitHub state them), translated:
    /// "6.2 GB" / "6.2 جيجابايت". The one formatter for model packages, download progress, the CUDA package, free space and exports.</summary>
    public static string Size(long? b)
    {
        if (b is not { } v) return "?";
        var (n, unit) = ByteSize.Split(v);
        var template = unit switch { "GB" => T("{0} GB"), "MB" => T("{0} MB"), "KB" => T("{0} KB"), _ => T("{0} bytes") };
        return string.Format(CultureInfo.InvariantCulture, template, n);
    }

    /// <summary>RAM or video memory: GB of 2^30 bytes, as Windows and the card makers label it ("16 GB RAM").</summary>
    public static string Memory(long? b) => b is null ? "?" : string.Format(CultureInfo.InvariantCulture, T("{0} GB"), ByteSize.MemoryGb(b.Value));

    /// <summary>A memory requirement from the model catalogue, e.g. 16 → "16 GB".</summary>
    public static string MemoryGiB(double gib) => string.Format(CultureInfo.InvariantCulture, T("{0} GB"), gib.ToString("0.#", CultureInfo.InvariantCulture));
}
