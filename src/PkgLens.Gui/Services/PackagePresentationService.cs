using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Platform.Storage;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Shared;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Services;

internal static class PackagePresentationService
{
    private static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    public static GuiOperationProgress ToGuiProgress(PkgOperationProgress progress) =>
        new(progress.Item is null ? null : $"Processing {progress.Item}…", progress.Percent);

    public static string DescribeFolder(GameFolderReport report)
    {
        var text = new StringBuilder();
        text.AppendLine($"Content ID   : {report.ContentId ?? "(unknown)"}");
        text.AppendLine($"Title        : {report.Title ?? "(none)"}");
        text.AppendLine($"Title ID     : {report.TitleId ?? "(none)"}");
        if (report.AppVersion is not null) text.AppendLine($"App version  : {report.AppVersion}");
        if (report.Category is not null) text.AppendLine($"Category     : {report.Category}");
        text.AppendLine($"Content type : {report.ContentTypeGuess ?? "(unknown)"}");
        text.AppendLine($"Contents     : {report.FileCount} file(s), {report.DirectoryCount} folder(s), {report.TotalBytes:n0} bytes");
        foreach (var eboot in report.Eboots)
        {
            string state = eboot.State switch
            {
                EbootState.EncryptedSigned => "encrypted / signed",
                EbootState.FakeSigned => "fake-signed (fSELF, CFW-ready)",
                EbootState.PlainElf => "plain ELF",
                _ => "unknown",
            };
            text.AppendLine($"EBOOT        : {eboot.RelativePath}  [{state}]");
            if (eboot.License is not null)
                text.AppendLine($"  license    : {eboot.License}" + (eboot.Npdrm ? "  (NPDRM)" : ""));
        }
        if (report.Edats.Count > 0)
        {
            text.AppendLine($"Data files   : {report.Edats.Count} EDAT/SDAT");
            foreach (var data in report.Edats)
                text.AppendLine($"  {data.RelativePath}  [{(data.IsSdat ? "SDAT" : "EDAT")}, {data.License}{(data.NeedsRap ? ", needs RAP" : "")}]");
        }
        return text.ToString().TrimEnd();
    }

    public static string DescribeSelf(SelfInfo self)
    {
        var text = new StringBuilder();
        var target = SelfTargetInfo.FromInfo(self);
        text.AppendLine($"CEX / DEX  : {target.Label}");
        text.AppendLine(target.Detail);
        text.AppendLine();
        text.AppendLine($"Program    : {self.ProgramTypeText}" + (self.IsNpdrm ? "  (NPDRM)" : ""));
        text.AppendLine($"Key rev    : 0x{self.KeyRevision:X4}" + (self.IsLikelyFakeSigned ? "  (fake-signed / fSELF)" : ""));
        text.AppendLine($"Auth ID    : 0x{self.AuthId:X16}");
        text.AppendLine($"Vendor ID  : 0x{self.VendorId:X8}");
        text.AppendLine($"ELF size   : {self.DataLength:n0} bytes (decrypted)");
        if (self.Elf is { } elf)
            text.AppendLine($"ELF        : {(elf.Is64Bit ? "64-bit" : "32-bit")} {(elf.IsBigEndian ? "big-endian" : "little-endian")}, {elf.TypeText}");
        if (self.ControlBlocks.Count > 0)
            text.AppendLine($"Control    : {string.Join(", ", self.ControlBlocks.Select(block => block.TypeText))}");
        if (self.ControlFlags is { } controlFlags)
            text.AppendLine($"Ctrl flags : {Convert.ToHexString(controlFlags)}");
        if (self.FirmwareVersionText is { } firmware)
            text.AppendLine($"FW version : {firmware}");
        if (self.Segments.Count > 0)
        {
            text.AppendLine($"Segments   : {self.Segments.Count}");
            foreach (var segment in self.Segments)
                text.AppendLine($"  [{segment.Index}] offset 0x{segment.Offset:X}  size {segment.Size:n0}  {segment.CompressedText}, {segment.EncryptedText}");
        }
        if (self.Npdrm is { } npdrm)
        {
            text.AppendLine($"Content ID : {npdrm.ContentId}");
            text.AppendLine($"License    : {npdrm.LicenseText}");
        }
        return text.ToString().TrimEnd();
    }

    public static string SanitizeFileName(string? name)
    {
        name ??= string.Empty;
        foreach (char character in Path.GetInvalidFileNameChars())
            name = name.Replace(character, '_');
        return string.IsNullOrEmpty(name) ? "package" : name;
    }

    public static IReadOnlyList<FilePickerFileType> ReplacementFilters(string name)
    {
        string extension = Path.GetExtension(name).ToLowerInvariant();
        var filters = new List<FilePickerFileType>();
        if (ImageExtensions.Contains(extension))
        {
            filters.Add(new FilePickerFileType($"{extension.TrimStart('.').ToUpperInvariant()} image")
                { Patterns = new[] { "*" + extension } });
            filters.Add(new FilePickerFileType("Images")
                { Patterns = ImageExtensions.Select(item => "*" + item).ToArray() });
        }
        else if (!string.IsNullOrEmpty(extension))
        {
            filters.Add(new FilePickerFileType($"{extension.TrimStart('.').ToUpperInvariant()} files")
                { Patterns = new[] { "*" + extension } });
        }
        filters.Add(FilePickerFileTypes.All);
        return filters;
    }
}
