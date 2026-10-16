using System.Globalization;
using System.Text.Json.Serialization;
using Blazma.Core.Serialization;

namespace Blazma.Core.Timing;

/// <summary>A rational frame rate (frames per second), always stored in lowest terms.</summary>
[JsonConverter(typeof(FrameRateJsonConverter))]
public readonly record struct FrameRate
{
    /// <summary>24 fps.</summary>
    public static readonly FrameRate Fps24 = new(24);
    /// <summary>25 fps.</summary>
    public static readonly FrameRate Fps25 = new(25);
    /// <summary>30 fps.</summary>
    public static readonly FrameRate Fps30 = new(30);
    /// <summary>60 fps.</summary>
    public static readonly FrameRate Fps60 = new(60);
    /// <summary>24000/1001 fps.</summary>
    public static readonly FrameRate Ntsc23976 = new(24000, 1001);
    /// <summary>30000/1001 fps.</summary>
    public static readonly FrameRate Ntsc2997 = new(30000, 1001);

    /// <summary>Creates a reduced rate; both parts must be positive.</summary>
    public FrameRate(int numerator, int denominator = 1)
    {
        if (numerator <= 0) throw new ArgumentOutOfRangeException(nameof(numerator));
        if (denominator <= 0) throw new ArgumentOutOfRangeException(nameof(denominator));
        int g = Gcd(numerator, denominator);
        Numerator = numerator / g;
        Denominator = denominator / g;
    }

    /// <summary>Reduced numerator.</summary>
    public int Numerator { get; }
    /// <summary>Reduced denominator.</summary>
    public int Denominator { get; }
    /// <summary>Rate as a double.</summary>
    public double FramesPerSecond => (double)Numerator / Denominator;

    /// <summary>True when one frame is a whole number of flicks.</summary>
    public bool IsExactInFlicks => Numerator != 0 && (Flicks.PerSecond * Denominator) % Numerator == 0;

    /// <summary>"30" or "30000/1001".</summary>
    public override string ToString() =>
        Denominator == 1
            ? Numerator.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{Numerator}/{Denominator}");

    /// <summary>Parses "N" or "N/D" (no decimals). Throws <see cref="FormatException"/>.</summary>
    public static FrameRate Parse(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        string[] parts = s.Split('/');
        if (parts.Length is < 1 or > 2) throw new FormatException($"Invalid frame rate '{s}'.");
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            throw new FormatException($"Invalid frame rate '{s}'.");
        int d = 1;
        if (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out d))
            throw new FormatException($"Invalid frame rate '{s}'.");
        if (n <= 0 || d <= 0) throw new FormatException($"Invalid frame rate '{s}'.");
        return new FrameRate(n, d);
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
