using PkgLens.Core;
using PkgLens.Core.Npd.Psp;

namespace PkgLens.Cli;

/// <summary>
/// <c>pkglens undoc &lt;DOCUMENT.DAT&gt;</c> — decrypts a PSP / PS-minis software manual into its PNG
/// pages. The per-document key comes from the sibling <c>DOCINFO.EDAT</c> (auto-located next to the
/// DOCUMENT.DAT, or given with <c>--docinfo</c>); without one, a fixed default key is tried. All keys
/// are public — nothing is signed or re-encrypted.
/// </summary>
internal static class UndocCommand
{
    public static int Run(ReadOnlySpan<string> args)
    {
        string? input = null, docinfo = null, outDir = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--docinfo":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("error: --docinfo requires a file path."); return ExitCode.Usage; }
                    docinfo = args[++i];
                    break;
                case "--out":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("error: --out requires a directory path."); return ExitCode.Usage; }
                    outDir = args[++i];
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) { Console.Error.WriteLine($"error: unknown option '{a}'."); return ExitCode.Usage; }
                    if (input is not null) { Console.Error.WriteLine($"error: unexpected extra argument '{a}'."); return ExitCode.Usage; }
                    input = a;
                    break;
            }
        }

        if (input is null)
        {
            Console.Error.WriteLine("error: a <DOCUMENT.DAT> file is required.");
            Console.Error.WriteLine("usage: pkglens undoc <DOCUMENT.DAT> [--docinfo DOCINFO.EDAT] [--out DIR]");
            return ExitCode.Usage;
        }
        if (!File.Exists(input)) { Console.Error.WriteLine($"error: file not found: {input}"); return ExitCode.Usage; }

        // Auto-locate DOCINFO.EDAT next to the DOCUMENT.DAT when not given explicitly.
        if (docinfo is null)
        {
            string sibling = Path.Combine(Path.GetDirectoryName(input) ?? ".", "DOCINFO.EDAT");
            if (File.Exists(sibling)) docinfo = sibling;
        }
        else if (!File.Exists(docinfo))
        {
            Console.Error.WriteLine($"error: DOCINFO.EDAT not found: {docinfo}");
            return ExitCode.Usage;
        }

        try
        {
            byte[] doc = File.ReadAllBytes(input);
            byte[]? edat = docinfo is not null ? File.ReadAllBytes(docinfo) : null;
            Console.WriteLine(edat is not null
                ? $"Using key from {Path.GetFileName(docinfo)}."
                : "No DOCINFO.EDAT found — trying the fixed default key.");

            var pages = PspDocument.DecryptPages(doc, edat);
            if (pages.Count == 0)
            {
                Console.Error.WriteLine("No manual pages were found in this DOCUMENT.DAT.");
                return ExitCode.ParseError;
            }

            string dir = outDir ?? Path.GetFileNameWithoutExtension(input) + "_pages";
            Directory.CreateDirectory(dir);
            for (int i = 0; i < pages.Count; i++)
            {
                string dest = Path.Combine(dir, $"page_{i + 1:D3}.png");
                File.WriteAllBytes(dest, pages[i]);
                Console.WriteLine($"  page_{i + 1:D3}.png  ({pages[i].Length:n0} bytes)");
            }
            Console.WriteLine($"Decrypted {pages.Count} manual page(s) → {Path.GetFullPath(dir)}");
            return ExitCode.Ok;
        }
        catch (PkgKeyException ex) { Console.Error.WriteLine($"key error: {ex.Message}"); return ExitCode.KeyOrDecryptError; }
        catch (PkgFormatException ex) { Console.Error.WriteLine($"undoc error: {ex.Message}"); return ExitCode.ParseError; }
    }
}
