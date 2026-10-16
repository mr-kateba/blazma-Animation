using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Blazma.Core.Serialization;

namespace Blazma.Core.Ids;

/// <summary>ULID identifier: 48-bit Unix-ms timestamp + 80 random bits, 26 Crockford Base32 characters.</summary>
[JsonConverter(typeof(ObjectIdJsonConverter))]
public readonly record struct ObjectId
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private static readonly object Gate = new();
    private static long s_lastMs = -1;
    private static UInt128 s_lastRandom;
    private const int Length = 26;

    private readonly string? _value;

    private ObjectId(string value) => _value = value;

    /// <summary>The empty id (default value).</summary>
    public static readonly ObjectId Empty = default;

    /// <summary>True for the empty id.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>Canonical uppercase 26-char string, or "" when empty.</summary>
    public string Value => _value ?? "";

    /// <summary>Creates an id for the current time. Ids created in the same millisecond still sort in creation order.</summary>
    public static ObjectId New() => New(DateTimeOffset.UtcNow);

    /// <summary>Creates an id with the given timestamp (monotonic within the same millisecond).</summary>
    public static ObjectId New(DateTimeOffset time)
    {
        long ms = time.ToUnixTimeMilliseconds();
        if (ms < 0 || ms >= (1L << 48)) throw new ArgumentOutOfRangeException(nameof(time));
        UInt128 mask = (UInt128.One << 80) - 1;
        UInt128 rnd;
        lock (Gate)
        {
            if (ms == s_lastMs && s_lastRandom < mask)
            {
                rnd = s_lastRandom + 1;
            }
            else
            {
                Span<byte> b = stackalloc byte[10];
                RandomNumberGenerator.Fill(b);
                rnd = 0;
                foreach (byte x in b) rnd = (rnd << 8) | x;
                // Leave headroom so in-millisecond increments cannot overflow.
                rnd &= mask >> 1;
            }
            if (ms >= s_lastMs)
            {
                s_lastMs = ms;
                s_lastRandom = rnd;
            }
        }
        UInt128 all = ((UInt128)(ulong)ms << 80) | rnd;
        Span<char> chars = stackalloc char[Length];
        for (int i = Length - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(all & 31)];
            all >>= 5;
        }
        return new ObjectId(new string(chars));
    }

    /// <summary>Parses a 26-char ULID, case-insensitively; throws <see cref="FormatException"/>.</summary>
    public static ObjectId Parse(string s) =>
        TryParse(s, out var id) ? id : throw new FormatException($"Invalid ObjectId '{s}'.");

    /// <summary>Tries to parse a 26-char ULID, case-insensitively.</summary>
    public static bool TryParse(string? s, out ObjectId id)
    {
        id = default;
        if (s is null || s.Length != Length) return false;
        Span<char> chars = stackalloc char[Length];
        for (int i = 0; i < Length; i++)
        {
            char c = char.ToUpperInvariant(s[i]);
            if (Alphabet.IndexOf(c) < 0) return false;
            chars[i] = c;
        }
        if (chars[0] > '7') return false; // would overflow 128 bits
        id = new ObjectId(new string(chars));
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
