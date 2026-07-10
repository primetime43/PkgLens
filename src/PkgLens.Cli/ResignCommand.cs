using PkgLens.Core;
using PkgLens.Core.Self;

namespace PkgLens.Cli;

/// <summary>
/// <c>pkglens resign &lt;elf&gt;</c> — builds a fake-signed SELF (fSELF) from a plaintext ELF so a
/// repacked game boots on a jailbroken (CFW) PS3 without its original license. No keys; the output
/// runs only where signature checks are patched (CFW), never on stock retail.
/// </summary>
internal static class ResignCommand
{
    public static int Run(ReadOnlySpan<string> args)
    {
        string? input = null, outPath = null;
        bool npdrm = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--npdrm": npdrm = true; break;
                case "--out":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("error: --out requires a file path."); return ExitCode.Usage; }
                    outPath = args[++i];
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
            Console.Error.WriteLine("error: an <elf> file to resign is required.");
            Console.Error.WriteLine("usage: pkglens resign <elf> [--out FILE] [--npdrm]");
            return ExitCode.Usage;
        }
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"error: file not found: {input}");
            return ExitCode.Usage;
        }

        try
        {
            byte[] elf = File.ReadAllBytes(input);
            byte[] fself = SelfBuilder.MakeFakeSelf(elf, npdrm);

            outPath ??= Path.GetFileNameWithoutExtension(input) + ".self";
            File.WriteAllBytes(outPath, fself);

            Console.WriteLine($"Resigned {elf.Length:n0}-byte ELF → {Path.GetFullPath(outPath)} ({fself.Length:n0} bytes)");
            Console.WriteLine($"  type : fake-signed {(npdrm ? "NPDRM" : "NON-DRM")} SELF (key revision 0x8000)");
            Console.WriteLine("Note: runs on a jailbroken (CFW) PS3 — signature checks patched. Not valid on stock retail.");
            return ExitCode.Ok;
        }
        catch (PkgFormatException ex)
        {
            Console.Error.WriteLine($"resign error: {ex.Message}");
            return ExitCode.ParseError;
        }
    }
}
