using System.Globalization;

namespace Blazma.Core.Text;

/// <summary>
/// One convention for file and download sizes everywhere (model packages, download progress, the CUDA package, free disk space,
/// exported files): decimal units, 1 GB = 1,000,000,000 bytes, the way Hugging Face, GitHub and download pages state them.
/// Memory (RAM, video memory) is the exception: it is counted in powers of two and labelled GB, as Windows and the card makers do
/// ("16 GB RAM" is 16 GiB) — see <see cref="MemoryGb"/>.
/// </summary>
public static class ByteSize
{
    public const long KB = 1_000, MB = 1_000_000, GB = 1_000_000_000;
    const long GiB = 1L << 30;

    /// <summary>The number and the unit ("GB", "MB", "KB" or "B") of a size, e.g. 6,198,797,854 bytes → ("6.2", "GB").
    /// The unit is the English symbol; the UI translates the "{0} GB" style templates.</summary>
    public static (string Number, string Unit) Split(long bytes)
    {
        var b = Math.Max(0, bytes);
        // 999,600 KB rounds to 1000 KB: show it as 1.0 MB instead.
        return b >= GB - MB / 2 ? (Num(b / (double)GB), "GB")
            : b >= MB - KB / 2 ? (Num(b / (double)MB), "MB")
            : b >= KB ? (Num(b / (double)KB), "KB")
            : (b.ToString(CultureInfo.InvariantCulture), "B");
    }

    /// <summary>"6.2 GB", "55.3 MB", "553 MB", "7.8 KB", "148 B" (decimal units, invariant digits).</summary>
    public static string Format(long bytes)
    {
        var (n, u) = Split(bytes);
        return n + " " + u;
    }

    /// <summary>Memory in GB of 2^30 bytes, as Windows shows installed RAM: 17,179,869,184 bytes → "16".</summary>
    public static string MemoryGb(long bytes)
    {
        var gib = Math.Max(0, bytes) / (double)GiB;
        return gib.ToString(gib < 10 ? "0.#" : "0", CultureInfo.InvariantCulture);
    }

    // One decimal below 100 of the unit (6.2 GB, 15.2 GB, 55.3 MB), none from 100 up (553 MB).
    static string Num(double v) => v.ToString(v < 100 ? "0.0" : "0", CultureInfo.InvariantCulture);
}
