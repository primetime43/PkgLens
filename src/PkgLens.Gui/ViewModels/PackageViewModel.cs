using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PkgLens.Core;
using PkgLens.Core.Keys;
using PkgLens.Core.Models;

namespace PkgLens.Gui.ViewModels;

/// <summary>
/// Wraps a parsed <see cref="PkgInfo"/> for display and keeps the underlying stream open so the
/// user can extract individual entries on demand. Dispose to release the file handle.
/// </summary>
public sealed partial class PackageViewModel : ObservableObject, IDisposable
{
    private readonly Stream _stream;
    private readonly PkgHeader _header;
    private readonly IKeyProvider _keys;
    private readonly PkgInfo _info;

    public string FilePath { get; }
    public string Title { get; }
    public string TitleId { get; }
    public string VersionText { get; }
    public string ContentIdRaw { get; }
    public string PlatformText { get; }
    public string FinalizationText { get; }
    public bool IsRetail { get; }

    public Bitmap? Icon { get; }

    /// <summary>Single synthetic root shown at the top of the left navigation tree.</summary>
    public ObservableCollection<EntryNode> FolderRoots { get; }
    public EntryNode RootFolder { get; }

    public IReadOnlyList<MetadataRow> MetadataRows { get; }
    public IReadOnlyList<SfoRow> SfoRows { get; }

    public bool IsDecrypted => _info.IsDecrypted;
    public bool HasSfo => SfoRows.Count > 0;
    public string? DecryptionNote => _info.DecryptionNote;
    public bool ShowDecryptionWarning => !_info.IsDecrypted;

    public int FileCount => _info.FileCount;
    public int DirectoryCount => _info.DirectoryCount;

    /// <summary>"Ps3 · Retail · 01.40" style one-liner for the info dialog.</summary>
    public string SummaryLine =>
        string.Join(" · ", new[] { PlatformText, FinalizationText, VersionText }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>Status-bar text mirroring the classic PkgView ("N files and M folders").</summary>
    public string StatusCounts =>
        $"{FileCount} {Plural(FileCount, "file")} and {DirectoryCount} {Plural(DirectoryCount, "folder")}";

    /// <summary>The folder selected in the left tree; drives the file list on the right.</summary>
    [ObservableProperty]
    private EntryNode? _selectedFolder;

    /// <summary>Contents (files + sub-folders) of the selected folder.</summary>
    public ObservableCollection<EntryNode> CurrentItems { get; } = new();

    /// <summary>The row selected in the right-hand file list.</summary>
    [ObservableProperty]
    private EntryNode? _selectedItem;

    partial void OnSelectedFolderChanged(EntryNode? value)
    {
        CurrentItems.Clear();
        foreach (var child in (value ?? RootFolder).Children)
            CurrentItems.Add(child);
        SelectedItem = null;
    }

    partial void OnSelectedItemChanged(EntryNode? value) =>
        OnPropertyChanged(nameof(HasSelectedFile));

    public bool HasSelectedFile => SelectedItem is { IsDirectory: false, Entry: not null };

    /// <summary>Files larger than this are not loaded into memory for preview (use Extract instead).</summary>
    public const long MaxPreviewBytes = 32L * 1024 * 1024;

    public bool CanPreviewSelected =>
        SelectedItem is { IsDirectory: false, Entry: not null } n && n.Size <= MaxPreviewBytes;

    /// <summary>Reads the selected file's decrypted bytes into memory (for the viewer).</summary>
    public byte[] ReadSelectedBytes()
    {
        if (SelectedItem is not { IsDirectory: false, Entry: { } entry })
            throw new InvalidOperationException("No file is selected.");
        return PkgReader.ExtractEntryBytes(_stream, _header, entry, _keys);
    }

    // --- Modify / repack ----------------------------------------------------------------------

    private readonly Dictionary<PkgEntry, byte[]> _replacements = new();

    public bool HasPendingChanges => _replacements.Count > 0;
    public int PendingChangeCount => _replacements.Count;

    /// <summary>Queues new content to replace the selected file when the package is next saved.</summary>
    public void ReplaceSelected(byte[] content)
    {
        if (SelectedItem is not { IsDirectory: false, Entry: { } entry })
            throw new InvalidOperationException("Select a file to replace.");
        _replacements[entry] = content;
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(PendingChangeCount));
    }

    /// <summary>
    /// Writes a repacked copy of the package to <paramref name="destinationPath"/>, applying any
    /// queued replacements. The signature is not recomputed (retail output is unsigned).
    /// </summary>
    public void SaveAs(string destinationPath)
    {
        using var dest = File.Create(destinationPath);
        PkgWriter.Repack(_stream, _info, _replacements, _keys, dest);
    }

    private PackageViewModel(string path, Stream stream, PkgInfo info, IKeyProvider keys)
    {
        FilePath = path;
        _stream = stream;
        _info = info;
        _keys = keys;
        _header = info.Header;

        ContentIdRaw = info.ContentId.Raw;
        Title = info.Sfo?.Title ?? info.ContentId.Name ?? info.ContentId.Raw;
        TitleId = info.Sfo?.TitleId ?? info.ContentId.TitleId ?? "";
        VersionText = info.Sfo?.AppVersion ?? info.Sfo?.Version ?? "";
        PlatformText = info.Header.Platform.ToString();
        FinalizationText = info.Header.Finalization.ToString();
        IsRetail = info.Header.IsRetail;

        // Wrap the parsed tree under a single root node labelled like the classic PkgView's top
        // node (e.g. "NPUB30468") — the package's install directory / title-id, which is the
        // folder it installs into on the PS3. Prefer the authoritative install directory from
        // metadata (entry 0x0A) when present, else fall back to the title-id from the content id.
        string rootLabel =
            info.Metadata.InstallDirectory is { Length: > 0 } installDir ? installDir :
            info.ContentId.TitleId ?? info.ContentId.Name ?? "package";
        RootFolder = new EntryNode { Name = rootLabel, FullPath = "", IsDirectory = true, IsExpanded = true };
        foreach (var node in EntryNode.BuildTree(info.Entries))
            RootFolder.Children.Add(node);
        FolderRoots = new ObservableCollection<EntryNode> { RootFolder };

        MetadataRows = BuildMetadataRows(info.Metadata);
        SfoRows = info.Sfo is null
            ? Array.Empty<SfoRow>()
            : info.Sfo.Entries.Select(e => new SfoRow
            {
                Key = e.Key,
                Format = e.Format.ToString(),
                Value = e.Value,
            }).ToList();

        Icon = info.IsDecrypted ? TryLoadIcon() : null;

        SelectedFolder = RootFolder; // show the root's contents initially
    }

    /// <summary>Opens and parses a package, keeping the stream open for on-demand extraction.</summary>
    public static PackageViewModel Load(string path, IKeyProvider keys)
    {
        var stream = File.OpenRead(path);
        try
        {
            var info = PkgReader.Read(stream, keys);
            return new PackageViewModel(path, stream, info, keys);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Navigates into a folder (from a double-click in the file list).</summary>
    public void OpenFolder(EntryNode folder)
    {
        if (folder.IsDirectory)
            SelectedFolder = folder;
    }

    /// <summary>Streams the currently selected entry's decrypted data to <paramref name="destinationPath"/>.</summary>
    public void ExtractSelectedTo(string destinationPath)
    {
        if (SelectedItem?.Entry is not { } entry)
            throw new InvalidOperationException("No extractable file is selected.");

        using var dest = File.Create(destinationPath);
        PkgReader.ExtractEntry(_stream, _header, entry, dest, _keys);
    }

    private Bitmap? TryLoadIcon()
    {
        var iconEntry = _info.Entries.FirstOrDefault(e =>
            e.IsFile && e.Name.Equals("ICON0.PNG", StringComparison.OrdinalIgnoreCase));
        if (iconEntry is null)
            return null;

        try
        {
            byte[] png = PkgReader.ExtractEntryBytes(_stream, _header, iconEntry, _keys);
            if (png.Length == 0) return null;
            using var ms = new MemoryStream(png);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    private static List<MetadataRow> BuildMetadataRows(PkgMetadata metadata)
    {
        var rows = new List<MetadataRow>();
        foreach (var e in metadata.Entries)
        {
            string value = e.AsUInt32() is uint u
                ? $"{u} (0x{u:X})"
                : e.Data.Length <= 32 ? e.ToHex() : $"{e.Data.Length} bytes";

            if (e.Id == PkgMetadataId.ContentType && metadata.ContentType is { } ct)
                value = $"{(uint)ct} ({ct})";

            rows.Add(new MetadataRow { Label = e.Label, Value = value });
        }
        return rows;
    }

    private static string Plural(int n, string word) => n == 1 ? word : word + "s";

    public void Dispose() => _stream.Dispose();
}
