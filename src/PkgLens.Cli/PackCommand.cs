using System.Globalization;
using PkgLens.Core;
using PkgLens.Core.Keys;
using PkgLens.Core.Models;

namespace PkgLens.Cli;

/// <summary>
/// <c>pkglens pack &lt;folder&gt;</c> — builds a PS3 .pkg from a content folder. With no options it
/// is Fast Pack (content id / install dir / content type inferred from PARAM.SFO); the options make
/// it Custom Pack. Produces a non-finalized (debug) package by default; <c>--retail</c> produces an
/// unsigned retail-encrypted package (needs the runtime key). PkgLens never finalizes or signs.
/// </summary>
internal static class PackCommand
{
    public static int Run(ReadOnlySpan<string> args)
    {
        string? folder = null, outPath = null, keysDir = null;
        // Default to retail-encrypted: it's the format a jailbroken (CFW) PS3 installs. Use --debug for
        // a self-contained non-finalized package (RPCS3 / dev consoles, no key needed).
        var options = new PackOptions { Finalization = PkgFinalization.Retail };

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--retail": options.Finalization = PkgFinalization.Retail; break;
                case "--debug": options.Finalization = PkgFinalization.Debug; break;
                case "--resign": options.ResignEboot = true; break;

                case "--rap":
                    if (!Next(args, ref i, a, out var rapPath)) return ExitCode.Usage;
                    if (!File.Exists(rapPath)) { Console.Error.WriteLine($"error: RAP file not found: {rapPath}"); return ExitCode.Usage; }
                    var rapBytes = File.ReadAllBytes(rapPath);
                    if (rapBytes.Length != 16) { Console.Error.WriteLine("error: a RAP file must be exactly 16 bytes."); return ExitCode.Usage; }
                    options.EbootKlicensee = PkgLens.Core.Npd.NpdKeys.RapToKlicensee(rapBytes);
                    break;

                case "--out": if (!Next(args, ref i, a, out outPath)) return ExitCode.Usage; break;
                case "--keys": if (!Next(args, ref i, a, out keysDir)) return ExitCode.Usage; break;
                case "--content-id": if (!Next(args, ref i, a, out var cid)) return ExitCode.Usage; options.ContentId = cid; break;
                case "--install-dir": if (!Next(args, ref i, a, out var dir)) return ExitCode.Usage; options.InstallDirectory = dir; break;

                case "--content-type":
                    if (!Next(args, ref i, a, out var ct)) return ExitCode.Usage;
                    if (!TryParseContentType(ct, out uint ctv))
                    {
                        Console.Error.WriteLine($"error: unknown --content-type '{ct}'. Use a name (GameExec, GameData, Theme, …) or a number.");
                        return ExitCode.Usage;
                    }
                    options.ContentType = ctv;
                    break;

                case "--drm-type":
                    if (!Next(args, ref i, a, out var dt)) return ExitCode.Usage;
                    if (!TryParseUInt(dt, out uint dtv))
                    {
                        Console.Error.WriteLine($"error: --drm-type '{dt}' is not a number.");
                        return ExitCode.Usage;
                    }
                    options.DrmType = dtv;
                    break;

                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"error: unknown option '{a}'.");
                        return ExitCode.Usage;
                    }
                    if (folder is not null)
                    {
                        Console.Error.WriteLine($"error: unexpected extra argument '{a}'.");
                        return ExitCode.Usage;
                    }
                    folder = a;
                    break;
            }
        }

        if (folder is null)
        {
            Console.Error.WriteLine("error: a <folder> to pack is required.");
            Console.Error.WriteLine("usage: pkglens pack <folder> [--out FILE] [--content-id CID] [--install-dir DIR]");
            Console.Error.WriteLine("                   [--content-type T] [--drm-type N] [--retail] [--keys DIR]");
            return ExitCode.Usage;
        }
        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"error: folder not found: {folder}");
            return ExitCode.Usage;
        }

        IKeyProvider keys = new FileKeyProvider(keysDir);

        try
        {
            PackPlan plan = FolderPackage.Plan(folder, options);

            outPath ??= SanitizeFileName(plan.ContentId) + ".pkg";

            Console.WriteLine($"Packing {plan.FileCount} file(s), {plan.DirectoryCount} folder(s) — {plan.TotalBytes:n0} bytes");
            Console.WriteLine($"  content id   : {plan.ContentId}");
            Console.WriteLine($"  install dir  : {plan.InstallDirectory}");
            Console.WriteLine($"  content type : {ContentTypeName(plan.ContentType)}");
            Console.WriteLine($"  drm type     : {plan.DrmType}");
            Console.WriteLine($"  finalization : {(plan.Finalization == PkgFinalization.Retail ? "retail (unsigned)" : "non-finalized (debug)")}");
            foreach (var note in plan.Notes)
                Console.WriteLine($"  · {note}");

            using (var dst = File.Create(outPath))
                plan.Builder.Build(dst, keys);

            long size = new FileInfo(outPath).Length;
            Console.WriteLine($"Wrote {size:n0} bytes → {Path.GetFullPath(outPath)}");
            if (plan.Finalization == PkgFinalization.Retail)
                Console.WriteLine("Note: retail-encrypted, unsigned. Installs on a jailbroken (CFW) PS3 — the CFW patches skip " +
                                  "the signature check. It will NOT install on a stock retail console.");
            else
                Console.WriteLine("Note: non-finalized (debug) package — for RPCS3 / dev consoles. For a CFW console use --retail.");
            return ExitCode.Ok;
        }
        catch (PkgKeyException ex) { Console.Error.WriteLine($"key error: {ex.Message}"); return ExitCode.KeyOrDecryptError; }
        catch (PkgFormatException ex) { Console.Error.WriteLine($"pack error: {ex.Message}"); return ExitCode.ParseError; }
    }

    private static bool Next(ReadOnlySpan<string> args, ref int i, string flag, out string value)
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine($"error: {flag} requires an argument.");
            value = string.Empty;
            return false;
        }
        value = args[++i];
        return true;
    }

    private static bool TryParseContentType(string text, out uint value)
    {
        if (Enum.TryParse<PkgContentType>(text, ignoreCase: true, out var named))
        {
            value = (uint)named;
            return true;
        }
        return TryParseUInt(text, out value);
    }

    private static bool TryParseUInt(string text, out uint value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return uint.TryParse(text, out value);
    }

    private static string ContentTypeName(uint value) =>
        Enum.IsDefined(typeof(PkgContentType), value) ? $"{(PkgContentType)value} (0x{value:X})" : $"0x{value:X}";

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length == 0 ? "package" : name;
    }
}
