using PkgLens.Core;
using PkgLens.Core.Npd;
using PkgLens.Core.Npd.Psp;

namespace PkgLens.Cli;

/// <summary>
/// <c>pkglens psar info|decrypt &lt;DATA.PSAR&gt;</c> — inspects or decrypts a PSP NPUMDIMG image (the
/// <c>DATA.PSAR</c> inside a minis / PSP-remaster <c>EBOOT.PBP</c>) back to a plain <c>.iso</c>.
/// RAP-licensed images need their klicensee via <c>--rap FILE</c> or <c>--klic HEX</c>.
/// </summary>
internal static class PsarCommand
{
    public static int Run(ReadOnlySpan<string> args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.Error.WriteLine("usage: pkglens psar info <DATA.PSAR> [--rap FILE | --klic HEX]");
            Console.Error.WriteLine("       pkglens psar decrypt <DATA.PSAR> [--rap FILE | --klic HEX] [--out FILE]");
            return ExitCode.Usage;
        }

        string sub = args[0].ToLowerInvariant();
        string? input = null, rap = null, klicHex = null, outPath = null;
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--rap": if (++i >= args.Length) { return Missing("--rap"); } rap = args[i]; break;
                case "--klic": case "--klicensee": if (++i >= args.Length) { return Missing(a); } klicHex = args[i]; break;
                case "--out": if (++i >= args.Length) { return Missing("--out"); } outPath = args[i]; break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) { Console.Error.WriteLine($"error: unknown option '{a}'."); return ExitCode.Usage; }
                    if (input is not null) { Console.Error.WriteLine($"error: unexpected extra argument '{a}'."); return ExitCode.Usage; }
                    input = a;
                    break;
            }
        }

        if (input is null) { Console.Error.WriteLine("error: a <DATA.PSAR> file is required."); return ExitCode.Usage; }
        if (!File.Exists(input)) { Console.Error.WriteLine($"error: file not found: {input}"); return ExitCode.Usage; }

        byte[]? klic;
        try { klic = ResolveKlic(rap, klicHex); }
        catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); return ExitCode.Usage; }

        try
        {
            using var src = File.OpenRead(input);

            if (sub == "info")
            {
                var head = new byte[0x100];
                src.ReadExactly(head, 0, head.Length);
                var info = NpumdImg.ParseHeader(head, klic);
                Console.WriteLine($"Content ID : {info.ContentId}");
                Console.WriteLine($"Disc ID    : {info.DiscId}");
                Console.WriteLine($"NP flags   : 0x{info.NpFlags:X}  ({(info.NeedsKlicensee ? "RAP-licensed" : "fixed-key")})");
                Console.WriteLine($"Sector size: 0x{info.SectorSize:X}");
                Console.WriteLine($"Block basis: {info.BlockBasis} sectors ({info.BlockSize:n0} bytes/block)");
                Console.WriteLine($"Sectors    : {info.TotalSectors:n0}");
                Console.WriteLine($"Blocks     : {info.BlockCount:n0}");
                Console.WriteLine($"ISO size   : {info.IsoSize:n0} bytes");
                Console.WriteLine($"Header     : {(info.HeaderValid ? "decrypted OK (klicensee correct)" : "INVALID — wrong klicensee/RAP")}");
                return info.HeaderValid ? ExitCode.Ok : ExitCode.KeyOrDecryptError;
            }

            if (sub == "decrypt")
            {
                string dest = outPath ?? Path.ChangeExtension(input, ".iso");
                using (var dst = File.Create(dest))
                    NpumdImg.DecryptToIso(src, dst, klic);
                Console.WriteLine($"Decrypted NPUMDIMG → {Path.GetFullPath(dest)} ({new FileInfo(dest).Length:n0} bytes)");
                return ExitCode.Ok;
            }

            Console.Error.WriteLine($"error: unknown subcommand '{sub}' (expected info or decrypt).");
            return ExitCode.Usage;
        }
        catch (PkgKeyException ex) { Console.Error.WriteLine($"key error: {ex.Message}"); return ExitCode.KeyOrDecryptError; }
        catch (PkgFormatException ex) { Console.Error.WriteLine($"parse error: {ex.Message}"); return ExitCode.ParseError; }
    }

    private static byte[]? ResolveKlic(string? rap, string? klicHex)
    {
        if (klicHex is not null) return NpKlic.ParseHex(klicHex);
        if (rap is not null)
        {
            if (!File.Exists(rap)) throw new FileNotFoundException($"RAP not found: {rap}");
            return NpKlic.FromRapFile(rap);
        }
        return null;
    }

    private static int Missing(string flag)
    {
        Console.Error.WriteLine($"error: {flag} requires a value.");
        return ExitCode.Usage;
    }
}
