using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Shared.Sfo;

namespace PkgLens.Core.Shared;

/// <summary>How an EBOOT/boot executable found in a content folder is signed.</summary>
public enum EbootState
{
    /// <summary>An encrypted, signed retail SELF — needs decrypting (and, if licensed, a RAP) to run off-license.</summary>
    EncryptedSigned,
    /// <summary>A fake-signed SELF (fSELF, key revision 0x8000) — already CFW-ready.</summary>
    FakeSigned,
    /// <summary>A plaintext ELF — not signed at all.</summary>
    PlainElf,
    /// <summary>Present but not a recognizable ELF/SELF.</summary>
    Unknown,
}

/// <summary>A boot executable (EBOOT.BIN / .self) found in the folder.</summary>
public sealed record EbootReport(
    string RelativePath,
    EbootState State,
    bool Npdrm,
    string? License,
    string? ContentId,
    ushort KeyRevision,
    long Size);

/// <summary>An NPDRM data file (EDAT/SDAT) found in the folder.</summary>
public sealed record EdatReport(string RelativePath, bool IsSdat, string License, string ContentId, bool NeedsRap, long Size);

/// <summary>A read-only summary of an extracted PS3 content folder.</summary>
public sealed record GameFolderReport(
    string Folder,
    string? ContentId,
    string? TitleId,
    string? Title,
    string? AppVersion,
    string? Category,
    string? ContentTypeGuess,
    int FileCount,
    int DirectoryCount,
    long TotalBytes,
    bool HasParamSfo,
    IReadOnlyList<EbootReport> Eboots,
    IReadOnlyList<EdatReport> Edats,
    IReadOnlyList<string> Notes);

/// <summary>
/// Inspects an extracted PS3 content folder and reports what it contains — content id / title from
/// <c>PARAM.SFO</c>, file counts and size, the boot executable's sign state (encrypted, fake-signed,
/// or plain ELF), and any NPDRM data files with their license state. Read-only; no keys required,
/// nothing is written. Pairs with <see cref="FolderPackage"/> (pack) and <see cref="EbootResigner"/>.
/// </summary>
public static class GameFolderInfo
{
    public static GameFolderReport Describe(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var root = new DirectoryInfo(folder);
        if (!root.Exists)
            throw new PkgFormatException($"Content folder not found: {folder}");
        SafeFileTree.ThrowIfLink(root);

        var notes = new List<string>();
        SfoTable? sfo = TryReadSfo(root, notes);

        long total = 0;
        int files = 0, dirs = 0;
        var eboots = new List<EbootReport>();
        var edats = new List<EdatReport>();

        foreach (var fsi in SafeFileTree.Enumerate(root))
        {
            if (fsi is DirectoryInfo) { dirs++; continue; }
            if (fsi is not FileInfo file) continue;

            files++;
            total += file.Length;
            string rel = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');
            string lower = file.Name.ToLowerInvariant();

            if (lower is "eboot.bin" || lower.EndsWith(".self") || lower.EndsWith(".sprx"))
                eboots.Add(DescribeEboot(file, rel));
            else if (lower.EndsWith(".edat") || lower.EndsWith(".sdat"))
                edats.Add(DescribeEdat(file, rel));
        }

        string? contentId = sfo?.GetString("CONTENT_ID");
        string? contentType = InferContentType(sfo, eboots.Count > 0);

        return new GameFolderReport(
            Folder: root.FullName,
            ContentId: contentId,
            TitleId: sfo?.TitleId,
            Title: sfo?.Title,
            AppVersion: sfo?.AppVersion,
            Category: sfo?.Category,
            ContentTypeGuess: contentType,
            FileCount: files,
            DirectoryCount: dirs,
            TotalBytes: total,
            HasParamSfo: sfo is not null,
            Eboots: eboots,
            Edats: edats,
            Notes: notes);
    }

    private static EbootReport DescribeEboot(FileInfo file, string rel)
    {
        try
        {
            using var s = file.OpenRead();
            Span<byte> magic = stackalloc byte[4];
            if (s.Read(magic) == 4 && magic[0] == 0x7F && magic[1] == (byte)'E' && magic[2] == (byte)'L' && magic[3] == (byte)'F')
                return new EbootReport(rel, EbootState.PlainElf, false, null, null, 0, file.Length);

            s.Position = 0;
            var info = SelfReader.ParseInfo(s);
            var state = info.IsLikelyFakeSigned ? EbootState.FakeSigned : EbootState.EncryptedSigned;
            return new EbootReport(rel, state, info.IsNpdrm, info.Npdrm?.LicenseText, info.Npdrm?.ContentId,
                info.KeyRevision, file.Length);
        }
        catch (PkgFormatException)
        {
            return new EbootReport(rel, EbootState.Unknown, false, null, null, 0, file.Length);
        }
    }

    private static EdatReport DescribeEdat(FileInfo file, string rel)
    {
        try
        {
            using var s = file.OpenRead();
            var npd = EdatFile.ParseHeader(s);
            return new EdatReport(rel, npd.IsSdat, npd.LicenseText, npd.ContentId, npd.NeedsKlicensee, file.Length);
        }
        catch (PkgFormatException)
        {
            return new EdatReport(rel, false, "?", "", false, file.Length);
        }
    }

    private static string? InferContentType(SfoTable? sfo, bool hasEboot)
    {
        string? c = sfo?.Category?.ToUpperInvariant();
        return c switch
        {
            "HG" or "DG" or "GD" => "GameExec",
            "TH" => "Theme",
            "SD" => "GameData",
            null => hasEboot ? "GameExec (inferred)" : "GameData (inferred)",
            _ => $"category {sfo!.Category}",
        };
    }

    private static SfoTable? TryReadSfo(DirectoryInfo root, List<string> notes)
    {
        var sfoFile = root.GetFiles("PARAM.SFO", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (sfoFile is null)
        {
            notes.Add("no PARAM.SFO at the folder root");
            return null;
        }
        try { return SfoParser.Parse(File.ReadAllBytes(sfoFile.FullName)); }
        catch (PkgFormatException ex) { notes.Add($"PARAM.SFO could not be parsed ({ex.Message})"); return null; }
    }
}
