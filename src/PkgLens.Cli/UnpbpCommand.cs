using PkgLens.Core;
using PkgLens.Core.Pbp;

namespace PkgLens.Cli;

/// <summary>
/// <c>pkglens unpbp &lt;EBOOT.PBP&gt;</c> — splits a PSP PBP container into its parts (PARAM.SFO,
/// icons, DATA.PSP, DATA.PSAR). The container is plaintext, so no keys are needed; the extracted
/// <c>DATA.PSP</c> executable is still encrypted (a separate PSP-SELF layer). Use <c>--list</c> to
/// only report the sections without writing them.
/// </summary>
internal static class UnpbpCommand
{
    public static int Run(ReadOnlySpan<string> args)
    {
        string? input = null, outDir = null;
        bool list = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--list": list = true; break;
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
            Console.Error.WriteLine("error: a <PBP> file (e.g. EBOOT.PBP) is required.");
            Console.Error.WriteLine("usage: pkglens unpbp <EBOOT.PBP> [--out DIR] [--list]");
            return ExitCode.Usage;
        }
        if (!File.Exists(input)) { Console.Error.WriteLine($"error: file not found: {input}"); return ExitCode.Usage; }

        try
        {
            using var stream = File.OpenRead(input);
            var pbp = PbpArchive.Parse(stream);

            Console.WriteLine($"PBP version 0x{pbp.Version:X}, {pbp.Entries.Count} section(s):");
            foreach (var e in pbp.Entries)
                Console.WriteLine($"  {e.Name,-12} {e.Size,14:n0}  @ 0x{e.Offset:X}");

            if (list)
                return ExitCode.Ok;

            string dir = outDir ?? Path.GetFileNameWithoutExtension(input) + "_pbp";
            Directory.CreateDirectory(dir);
            foreach (var e in pbp.Entries)
            {
                string dest = Path.Combine(dir, e.Name);
                using (var d = File.Create(dest))
                    PbpArchive.Extract(stream, e, d);
            }

            Console.WriteLine($"Extracted {pbp.Entries.Count} section(s) to {Path.GetFullPath(dir)}");
            if (pbp.Entries.Any(e => e.Name == "DATA.PSP"))
                Console.WriteLine("Note: DATA.PSP is still an encrypted PSP executable (a separate decryption step).");
            return ExitCode.Ok;
        }
        catch (PkgFormatException ex)
        {
            Console.Error.WriteLine($"unpbp error: {ex.Message}");
            return ExitCode.ParseError;
        }
    }
}
