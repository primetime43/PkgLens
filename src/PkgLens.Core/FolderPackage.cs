using System.Text.RegularExpressions;
using PkgLens.Core.Models;
using PkgLens.Core.Self;
using PkgLens.Core.Sfo;

namespace PkgLens.Core;

/// <summary>Caller-supplied overrides for a folder pack. Any field left null is inferred (Fast Pack).</summary>
public sealed class PackOptions
{
    public string? ContentId { get; set; }
    public string? InstallDirectory { get; set; }
    public uint? DrmType { get; set; }
    public uint? ContentType { get; set; }
    public PkgFinalization Finalization { get; set; } = PkgFinalization.Debug;

    /// <summary>
    /// When set, the folder's <c>EBOOT.BIN</c> is fake-signed (to a fSELF) as it is packed, so the
    /// packaged game boots on a jailbroken (CFW) PS3 without its original license. An encrypted,
    /// <em>licensed</em> EBOOT needs <see cref="EbootKlicensee"/> (from its RAP) to decrypt first;
    /// free-license, debug, plaintext-ELF and already-fSELF EBOOTs need nothing.
    /// </summary>
    public bool ResignEboot { get; set; }

    /// <summary>Optional 16-byte klicensee (from a RAP) for decrypting a licensed EBOOT during resign-on-pack.</summary>
    public byte[]? EbootKlicensee { get; set; }
}

/// <summary>A configured builder plus a record of what was inferred, so the CLI/GUI can report it.</summary>
public sealed record PackPlan(
    PkgBuilder Builder,
    string ContentId,
    string InstallDirectory,
    uint ContentType,
    uint DrmType,
    PkgFinalization Finalization,
    int FileCount,
    int DirectoryCount,
    long TotalBytes,
    IReadOnlyList<string> Notes);

/// <summary>
/// Turns a PS3 content folder into a ready-to-build <see cref="PkgBuilder"/> (the "Pack" operation).
/// With no <see cref="PackOptions"/> overrides this is <b>Fast Pack</b>: content id, install
/// directory and content type are inferred from the folder's <c>PARAM.SFO</c> (and, failing that,
/// the folder name). Supplying overrides makes it <b>Custom Pack</b>. The folder is scanned for
/// sizes only; file bytes are opened lazily at build time.
/// </summary>
public static class FolderPackage
{
    private static readonly Regex ContentIdShape =
        new(@"^[A-Z]{2}\d{4}-[A-Z0-9]+_\d{2}-[A-Z0-9_]+$", RegexOptions.Compiled);

    public static PackPlan Plan(string folder, PackOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        options ??= new PackOptions();

        var root = new DirectoryInfo(folder);
        if (!root.Exists)
            throw new PkgFormatException($"Content folder not found: {folder}");

        var notes = new List<string>();
        SfoTable? sfo = TryReadSfo(root, notes);

        string contentId = ResolveContentId(root, sfo, options.ContentId, notes);
        var parsedCid = ContentId.Parse(contentId);

        string installDir = options.InstallDirectory
            ?? sfo?.TitleId
            ?? parsedCid.TitleId
            ?? parsedCid.Name
            ?? "USRDIR";
        if (options.InstallDirectory is null)
            notes.Add($"install directory: {installDir}");

        uint contentType = options.ContentType ?? InferContentType(root, sfo, notes);
        uint drmType = options.DrmType ?? 3; // free / non-DRM

        var builder = new PkgBuilder
        {
            Finalization = options.Finalization,
            ContentId = contentId,
            InstallDirectory = installDir,
            ContentType = contentType,
            DrmType = drmType,
        };

        long totalBytes = 0;
        int files = 0, dirs = 0;
        Walk(root, string.Empty, builder, options, notes, ref totalBytes, ref files, ref dirs);
        if (files == 0)
            throw new PkgFormatException($"Content folder '{folder}' contains no files to pack.");

        return new PackPlan(builder, contentId, installDir, contentType, drmType,
            options.Finalization, files, dirs, totalBytes, notes);
    }

    private static void Walk(DirectoryInfo dir, string prefix, PkgBuilder builder, PackOptions options,
        List<string> notes, ref long totalBytes, ref int files, ref int dirs)
    {
        foreach (var info in dir.GetFileSystemInfos().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            string name = prefix.Length == 0 ? info.Name : prefix + "/" + info.Name;
            if (info is DirectoryInfo sub)
            {
                builder.AddDirectory(name);
                dirs++;
                Walk(sub, name, builder, options, notes, ref totalBytes, ref files, ref dirs);
            }
            else if (info is FileInfo file)
            {
                if (options.ResignEboot && string.Equals(file.Name, "EBOOT.BIN", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] resigned = ResignEboot(file, options, notes);
                    builder.AddFile(name, resigned, PkgEntryType.Npdrm);
                    totalBytes += resigned.Length;
                }
                else
                {
                    builder.AddFile(name, file.Length, () => File.OpenRead(file.FullName), KindFor(file.Name));
                    totalBytes += file.Length;
                }
                files++;
            }
        }
    }

    /// <summary>Fake-signs the EBOOT during packing (resign-on-pack). Falls back with a note on failure.</summary>
    private static byte[] ResignEboot(FileInfo file, PackOptions options, List<string> notes)
    {
        byte[] raw = File.ReadAllBytes(file.FullName);
        try
        {
            var r = EbootResigner.Resign(raw, options.EbootKlicensee);
            string what = r.Action switch
            {
                EbootResignAction.AlreadyFakeSigned => "already fake-signed (unchanged)",
                EbootResignAction.ResignedFromElf => "signed from ELF → fSELF",
                _ => "decrypted → fake-signed fSELF",
            };
            notes.Add($"EBOOT.BIN resigned: {what}");
            return r.Data;
        }
        catch (PkgFormatException ex)
        {
            // Don't fail the whole pack: keep the original EBOOT and warn loudly.
            notes.Add($"EBOOT.BIN NOT resigned ({ex.Message}) — packed as-is; supply its RAP to resign a licensed EBOOT.");
            return raw;
        }
    }

    /// <summary>Marks EBOOT/self/sprx as NPDRM and edat/sdat as NPDRM EDAT; everything else is a regular file.</summary>
    private static PkgEntryType KindFor(string fileName)
    {
        string lower = fileName.ToLowerInvariant();
        if (lower is "eboot.bin" || lower.EndsWith(".self") || lower.EndsWith(".sprx") || lower.EndsWith(".sinf"))
            return PkgEntryType.Npdrm;
        if (lower.EndsWith(".edat") || lower.EndsWith(".sdat"))
            return PkgEntryType.NpdrmEdat;
        return PkgEntryType.Regular;
    }

    private static SfoTable? TryReadSfo(DirectoryInfo root, List<string> notes)
    {
        var sfoFile = root.GetFiles("PARAM.SFO", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (sfoFile is null)
        {
            notes.Add("no PARAM.SFO at the folder root — inference is limited");
            return null;
        }
        try
        {
            return SfoParser.Parse(File.ReadAllBytes(sfoFile.FullName));
        }
        catch (PkgFormatException ex)
        {
            notes.Add($"PARAM.SFO could not be parsed ({ex.Message})");
            return null;
        }
    }

    private static string ResolveContentId(DirectoryInfo root, SfoTable? sfo, string? explicitId, List<string> notes)
    {
        if (!string.IsNullOrEmpty(explicitId))
            return explicitId;

        string? fromSfo = sfo?.GetString("CONTENT_ID");
        if (!string.IsNullOrEmpty(fromSfo))
        {
            notes.Add($"content id from PARAM.SFO CONTENT_ID: {fromSfo}");
            return fromSfo;
        }

        if (ContentIdShape.IsMatch(root.Name))
        {
            notes.Add($"content id from folder name: {root.Name}");
            return root.Name;
        }

        throw new PkgFormatException(
            "Could not infer a content id: the folder has no PARAM.SFO CONTENT_ID and its name is not a " +
            "content id (e.g. UP0001-NPUB30910_00-EXAMPLE000000001). Pass --content-id to set one.");
    }

    private static uint InferContentType(DirectoryInfo root, SfoTable? sfo, List<string> notes)
    {
        string? category = sfo?.Category;
        // CATEGORY codes: HG/DG/GD → a game; AV → theme/avatar; others fall through to a game default.
        if (category is not null)
        {
            switch (category.ToUpperInvariant())
            {
                case "HG": // HDD game (includes patches/DLC installers)
                case "DG": // disc game
                case "GD": // game data
                    notes.Add($"content type: GameExec (PARAM.SFO CATEGORY={category})");
                    return (uint)PkgContentType.GameExec;
                case "TH": // theme
                    notes.Add($"content type: Theme (PARAM.SFO CATEGORY={category})");
                    return (uint)PkgContentType.Theme;
            }
        }

        bool hasEboot = root.GetFiles("EBOOT.BIN", SearchOption.AllDirectories).Length > 0;
        uint type = hasEboot ? (uint)PkgContentType.GameExec : (uint)PkgContentType.GameData;
        notes.Add($"content type: {(PkgContentType)type} (inferred)");
        return type;
    }
}
