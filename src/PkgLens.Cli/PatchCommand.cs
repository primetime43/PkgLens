using System.Buffers.Binary;
using System.Globalization;
using PkgLens.Core;
using PkgLens.Core.Npd;
using PkgLens.Core.Self;

namespace PkgLens.Cli;

/// <summary>
/// <c>pkglens patch &lt;eboot&gt;</c> — "Magic Patch": apply static byte edits to a PS3 executable so it
/// boots on CFW. The headline patch (<c>--sdk-version</c>) lowers the firmware/SDK version the game
/// demands. Generic <c>--find/--replace</c> and <c>--at</c> patches cover other known edits. Accepts a
/// plaintext ELF or an encrypted EBOOT.BIN/.self (decrypted first, then re-fake-signed on output).
/// </summary>
internal static class PatchCommand
{
    private const uint SceMagic = 0x53434500;
    private const uint ElfMagic = 0x7F454C46;

    public static int Run(ReadOnlySpan<string> args)
    {
        string? input = null, outPath = null, sdk = null, rap = null, klicHex = null;
        var patterns = new List<(byte[] find, byte[] replace)>();
        var offsets = new List<(int at, byte[] bytes)>();
        bool resign = false, npdrmOverride = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--out": if (!Take(args, ref i, a, out outPath)) return ExitCode.Usage; break;
                case "--sdk-version": if (!Take(args, ref i, a, out sdk)) return ExitCode.Usage; break;
                case "--rap": if (!Take(args, ref i, a, out rap)) return ExitCode.Usage; break;
                case "--klic": if (!Take(args, ref i, a, out klicHex)) return ExitCode.Usage; break;
                case "--resign": resign = true; break;
                case "--npdrm": npdrmOverride = true; break;

                case "--find":
                {
                    if (!Take(args, ref i, a, out var f) || i + 1 >= args.Length || args[i + 1] != "--replace")
                    {
                        Console.Error.WriteLine("error: --find HEX must be followed by --replace HEX.");
                        return ExitCode.Usage;
                    }
                    i++; // consume --replace
                    if (!Take(args, ref i, "--replace", out var r)) return ExitCode.Usage;
                    if (!TryHex(f, out var fb) || !TryHex(r, out var rb)) { Console.Error.WriteLine("error: --find/--replace need hex bytes."); return ExitCode.Usage; }
                    if (fb.Length != rb.Length) { Console.Error.WriteLine("error: --find and --replace must be the same length."); return ExitCode.Usage; }
                    patterns.Add((fb, rb));
                    break;
                }

                case "--at":
                {
                    if (!Take(args, ref i, a, out var spec)) return ExitCode.Usage;
                    int eq = spec.IndexOf('=');
                    if (eq <= 0 || !TryParseOffset(spec[..eq], out int off) || !TryHex(spec[(eq + 1)..], out var bytes))
                    {
                        Console.Error.WriteLine("error: --at expects OFFSET=HEXBYTES, e.g. --at 0x1234=90909090.");
                        return ExitCode.Usage;
                    }
                    offsets.Add((off, bytes));
                    break;
                }

                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) { Console.Error.WriteLine($"error: unknown option '{a}'."); return ExitCode.Usage; }
                    if (input is not null) { Console.Error.WriteLine($"error: unexpected extra argument '{a}'."); return ExitCode.Usage; }
                    input = a;
                    break;
            }
        }

        if (input is null)
        {
            Console.Error.WriteLine("error: an <eboot> (ELF or SELF/EBOOT.BIN) to patch is required.");
            Console.Error.WriteLine("usage: pkglens patch <eboot> [--sdk-version VER] [--find HEX --replace HEX] [--at OFF=HEX]");
            Console.Error.WriteLine("                    [--resign] [--npdrm] [--rap FILE | --klic HEX] [--out FILE]");
            return ExitCode.Usage;
        }
        if (!File.Exists(input)) { Console.Error.WriteLine($"error: file not found: {input}"); return ExitCode.Usage; }
        if (sdk is null && patterns.Count == 0 && offsets.Count == 0)
        {
            Console.Error.WriteLine("error: nothing to patch — pass --sdk-version, --find/--replace, or --at.");
            return ExitCode.Usage;
        }

        byte[]? klic = null;
        if (klicHex is not null)
        {
            if (!TryHex(klicHex, out klic) || klic.Length != 16) { Console.Error.WriteLine("error: --klic must be 16 bytes (32 hex chars)."); return ExitCode.Usage; }
        }
        else if (rap is not null)
        {
            if (!File.Exists(rap)) { Console.Error.WriteLine($"error: RAP not found: {rap}"); return ExitCode.Usage; }
            var rapBytes = File.ReadAllBytes(rap);
            if (rapBytes.Length != 16) { Console.Error.WriteLine("error: a RAP must be 16 bytes."); return ExitCode.Usage; }
            klic = NpdKeys.RapToKlicensee(rapBytes);
        }

        try
        {
            byte[] raw = File.ReadAllBytes(input);
            if (raw.Length < 4) { Console.Error.WriteLine("error: file is too small."); return ExitCode.ParseError; }
            uint magic = BinaryPrimitives.ReadUInt32BigEndian(raw);

            byte[] elf;
            bool wasSelf = false, npdrm = npdrmOverride;
            if (magic == SceMagic)
            {
                var dec = SelfDecryptor.Decrypt(raw, klic);
                elf = dec.Elf;
                wasSelf = true;
                npdrm = dec.WasNpdrm || npdrmOverride;
                Console.WriteLine($"Decrypted SELF → ELF ({elf.Length:n0} bytes).");
            }
            else if (magic == ElfMagic)
            {
                elf = raw;
            }
            else
            {
                Console.Error.WriteLine("error: input is neither an ELF nor a SELF/EBOOT.BIN.");
                return ExitCode.ParseError;
            }

            // ---- apply patches ----
            if (sdk is not null)
            {
                SdkVersion? prev = ApplySdkVersion(elf, sdk, out string? err);
                if (err is not null) { Console.Error.WriteLine($"error: {err}"); return ExitCode.Usage; }
                if (prev is null)
                    Console.WriteLine("  sdk-version: no sys_process_param in this ELF — skipped.");
                else
                {
                    var now = EbootPatcher.FindSdkVersion(elf)!;
                    Console.WriteLine($"  sdk-version: {prev.Display} (0x{prev.Value:X8}) → {now.Display} (0x{now.Value:X8})");
                }
            }
            foreach (var (find, replace) in patterns)
            {
                int n = EbootPatcher.PatchPattern(elf, find, replace);
                Console.WriteLine($"  find/replace: {n} occurrence(s) of {Convert.ToHexString(find)}");
            }
            foreach (var (at, bytes) in offsets)
            {
                EbootPatcher.PatchAt(elf, at, bytes);
                Console.WriteLine($"  at 0x{at:X}: wrote {bytes.Length} byte(s)");
            }

            // ---- output: re-fake-sign if the input was a SELF or --resign was asked ----
            bool emitSelf = wasSelf || resign;
            byte[] output = emitSelf ? SelfBuilder.MakeFakeSelf(elf, npdrm) : elf;

            outPath ??= DefaultOut(input, emitSelf);
            File.WriteAllBytes(outPath, output);

            Console.WriteLine($"Patched → {Path.GetFullPath(outPath)} ({output.Length:n0} bytes)" +
                              (emitSelf ? $"  [fake-signed {(npdrm ? "NPDRM" : "NON-DRM")} SELF]" : "  [ELF]"));
            if (emitSelf)
                Console.WriteLine("Note: runs on a jailbroken (CFW) PS3 — not on stock retail.");
            return ExitCode.Ok;
        }
        catch (PkgFormatException ex) { Console.Error.WriteLine($"patch error: {ex.Message}"); return ExitCode.ParseError; }
    }

    private static SdkVersion? ApplySdkVersion(byte[] elf, string spec, out string? error)
    {
        error = null;
        if (spec.Contains('.'))
        {
            var parts = spec.Split('.');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor) || major is < 0 or > 15 || minor is < 0 or > 99)
            {
                error = $"--sdk-version '{spec}' should be a firmware like 4.00, or a raw 0x-value.";
                return null;
            }
            return EbootPatcher.SetFirmwareVersion(elf, major, minor);
        }

        string body = spec.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? spec[2..] : spec;
        if (!uint.TryParse(body, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
        {
            error = $"--sdk-version '{spec}' is not a firmware (4.00) or a hex value (0x00400001).";
            return null;
        }
        return EbootPatcher.SetSdkVersionRaw(elf, value);
    }

    private static string DefaultOut(string input, bool emitSelf)
    {
        string dir = Path.GetDirectoryName(input) ?? "";
        string name = Path.GetFileNameWithoutExtension(input);
        return Path.Combine(dir, emitSelf ? name + ".patched.self" : name + ".patched.elf");
    }

    private static bool Take(ReadOnlySpan<string> args, ref int i, string flag, out string value)
    {
        if (i + 1 >= args.Length) { Console.Error.WriteLine($"error: {flag} requires a value."); value = ""; return false; }
        value = args[++i];
        return true;
    }

    private static bool TryHex(string s, out byte[] bytes)
    {
        s = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        try { bytes = Convert.FromHexString(s); return true; }
        catch (FormatException) { bytes = Array.Empty<byte>(); return false; }
    }

    private static bool TryParseOffset(string s, out int offset)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset);
        return int.TryParse(s, out offset);
    }
}
