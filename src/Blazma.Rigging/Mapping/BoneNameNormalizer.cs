using System.Text.RegularExpressions;

namespace Blazma.Rigging.Mapping;

/// <summary>
/// Splits a rig bone name into a character side and a normalized base key, independent of the
/// authoring convention: "mixamorig:LeftUpLeg" → (Left, "upleg"), "Bip01 L Thigh" → (Left, "thigh"),
/// "DEF-upper_arm.L" → (Left, "upperarm"), "J_Bip_R_Hand" → (Right, "hand"), "spine.001" → (null, "spine1").
/// </summary>
public static partial class BoneNameNormalizer
{
    public static (Side? Side, string BaseName) Parse(string boneName)
    {
        var p = ParseFull(boneName);
        return (p.Side, p.Key);
    }

    internal readonly record struct Parsed(Side? Side, string Key, int Penalty);

    internal static Parsed ParseFull(string name)
    {
        string s = name.Trim();
        int cut = Math.Max(s.LastIndexOf(':'), s.LastIndexOf('|'));
        if (cut >= 0 && cut < s.Length - 1) s = s[(cut + 1)..];

        if (BipRootRegex().IsMatch(s)) return new Parsed(null, "bip01", 0);

        Side? side = null;
        int penalty = 0;

        var vrm = VrmRegex().Match(s);
        if (vrm.Success)
        {
            side = vrm.Groups[1].Value.ToUpperInvariant() switch { "L" => Side.Left, "R" => Side.Right, _ => null };
            s = vrm.Groups[2].Value;
        }
        else
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var (prefix, pen) in Prefixes)
                {
                    if (s.Length > prefix.Length && s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        s = s[prefix.Length..];
                        penalty += pen;
                        changed = true;
                    }
                }
                var bip = BipPrefixRegex().Match(s);
                if (bip.Success && bip.Length < s.Length) { s = s[bip.Length..]; changed = true; }
            }
        }

        var tokens = new List<string>();
        foreach (string raw in SeparatorRegex().Split(s))
            foreach (string t in CamelRegex().Split(raw))
                if (t.Length > 0) tokens.Add(t.ToLowerInvariant());

        if (side is null)
        {
            for (int i = 0; i < tokens.Count && side is null; i++)
            {
                if (tokens.Count > 1 && tokens[i] is "l" or "left") { side = Side.Left; tokens.RemoveAt(i); }
                else if (tokens.Count > 1 && tokens[i] is "r" or "right") { side = Side.Right; tokens.RemoveAt(i); }
            }
        }

        string key = LeadingZeroRegex().Replace(string.Concat(tokens), "");
        if (side is null && key.Length > 5)
        {
            if (key.StartsWith("left", StringComparison.Ordinal)) { side = Side.Left; key = key[4..]; }
            else if (key.StartsWith("right", StringComparison.Ordinal)) { side = Side.Right; key = key[5..]; }
            else if (key.EndsWith("left", StringComparison.Ordinal)) { side = Side.Left; key = key[..^4]; }
            else if (key.EndsWith("right", StringComparison.Ordinal)) { side = Side.Right; key = key[..^5]; }
        }
        return new Parsed(side, key, penalty);
    }

    /// <summary>Lowercase with separators (space _ . - :) removed; used to compare against standard names.</summary>
    internal static string Condense(string name) =>
        string.Concat(name.Where(c => c is not (' ' or '_' or '.' or '-' or ':'))).ToLowerInvariant();

    // Prefix → priority penalty (copies authored for deformation are preferred over originals/helpers).
    static readonly (string Prefix, int Penalty)[] Prefixes =
    {
        ("DEF-", 0), ("DEF_", 0), ("ORG-", 3), ("ORG_", 3), ("CC_Base_", 0),
    };

    [GeneratedRegex(@"^bip0*1$", RegexOptions.IgnoreCase)]
    private static partial Regex BipRootRegex();

    [GeneratedRegex(@"^bip0*1[ _]+", RegexOptions.IgnoreCase)]
    private static partial Regex BipPrefixRegex();

    [GeneratedRegex(@"^J_Bip_([CLR])_(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex VrmRegex();

    [GeneratedRegex(@"[ _.\-:]+")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex CamelRegex();

    // "spine01"/"spine001" → "spine1"; "toe0" is untouched.
    [GeneratedRegex(@"(?<=[a-z])0+(?=[1-9])")]
    private static partial Regex LeadingZeroRegex();
}
