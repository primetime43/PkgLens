using System.Text.Json;
using PkgLens.Core;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Cli;

/// <summary>Human-readable and <c>--json</c> renderers for each subcommand.</summary>
internal static class Render
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static void WriteJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOpts));

    public static void Info(PkgInfo info, Options o)
    {
        var h = info.Header;
        if (o.Json)
        {
            WriteJson(new
            {
                contentId = new
                {
                    raw = info.ContentId.Raw,
                    region = info.ContentId.Region,
                    titleId = info.ContentId.TitleId,
                    variant = info.ContentId.Variant,
                    name = info.ContentId.Name,
                },
                platform = h.Platform.ToString(),
                finalization = h.Finalization.ToString(),
                finalizationRaw = $"0x{h.RawFinalization:X4}",
                totalSize = h.TotalSize,
                dataOffset = h.DataOffset,
                dataSize = h.DataSize,
                metadata = new
                {
                    count = info.Metadata.Entries.Count,
                    drmType = info.Metadata.DrmType,
                    drmTypeName = info.Metadata.DrmType is uint d ? DrmType.Name(d) : null,
                    contentType = info.Metadata.ContentType?.ToString(),
                    contentTypeRaw = info.Metadata.ContentTypeRaw,
                    installDirectory = info.Metadata.InstallDirectory,
                },
                decrypted = info.IsDecrypted,
                decryptionNote = info.DecryptionNote,
                fileCount = info.IsDecrypted ? info.FileCount : (int?)null,
                directoryCount = info.IsDecrypted ? info.DirectoryCount : (int?)null,
                sfo = info.Sfo is null ? null : new
                {
                    title = info.Sfo.Title,
                    titleId = info.Sfo.TitleId,
                    category = info.Sfo.Category,
                    appVersion = info.Sfo.AppVersion,
                    version = info.Sfo.Version,
                    parentalLevel = info.Sfo.ParentalLevel,
                },
            });
            return;
        }

        var cid = info.ContentId;
        Console.WriteLine($"Content ID : {cid.Raw}");
        if (cid.Region is not null) Console.WriteLine($"  Region   : {cid.Region}");
        if (cid.TitleId is not null) Console.WriteLine($"  Title ID : {cid.TitleId}");
        if (cid.Variant is not null) Console.WriteLine($"  Variant  : {cid.Variant}");
        if (cid.Name is not null) Console.WriteLine($"  Name     : {cid.Name}");
        Console.WriteLine($"Platform   : {h.PlatformDisplay}");
        Console.WriteLine($"Finalized  : {h.Finalization} (0x{h.RawFinalization:X4})");
        Console.WriteLine($"Total size : {h.TotalSize:n0} bytes");
        Console.WriteLine($"Data       : offset 0x{h.DataOffset:X}, size {h.DataSize:n0} bytes");

        Console.WriteLine($"Metadata   : {info.Metadata.Entries.Count} entries");
        if (info.Metadata.DrmType is uint drm) Console.WriteLine($"  DRM type : {drm} ({DrmType.Name(drm)})");
        if (info.Metadata.ContentTypeRaw is uint ct)
            Console.WriteLine($"  Content  : {info.Metadata.ContentType?.ToString() ?? $"0x{ct:X}"}");
        if (info.Metadata.InstallDirectory is { Length: > 0 } dir)
            Console.WriteLine($"  InstallTo: {dir}");

        if (info.IsDecrypted)
            Console.WriteLine($"Entries    : {info.FileCount} files, {info.DirectoryCount} directories");
        else
            Console.WriteLine($"Entries    : unavailable — {info.DecryptionNote}");

        if (info.Sfo is not null)
        {
            Console.WriteLine("SFO:");
            if (info.Sfo.Title is not null) Console.WriteLine($"  TITLE    : {info.Sfo.Title}");
            if (info.Sfo.TitleId is not null) Console.WriteLine($"  TITLE_ID : {info.Sfo.TitleId}");
            if (info.Sfo.Category is not null) Console.WriteLine($"  CATEGORY : {info.Sfo.Category}");
            if (info.Sfo.AppVersion is not null) Console.WriteLine($"  APP_VER  : {info.Sfo.AppVersion}");
            if (info.Sfo.Version is not null) Console.WriteLine($"  VERSION  : {info.Sfo.Version}");
        }
    }

    public static void List(PkgInfo info, Options o)
    {
        if (o.Json)
        {
            WriteJson(info.Entries.Select(e => new
            {
                name = e.Name,
                kind = e.Kind.ToString(),
                isDirectory = e.IsDirectory,
                encrypted = e.IsEncrypted,
                psp = e.IsPsp,
                size = e.FileSize,
                offset = e.FileOffset,
            }));
            return;
        }

        // In a PS3 package a PSP-flagged entry's name was decrypted with the wrong key, so it may
        // look like garbage — annotate it so that isn't mistaken for corruption.
        bool ps3Host = info.Header.IsPs3;

        Console.WriteLine($"{"TYPE",-4} {"SIZE",14}  NAME");
        foreach (var e in info.Entries)
        {
            string type = e.IsDirectory ? "DIR" : "FILE";
            string size = e.IsDirectory ? "-" : e.FileSize.ToString("n0");
            string tag = e.IsPsp ? (ps3Host ? "  [PSP-encrypted — name not decodable with the PS3 key]" : "  [PSP]") : "";
            Console.WriteLine($"{type,-4} {size,14}  {e.Name}{tag}");
        }

        int psp = info.Entries.Count(e => e.IsPsp);
        Console.WriteLine($"\n{info.FileCount} files, {info.DirectoryCount} directories" +
                          (psp > 0 ? $", {psp} PSP-encrypted" : "") + ".");
    }

    public static void Verify(PkgVerificationReport report, Options o)
    {
        if (o.Json)
        {
            WriteJson(new
            {
                passed = report.Passed,
                failures = report.Failures,
                checks = report.Checks.Select(c => new { name = c.Name, status = c.Status.ToString(), detail = c.Detail }),
            });
            return;
        }

        foreach (var c in report.Checks)
        {
            string mark = c.Status switch
            {
                PkgCheckStatus.Pass => "ok  ",
                PkgCheckStatus.Fail => "FAIL",
                _ => "skip",
            };
            Console.WriteLine($"[{mark}] {c.Name,-16} {c.Detail}");
        }
        Console.WriteLine();
        Console.WriteLine(report.Passed
            ? "Integrity: OK"
            : $"Integrity: FAILED — {report.Failures} check(s) failed");
    }

    public static void Self(PkgLens.Core.Ps3.Self.SelfInfo self)
    {
        Console.WriteLine($"SELF       : {self.FileSize:n0} bytes");
        Console.WriteLine($"Program    : {self.ProgramTypeText}" + (self.IsNpdrm ? "  (NPDRM)" : ""));
        Console.WriteLine($"Key rev    : 0x{self.KeyRevision:X4}" + (self.IsLikelyFakeSigned ? "  (fake-signed / fSELF)" : ""));
        Console.WriteLine($"Version    : {self.VersionText}");
        Console.WriteLine($"Auth ID    : 0x{self.AuthId:X16}");
        Console.WriteLine($"Vendor ID  : 0x{self.VendorId:X8}");
        Console.WriteLine($"Metadata   : offset 0x{self.MetadataOffset:X}");
        Console.WriteLine($"ELF size   : {self.DataLength:n0} bytes (decrypted)");

        if (self.Elf is { } elf)
            Console.WriteLine($"ELF        : {(elf.Is64Bit ? "64-bit" : "32-bit")} " +
                              $"{(elf.IsBigEndian ? "big-endian" : "little-endian")}, {elf.TypeText}, machine 0x{elf.Machine:X}");

        if (self.ControlBlocks.Count > 0)
            Console.WriteLine($"Control    : {string.Join(", ", self.ControlBlocks.Select(b => b.TypeText))}");

        if (self.ControlFlags is { } cf)
            Console.WriteLine($"Ctrl flags : {Convert.ToHexString(cf)}");

        if (self.FirmwareVersionText is { } fw)
            Console.WriteLine($"FW version : {fw}");

        if (self.Segments.Count > 0)
        {
            Console.WriteLine($"Segments   : {self.Segments.Count}");
            foreach (var seg in self.Segments)
                Console.WriteLine(
                    $"  [{seg.Index}] offset 0x{seg.Offset:X}  size {seg.Size:n0}  {seg.CompressedText}, {seg.EncryptedText}");
        }

        if (self.Npdrm is { } npd)
        {
            Console.WriteLine("NPDRM:");
            Console.WriteLine($"  Content ID : {npd.ContentId}");
            Console.WriteLine($"  License    : {npd.LicenseText}");
            Console.WriteLine($"  App type   : 0x{npd.AppType:X}");
        }
    }

    public static void FolderInfo(GameFolderReport r, bool json)
    {
        if (json) { WriteJson(r); return; }

        Console.WriteLine($"Folder       : {r.Folder}");
        Console.WriteLine($"Content ID   : {r.ContentId ?? "(unknown)"}");
        Console.WriteLine($"Title        : {r.Title ?? "(none)"}");
        Console.WriteLine($"Title ID     : {r.TitleId ?? "(none)"}");
        if (r.AppVersion is not null) Console.WriteLine($"App version  : {r.AppVersion}");
        if (r.Category is not null)   Console.WriteLine($"Category     : {r.Category}");
        Console.WriteLine($"Content type : {r.ContentTypeGuess ?? "(unknown)"}");
        Console.WriteLine($"Contents     : {r.FileCount} file(s), {r.DirectoryCount} folder(s), {r.TotalBytes:n0} bytes");

        foreach (var e in r.Eboots)
        {
            string state = e.State switch
            {
                EbootState.EncryptedSigned => "encrypted / signed",
                EbootState.FakeSigned => "fake-signed (fSELF, CFW-ready)",
                EbootState.PlainElf => "plain ELF (decrypted)",
                _ => "unknown",
            };
            Console.WriteLine($"EBOOT        : {e.RelativePath}  [{state}]");
            if (e.State is EbootState.EncryptedSigned or EbootState.FakeSigned)
            {
                Console.WriteLine($"  key rev    : 0x{e.KeyRevision:X4}" + (e.Npdrm ? "  NPDRM" : ""));
                if (e.License is not null) Console.WriteLine($"  license    : {e.License}");
                if (e.ContentId is { Length: > 0 }) Console.WriteLine($"  content id : {e.ContentId}");
            }
        }

        if (r.Edats.Count > 0)
        {
            Console.WriteLine($"Data files   : {r.Edats.Count} EDAT/SDAT");
            foreach (var d in r.Edats)
                Console.WriteLine($"  {d.RelativePath}  [{(d.IsSdat ? "SDAT" : "EDAT")}, {d.License}{(d.NeedsRap ? ", needs RAP" : "")}]");
        }

        foreach (var note in r.Notes)
            Console.WriteLine($"  · {note}");
    }

    public static void Sfo(PkgInfo info, Options o)
    {
        var sfo = info.Sfo!;
        if (o.Json)
        {
            WriteJson(sfo.Entries.Select(e => new
            {
                key = e.Key,
                format = e.Format.ToString(),
                value = e.Value,
                intValue = e.IntValue,
            }));
            return;
        }

        int width = sfo.Entries.Count == 0 ? 0 : sfo.Entries.Max(e => e.Key.Length);
        foreach (var e in sfo.Entries)
            Console.WriteLine($"{e.Key.PadRight(width)} = {e.Value}");
    }
}
