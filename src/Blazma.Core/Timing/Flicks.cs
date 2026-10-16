using System.Text.Json.Serialization;
using Blazma.Core.Serialization;

namespace Blazma.Core.Timing;

/// <summary>Integer time in flicks (1/705,600,000 s).</summary>
[JsonConverter(typeof(FlicksJsonConverter))]
public readonly record struct Flicks(long Value) : IComparable<Flicks>
{
    /// <summary>Flicks per second.</summary>
    public const long PerSecond = 705_600_000;

    /// <summary>Zero time.</summary>
    public static readonly Flicks Zero = new(0);

    /// <summary>Converts seconds, rounding to the nearest flick.</summary>
    public static Flicks FromSeconds(double s) => new(checked((long)Math.Round(s * PerSecond, MidpointRounding.AwayFromZero)));

    /// <summary>Converts to seconds.</summary>
    public double ToSeconds() => (double)Value / PerSecond;

    /// <summary>Start time of a frame: exact when divisible, otherwise rounded to nearest (midpoint away from zero).</summary>
    public static Flicks FromFrame(long frame, FrameRate rate)
    {
        Int128 num = (Int128)frame * PerSecond * rate.Denominator;
        return new Flicks(checked((long)DivRound(num, rate.Numerator)));
    }

    /// <summary>Nearest frame index (midpoint away from zero).</summary>
    public long ToFrame(FrameRate rate)
    {
        Int128 num = (Int128)Value * rate.Numerator;
        Int128 den = (Int128)PerSecond * rate.Denominator;
        return checked((long)DivRound(num, den));
    }

    /// <summary>Fractional frame position.</summary>
    public double ToFrameExact(FrameRate rate) =>
        (double)Value * rate.Numerator / ((double)PerSecond * rate.Denominator);

    private static Int128 DivRound(Int128 n, Int128 d)
    {
        // d > 0. Round half away from zero.
        Int128 q = Int128.Abs(n);
        Int128 r = (q + d / 2) / d; // odd d has no exact midpoint; even d midpoint rounds up in magnitude
        return n < 0 ? -r : r;
    }

    /// <summary>Sum.</summary>
    public static Flicks operator +(Flicks a, Flicks b) => new(checked(a.Value + b.Value));
    /// <summary>Difference.</summary>
    public static Flicks operator -(Flicks a, Flicks b) => new(checked(a.Value - b.Value));
    /// <summary>Less than.</summary>
    public static bool operator <(Flicks a, Flicks b) => a.Value < b.Value;
    /// <summary>Greater than.</summary>
    public static bool operator >(Flicks a, Flicks b) => a.Value > b.Value;
    /// <summary>Less or equal.</summary>
    public static bool operator <=(Flicks a, Flicks b) => a.Value <= b.Value;
    /// <summary>Greater or equal.</summary>
    public static bool operator >=(Flicks a, Flicks b) => a.Value >= b.Value;

    /// <inheritdoc />
    public int CompareTo(Flicks other) => Value.CompareTo(other.Value);

    /// <summary>Smaller of two values.</summary>
    public static Flicks Min(Flicks a, Flicks b) => a <= b ? a : b;
    /// <summary>Larger of two values.</summary>
    public static Flicks Max(Flicks a, Flicks b) => a >= b ? a : b;
}
