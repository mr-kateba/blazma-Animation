using System.Globalization;

namespace Blazma.AI;

/// <summary>Whether a tier fits this PC. <see cref="Fits"/> = enough RAM to load it at all; <see cref="GpuFits"/> = the best GPU has
/// the tier's minimum VRAM (then the GPU is used, otherwise the CPU).</summary>
public sealed record TierFit(string TierId, bool Fits, bool GpuFits, string Device, string ReasonEn, string ReasonAr);

/// <summary>The suggested tier and device (<see cref="KimodoSettings.Device"/>): <c>auto</c> = graphics card first with automatic
/// fallback; <c>hybrid</c> = a discrete card with too little memory for the text encoder (it runs on the processor, the motion
/// model on the card); <c>cpu</c> = no discrete graphics card was detected.</summary>
public sealed record ModelRecommendation(string TierId, string Device, GpuInfo? Gpu, string ReasonEn, string ReasonAr, IReadOnlyList<TierFit> Tiers);

/// <summary>Picks a Kimodo package for the machine: weak PCs (little RAM, no discrete GPU) get the light tier on the CPU, strong
/// PCs the bigger text encoder on the GPU. Thresholds come from the catalog and are estimates (R4), so every tier stays selectable.</summary>
public static class HardwareAdvisor
{
    public static ModelRecommendation Recommend(HardwareReport hw, ModelCatalog catalog)
    {
        var gpu = hw.BestGpu;
        var ram = hw.TotalRamBytes is { } r ? r / (double)HardwareReport.GiB : (double?)null;
        var vram = gpu is { Integrated: not true, VramBytes: { } v } ? v / (double)HardwareReport.GiB : (double?)null;

        var fits = catalog.Tiers.Select(t => Fit(t, ram, vram, gpu)).ToList();
        // Largest tier first: its recommended RAM is there, and (if it needs one) a discrete GPU with its recommended VRAM.
        var pick = catalog.Tiers.OrderByDescending(t => t.TotalBytes).FirstOrDefault(t =>
                       ram >= t.RecommendedRamGiB && (!t.AutoNeedsGpu || vram >= t.RecommendedVramGiB))
                   ?? catalog.Tiers.OrderBy(t => t.TotalBytes).First();
        var fit = fits.First(f => f.TierId == pick.Id);

        string en, ar;
        var ramText = ram is { } x ? Gb(x) : "unknown";
        var gpuText = gpu is null ? "no graphics card detected" : $"{gpu.Name}{(gpu.VramBytes is { } b ? $" ({Gb(b / (double)HardwareReport.GiB)} VRAM)" : "")}";
        var gpuTextAr = gpu is null ? "لم تُكتشف بطاقة رسوميات" : $"{gpu.Name}{(gpu.VramBytes is { } b2 ? $" (ذاكرة رسوميات {Gb(b2 / (double)HardwareReport.GiB)})" : "")}";
        if (!fit.Fits)
        {
            en = $"This PC has {ramText} RAM; even the smallest package needs about {pick.MinRamGiB:0} GB. Generation may fail or be very slow.";
            ar = $"ذاكرة هذا الجهاز {ramText}، وأصغر حزمة تحتاج نحو {pick.MinRamGiB:0} GB. قد يفشل التوليد أو يكون بطيئًا جدًا.";
        }
        else if (fit.GpuFits)
        {
            en = $"{ramText} RAM and {gpuText}: the {pick.NameEn} package runs on the graphics card.";
            ar = $"ذاكرة {ramText} و{gpuTextAr}: الحزمة «{pick.NameAr}» تعمل على بطاقة الرسوميات.";
        }
        else
        {
            en = $"{ramText} RAM, {gpuText}: the {pick.NameEn} package on the CPU is the safe choice (slower than a graphics card).";
            ar = $"ذاكرة {ramText}، {gpuTextAr}: الحزمة «{pick.NameAr}» على المعالج هي الخيار الآمن (أبطأ من بطاقة الرسوميات).";
        }
        if (ram is null) { en += " RAM could not be read."; ar += " تعذّرت قراءة حجم الذاكرة."; }
        return new ModelRecommendation(pick.Id, fit.Device, gpu, en, ar, fits);
    }

    static TierFit Fit(ModelTier t, double? ram, double? vram, GpuInfo? gpu)
    {
        var fits = ram is null || ram >= t.MinRamGiB;
        // The GPU path needs Vulkan from a real driver: a discrete card (or one we cannot classify) with the tier's minimum VRAM.
        var gpuFits = vram >= t.MinVramGiB && gpu?.Vendor is "NVIDIA" or "AMD" or "Intel";
        // A discrete card whose memory could not be read (common: the registry has no size for some drivers) still gets
        // "auto": the runner sees the real card and Blazma falls back by itself if it does not fit.
        var discrete = gpu is { Integrated: not true, Vendor: "NVIDIA" or "AMD" or "Intel" };
        var device = gpuFits || (discrete && vram is null) ? "auto" : discrete ? "hybrid" : "cpu";
        string en, ar;
        if (!fits)
        {
            en = $"Needs at least {t.MinRamGiB:0} GB RAM ({t.RecommendedRamGiB:0} GB recommended).";
            ar = $"يحتاج {t.MinRamGiB:0} GB ذاكرة على الأقل (يُنصح بـ{t.RecommendedRamGiB:0} GB).";
        }
        else if (gpuFits)
        {
            en = vram >= t.RecommendedVramGiB ? "Fits on the graphics card." : $"Runs on the graphics card; {t.RecommendedVramGiB:0} GB VRAM recommended.";
            ar = vram >= t.RecommendedVramGiB ? "يعمل على بطاقة الرسوميات." : $"يعمل على بطاقة الرسوميات؛ يُنصح بذاكرة رسوميات {t.RecommendedVramGiB:0} GB.";
        }
        else
        {
            en = ram >= t.RecommendedRamGiB || ram is null ? "Runs on the CPU (slower)." : $"Runs on the CPU, but {t.RecommendedRamGiB:0} GB RAM is recommended.";
            ar = ram >= t.RecommendedRamGiB || ram is null ? "يعمل على المعالج (أبطأ)." : $"يعمل على المعالج، لكن يُنصح بذاكرة {t.RecommendedRamGiB:0} GB.";
        }
        return new TierFit(t.Id, fits, gpuFits, device, en, ar);
    }

    static string Gb(double gib) => gib.ToString(gib < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + " GB";
}
