using PkgLens.Core.Ps3.Npd;

namespace PkgLens.Cli;

/// <summary>
/// Shared helpers for supplying an NPDRM klicensee, either indirectly via a RAP license file
/// (<c>--rap</c>) or directly as raw hex (<c>--klic</c> / <c>--klicensee</c>, scetool's <c>-l</c>).
/// </summary>
internal static class NpKlic
{
    internal sealed record Resolution(byte[]? Klicensee, string? Source, string? RapPath);

    /// <summary>Parses a 16-byte klicensee from 32 hex chars (optional <c>0x</c> / whitespace).</summary>
    public static byte[] ParseHex(string text)
    {
        string t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        t = new string(t.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (t.Length != 32)
            throw new FormatException("a klicensee must be 32 hex characters (16 bytes).");
        return Convert.FromHexString(t);
    }

    /// <summary>Reads a 16-byte RAP file and converts it to the klicensee used to decrypt content.</summary>
    public static byte[] FromRapFile(string path)
    {
        byte[] rap = File.ReadAllBytes(path);
        if (rap.Length != 16)
            throw new FormatException("a RAP file must be exactly 16 bytes.");
        return NpdKeys.RapToKlicensee(rap);
    }

    /// <summary>
    /// Resolves license material in command-line precedence order: explicit klicensee, explicit RAP,
    /// then the RAP library by content id.
    /// </summary>
    public static Resolution Resolve(string? klicHex, string? rapPath, string? contentId, string? rapDirectory)
    {
        if (klicHex is not null)
            return new Resolution(ParseHex(klicHex), "klicensee", null);
        if (rapPath is not null)
            return new Resolution(FromRapFile(rapPath), "rap-file", Path.GetFullPath(rapPath));
        if (!string.IsNullOrWhiteSpace(contentId))
        {
            byte[]? rap = RapStore.Find(contentId, rapDirectory);
            if (rap is not null)
                return new Resolution(NpdKeys.RapToKlicensee(rap), "rap-store", RapStore.PathFor(contentId, rapDirectory));
        }
        return new Resolution(null, null, null);
    }
}
