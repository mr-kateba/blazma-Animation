using System.Globalization;
using System.Text.RegularExpressions;
using Blazma.AI;
using Blazma.Commands;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 7 text helpers: the core speaks short English; the common messages get a translatable version here and anything else is
/// shown as is. Numbers, paths and bone names are isolated left-to-right so they read correctly inside Arabic sentences.
/// </summary>
public static partial class AiText
{
    static string T(string s) => TranslationServer.Translate(s);

    public static string Ltr(string s) => "⁦" + s + "⁩";

    public const string KimodoId = KimodoProvider.ProviderId;
    public const string ProceduralId = ProceduralMotionProvider.ProviderId;

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

    /// <summary>A job failure in the user's language (the provider's own message is appended when it adds something).</summary>
    public static string Failure(string? code, string? message) => code switch
    {
        "unsupported_prompt" => string.Format(CultureInfo.InvariantCulture, T("The built-in provider only understands these words: {0}. Try one of them."), Keywords),
        "runner_timeout" => T("The Kimodo runner took too long and was stopped. Raise the timeout in the AI settings, or try a shorter motion."),
        "runner_crashed" => T("The Kimodo runner stopped unexpectedly. Open the runner log in the AI settings for details."),
        "runner_start_failed" => string.Format(CultureInfo.InvariantCulture, T("The Kimodo runner could not be started: {0}"), message ?? ""),
        "provider_unavailable" => Reason(message ?? ""),
        _ => string.Format(CultureInfo.InvariantCulture, T("Generation failed: {0}"), message ?? code ?? ""),
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

    public static string Bytes(long? b) => b is null ? "?" : string.Format(CultureInfo.InvariantCulture, "{0:0.#} GB", b.Value / (double)HardwareReport.GiB);
}
