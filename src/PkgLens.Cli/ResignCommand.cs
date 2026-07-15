using PkgLens.Core;
using PkgLens.Core.Shared;
using PkgLens.Core.Ps3.Self;

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
        var opts = new SelfBuilder.FakeSelfOptions();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--npdrm": opts.Npdrm = true; break;
                case "--out":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("error: --out requires a file path."); return ExitCode.Usage; }
                    outPath = args[++i];
                    break;

                // Custom Sign: override the fake-signed app_info / NPDRM fields.
                case "--auth-id":
                    if (!TakeU64(args, ref i, a, out var authId)) return ExitCode.Usage;
                    opts.AuthId = authId; break;
                case "--vendor-id":
                    if (!TakeU32(args, ref i, a, out var vendorId)) return ExitCode.Usage;
                    opts.VendorId = vendorId; break;
                case "--app-version":
                    if (!TakeU64(args, ref i, a, out var appVer)) return ExitCode.Usage;
                    opts.AppVersion = appVer; break;
                case "--type":
                    if (!TakeU32(args, ref i, a, out var type)) return ExitCode.Usage;
                    opts.ProgramType = type; break;
                case "--content-id":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("error: --content-id requires a value."); return ExitCode.Usage; }
                    opts.ContentId = args[++i]; break;
                case "--np-license-type":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("error: --np-license-type requires FREE|LOCAL|NETWORK."); return ExitCode.Usage; }
                    if (!SelfBuilder.TryParseNpLicenseType(args[++i], out uint lic)) { Console.Error.WriteLine($"error: unknown --np-license-type '{args[i]}' (use FREE, LOCAL or NETWORK)."); return ExitCode.Usage; }
                    opts.NpLicenseType = lic; break;
                case "--np-app-type":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("error: --np-app-type requires SPRX|EXEC|USPRX|UEXEC."); return ExitCode.Usage; }
                    if (!SelfBuilder.TryParseNpAppType(args[++i], out uint at)) { Console.Error.WriteLine($"error: unknown --np-app-type '{args[i]}' (use SPRX, EXEC, USPRX or UEXEC)."); return ExitCode.Usage; }
                    opts.NpAppType = at; break;
                case "--fw-version":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("error: --fw-version requires a value, e.g. 4.46."); return ExitCode.Usage; }
                    if (!TryParseFirmware(args[++i], out ulong fw)) { Console.Error.WriteLine($"error: --fw-version '{args[i]}' is not a M.NN version."); return ExitCode.Usage; }
                    opts.FirmwareVersion = fw; break;
                case "--control-flags":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("error: --control-flags requires 32 hex bytes."); return ExitCode.Usage; }
                    if (!TryParseHex(args[++i], 0x20, out byte[] flags)) { Console.Error.WriteLine("error: --control-flags must be 64 hex chars (0x20 bytes)."); return ExitCode.Usage; }
                    opts.ControlFlags = flags; break;

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
            Console.Error.WriteLine("       custom sign: [--auth-id HEX] [--vendor-id HEX] [--app-version HEX] [--type N] [--content-id CID]");
            Console.Error.WriteLine("                    [--fw-version M.NN] [--control-flags 64-HEX]");
            Console.Error.WriteLine("                    [--np-license-type FREE|LOCAL|NETWORK] [--np-app-type SPRX|EXEC|USPRX|UEXEC]");
            return ExitCode.Usage;
        }
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"error: file not found: {input}");
            return ExitCode.Usage;
        }

        bool npdrm = opts.Npdrm;
        try
        {
            byte[] elf = File.ReadAllBytes(input);
            byte[] fself = SelfBuilder.MakeFakeSelf(elf, opts);

            outPath ??= Path.GetFileNameWithoutExtension(input) + ".self";
            AtomicOutput.EnsureDifferentPath(input, outPath);
            AtomicOutput.WriteAllBytes(outPath, fself);

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

    // Parse a numeric option value (accepts 0x-prefixed hex or decimal).
    private static bool TakeU64(ReadOnlySpan<string> args, ref int i, string flag, out ulong value)
    {
        value = 0;
        if (i + 1 >= args.Length) { Console.Error.WriteLine($"error: {flag} requires a value."); return false; }
        string t = args[++i].Trim();
        bool ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(t.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out value)
            : ulong.TryParse(t, out value);
        if (!ok) Console.Error.WriteLine($"error: {flag} '{t}' is not a number.");
        return ok;
    }

    private static bool TakeU32(ReadOnlySpan<string> args, ref int i, string flag, out uint value)
    {
        value = 0;
        if (!TakeU64(args, ref i, flag, out ulong v)) return false;
        if (v > uint.MaxValue) { Console.Error.WriteLine($"error: {flag} value is too large (max 0xFFFFFFFF)."); return false; }
        value = (uint)v;
        return true;
    }

    /// <summary>Parses a firmware version "M.NN" (e.g. 4.46) into scetool's decimal form (major*10000 + minor*100).</summary>
    private static bool TryParseFirmware(string text, out ulong decver)
    {
        decver = 0;
        string[] parts = text.Trim().Split('.');
        if (parts.Length != 2 ||
            !uint.TryParse(parts[0], out uint major) ||
            !uint.TryParse(parts[1], out uint minor) || minor >= 100)
            return false;
        decver = major * 10000UL + minor * 100UL;
        return true;
    }

    /// <summary>Parses exactly <paramref name="byteLen"/> bytes of hex (optional 0x / whitespace).</summary>
    private static bool TryParseHex(string text, int byteLen, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        string t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        t = new string(t.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (t.Length != byteLen * 2) return false;
        try { bytes = Convert.FromHexString(t); return true; }
        catch (FormatException) { return false; }
    }
}
