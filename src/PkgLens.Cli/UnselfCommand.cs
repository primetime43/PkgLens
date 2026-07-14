using PkgLens.Core;
using PkgLens.Core.Self;

namespace PkgLens.Cli;

/// <summary>
/// <c>pkglens unself &lt;eboot&gt;</c> — decrypts a retail / NPDRM SELF (EBOOT.BIN, .self, .sprx) back
/// to its plaintext ELF. Free-license and debug SELFs need no key; a licensed SELF needs its RAP
/// (<c>--rap</c>) or a raw klicensee (<c>--klic</c>). The output is a plaintext ELF you can inspect
/// or fake-sign with <c>pkglens resign</c>.
/// </summary>
internal static class UnselfCommand
{
    public static int Run(ReadOnlySpan<string> args)
    {
        string? input = null, outPath = null, rap = null, klicHex = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--out":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("error: --out requires a file path."); return ExitCode.Usage; }
                    outPath = args[++i];
                    break;
                case "--rap":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("error: --rap requires a RAP file path."); return ExitCode.Usage; }
                    rap = args[++i];
                    break;
                case "--klic":
                case "--klicensee":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine($"error: {a} requires a 32-hex-char key."); return ExitCode.Usage; }
                    klicHex = args[++i];
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"error: unknown option '{a}'.");
                        return ExitCode.Usage;
                    }
                    if (input is not null) { Console.Error.WriteLine($"error: unexpected extra argument '{a}'."); return ExitCode.Usage; }
                    input = a;
                    break;
            }
        }

        if (input is null)
        {
            Console.Error.WriteLine("error: an <eboot> SELF file to decrypt is required.");
            Console.Error.WriteLine("usage: pkglens unself <eboot> [--out FILE] [--rap FILE | --klic HEX]");
            return ExitCode.Usage;
        }
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"error: file not found: {input}");
            return ExitCode.Usage;
        }

        byte[]? klic = null;
        try
        {
            if (klicHex is not null) klic = NpKlic.ParseHex(klicHex);
            else if (rap is not null)
            {
                if (!File.Exists(rap)) { Console.Error.WriteLine($"error: RAP file not found: {rap}"); return ExitCode.Usage; }
                klic = NpKlic.FromRapFile(rap);
            }
        }
        catch (FormatException ex) { Console.Error.WriteLine($"error: {ex.Message}"); return ExitCode.Usage; }

        try
        {
            byte[] self = File.ReadAllBytes(input);
            var result = SelfDecryptor.Decrypt(self, klic);

            outPath ??= DefaultOut(input);
            File.WriteAllBytes(outPath, result.Elf);

            string lic = result.WasNpdrm ? (result.License?.ToString() ?? "NPDRM") : "non-NPDRM";
            Console.WriteLine($"Decrypted {self.Length:n0}-byte SELF → {Path.GetFullPath(outPath)} ({result.Elf.Length:n0} bytes)");
            Console.WriteLine($"  key revision : 0x{result.KeyRevision:X4}   license: {lic}" +
                              (result.ContentId is { Length: > 0 } ? $"   content id: {result.ContentId}" : ""));
            Console.WriteLine("  fake-sign it for CFW with:  pkglens resign " + Path.GetFileName(outPath));
            return ExitCode.Ok;
        }
        catch (PkgFormatException ex)
        {
            Console.Error.WriteLine($"unself error: {ex.Message}");
            return ExitCode.ParseError;
        }
    }

    private static string DefaultOut(string input)
    {
        // EBOOT.BIN → EBOOT.ELF; anything.self → anything.elf
        string dir = Path.GetDirectoryName(input) ?? "";
        string name = Path.GetFileNameWithoutExtension(input);
        return Path.Combine(dir, name + ".ELF");
    }
}
