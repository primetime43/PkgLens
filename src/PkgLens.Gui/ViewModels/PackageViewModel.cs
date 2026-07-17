using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PkgLens.Core;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Psp;
using PkgLens.Core.Shared.Sfo;
using PkgLens.Gui.Services;

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

    /// <summary>True when an ICON0.PNG was decoded — drives the header-strip thumbnail's visibility.</summary>
    public bool HasIcon => Icon is not null;

    /// <summary>Single synthetic root shown at the top of the left navigation tree.</summary>
    public ObservableCollection<EntryNode> FolderRoots { get; }
    public EntryNode RootFolder { get; }

    public IReadOnlyList<MetadataRow> MetadataRows { get; }
    public IReadOnlyList<SfoRow> SfoRows { get; }

    public bool IsDecrypted => _info.IsDecrypted;
    public PspExportEligibility PspExportEligibility => PspPackageExporter.CheckEligibility(_info);
    public bool CanExportPsp => PspExportEligibility.CanExport;
    public bool HasSfo => SfoRows.Count > 0;
    public string? DecryptionNote => _info.DecryptionNote;
    public bool ShowDecryptionWarning => !_info.IsDecrypted;
    public PackageRecommendationSet RecommendationSet { get; }
    public IReadOnlyList<PackageActionRecommendation> Recommendations => RecommendationSet.Actions;
    public string RecommendationHeading => $"Suggested for this {RecommendationSet.Classification.ToLowerInvariant()}";
    public string RecommendationSummary => RecommendationSet.Summary;
    public bool HasRecommendations => Recommendations.Count > 0;

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

    [ObservableProperty]
    private string _fileFilter = string.Empty;

    [ObservableProperty]
    private int _filterMatchCount;

    public bool HasFileFilter => !string.IsNullOrWhiteSpace(FileFilter);
    public string FilterSummary => HasFileFilter
        ? $"{FilterMatchCount} matching {Plural(FilterMatchCount, "file")}"
        : string.Empty;

    /// <summary>Contents (files + sub-folders) of the selected folder.</summary>
    public ObservableCollection<EntryNode> CurrentItems { get; } = new();

    /// <summary>The row selected in the right-hand file list.</summary>
    [ObservableProperty]
    private EntryNode? _selectedItem;

    partial void OnSelectedFolderChanged(EntryNode? value)
    {
        RefreshCurrentItems(value);
    }

    partial void OnFileFilterChanged(string value)
    {
        ApplyFileFilter();
        OnPropertyChanged(nameof(HasFileFilter));
        OnPropertyChanged(nameof(FilterSummary));
    }

    partial void OnFilterMatchCountChanged(int value) => OnPropertyChanged(nameof(FilterSummary));

    public void ClearFileFilter() => FileFilter = string.Empty;

    private void RefreshCurrentItems(EntryNode? folder)
    {
        CurrentItems.Clear();
        EntryNode selected = folder ?? RootFolder;
        IEnumerable<EntryNode> items = HasFileFilter
            ? DescendantFiles(selected).OrderBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase)
            : selected.Children;
        foreach (var child in items)
            CurrentItems.Add(child);
        SelectedItem = null;
    }

    private void ApplyFileFilter()
    {
        string filter = FileFilter.Trim();
        FolderRoots.Clear();

        if (filter.Length == 0)
        {
            FilterMatchCount = 0;
            RootFolder.IsExpanded = true;
            FolderRoots.Add(RootFolder);
            SelectedFolder = RootFolder;
            RefreshCurrentItems(RootFolder);
            return;
        }

        var matches = _info.Entries
            .Where(entry => entry.IsFile && entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        FilterMatchCount = matches.Count;

        var filteredRoot = new EntryNode
        {
            Name = RootFolder.Name,
            FullPath = string.Empty,
            IsDirectory = true,
            IsExpanded = true,
        };
        foreach (var node in EntryNode.BuildTree(matches))
        {
            ExpandDirectories(node);
            filteredRoot.Children.Add(node);
        }

        FolderRoots.Add(filteredRoot);
        SelectedFolder = filteredRoot;
        RefreshCurrentItems(filteredRoot);
    }

    private static IEnumerable<EntryNode> DescendantFiles(EntryNode node)
    {
        foreach (var child in node.Children)
        {
            if (child.IsDirectory)
            {
                foreach (var descendant in DescendantFiles(child))
                    yield return descendant;
            }
            else
            {
                yield return child;
            }
        }
    }

    private static void ExpandDirectories(EntryNode node)
    {
        if (!node.IsDirectory)
            return;
        node.IsExpanded = true;
        foreach (var child in node.Children)
            ExpandDirectories(child);
    }

    partial void OnSelectedItemChanged(EntryNode? value)
    {
        OnPropertyChanged(nameof(HasSelectedFile));
        OnPropertyChanged(nameof(SelectedFileDetail));
        OnPropertyChanged(nameof(SelectedIsPbp));
        OnPropertyChanged(nameof(SelectedIsDocument));
        OnPropertyChanged(nameof(HasSelectedPspTool));
    }

    /// <summary>True when the selected file has a PSP-specific action (manual decrypt or PBP unpack).</summary>
    public bool HasSelectedPspTool => SelectedIsPbp || SelectedIsDocument;

    public bool HasSelectedFile => SelectedItem is { IsDirectory: false, Entry: not null };

    /// <summary>True when the selected file is a PSP PBP container (EBOOT.PBP), unpackable in-app.</summary>
    public bool SelectedIsPbp => SelectedItem is { IsDirectory: false, Entry: not null } n
        && n.Name.EndsWith(".PBP", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the selected file is a PSP/minis DOCUMENT.DAT (a decryptable manual).</summary>
    public bool SelectedIsDocument => SelectedItem is { IsDirectory: false, Entry: not null } n
        && n.Name.Equals("DOCUMENT.DAT", StringComparison.OrdinalIgnoreCase);

    /// <summary>One-line detail for the selected file (size, offset, encryption, PSP flag), for the footer.</summary>
    public string SelectedFileDetail
    {
        get
        {
            if (SelectedItem is not { IsDirectory: false, Entry: { } e })
                return "";
            var parts = new List<string>
            {
                $"{e.FileSize:n0} bytes",
                $"offset 0x{e.FileOffset:X}",
                e.IsEncrypted ? "encrypted" : "stored",
            };
            if (e.IsPsp) parts.Add("PSP-encrypted");
            return $"{SelectedItem.Name}   {string.Join("  ·  ", parts)}";
        }
    }

    /// <summary>Files larger than this are not loaded into memory for preview (use Extract instead).</summary>
    public const long MaxPreviewBytes = 32L * 1024 * 1024;

    public bool CanPreviewSelected =>
        SelectedItem is { IsDirectory: false, Entry: not null } n && n.Size <= MaxPreviewBytes;

    /// <summary>Runs the integrity checks against this package.</summary>
    public PkgVerificationReport Verify() => PkgVerifier.Verify(_stream, _keys);

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

    /// <summary>The parsed PARAM.SFO, if present.</summary>
    public SfoTable? Sfo => _info.Sfo;

    private PkgEntry? SfoEntry => _info.Entries.FirstOrDefault(e =>
        e.IsFile && (e.Name.Equals("PARAM.SFO", StringComparison.OrdinalIgnoreCase) ||
                     e.Name.EndsWith("/PARAM.SFO", StringComparison.OrdinalIgnoreCase)));

    public bool CanEditSfo => _info.Sfo is not null && SfoEntry is not null;

    /// <summary>Serializes the edited SFO and queues it as the PARAM.SFO replacement for the next save.</summary>
    public void ApplySfoEdits(IReadOnlyList<SfoEntry> edited)
    {
        if (SfoEntry is not { } entry)
            throw new InvalidOperationException("This package has no PARAM.SFO to edit.");
        _replacements[entry] = SfoWriter.Write(edited);
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(PendingChangeCount));
    }

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
    public void SaveAs(string destinationPath, CancellationToken cancellationToken = default,
        IProgress<PkgOperationProgress>? progress = null)
    {
        AtomicOutput.EnsureDifferentPath(FilePath, destinationPath);
        AtomicOutput.Write(destinationPath,
            dest => PkgWriter.Repack(_stream, _info, _replacements, _keys, dest,
                cancellationToken, progress));
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
        PlatformText = info.Header.PlatformDisplay;
        FinalizationText = info.Header.Finalization.ToString();
        IsRetail = info.Header.IsRetail;
        RecommendationSet = PackageRecommendationEngine.Analyze(info);

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
    public static PackageViewModel Load(string path, IKeyProvider keys, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stream = File.OpenRead(path);
        try
        {
            var info = PkgReader.Read(stream, keys);
            cancellationToken.ThrowIfCancellationRequested();
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

    /// <summary>Extracts every file to <paramref name="directory"/>, rebuilding the tree. Returns the file count.</summary>
    public int ExtractAllTo(string directory, CancellationToken cancellationToken = default,
        IProgress<PkgOperationProgress>? progress = null) =>
        PkgReader.ExtractAll(_stream, _info, directory, _keys,
            cancellationToken: cancellationToken, progress: progress);

    /// <summary>Streams the currently selected entry's decrypted data to <paramref name="destinationPath"/>.</summary>
    public void ExtractSelectedTo(string destinationPath, CancellationToken cancellationToken = default,
        IProgress<long>? progress = null)
    {
        if (SelectedItem?.Entry is not { } entry)
            throw new InvalidOperationException("No extractable file is selected.");

        AtomicOutput.EnsureDifferentPath(FilePath, destinationPath);
        AtomicOutput.Write(destinationPath,
            dest => PkgReader.ExtractEntry(_stream, _header, entry, dest, _keys,
                cancellationToken, progress));
    }

    /// <summary>Decrypts the selected DOCUMENT.DAT into its manual pages (each a PNG), using the sibling DOCINFO.EDAT.</summary>
    public IReadOnlyList<byte[]> DecryptSelectedDocument()
    {
        if (SelectedItem is not { IsDirectory: false, Entry: { } entry })
            throw new InvalidOperationException("Select a DOCUMENT.DAT file.");

        byte[] doc = PkgReader.ExtractEntryBytes(_stream, _header, entry, _keys);
        byte[]? docInfo = ReadSiblingBytes("DOCINFO.EDAT");
        return PspDocument.DecryptPages(doc, docInfo);
    }

    /// <summary>Extracts and splits the selected PBP into its parts under <paramref name="destinationDir"/>.</summary>
    public IReadOnlyList<string> UnpackSelectedPbpTo(string destinationDir,
        CancellationToken cancellationToken = default)
    {
        if (SelectedItem is not { IsDirectory: false, Name: { } leaf, Entry: { } entry })
            throw new InvalidOperationException("Select a .PBP file.");

        Directory.CreateDirectory(destinationDir);
        string temp = Path.Combine(destinationDir, $".pkglens-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var d = File.Create(temp))
                PkgReader.ExtractEntry(_stream, _header, entry, d, _keys, cancellationToken);

            var written = new List<string>();
            using (var src = File.OpenRead(temp))
            {
                var pbp = PbpArchive.Parse(src);
                foreach (var e in pbp.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AtomicOutput.Write(Path.Combine(destinationDir, e.Name),
                        dst => PbpArchive.Extract(src, e, dst));
                    written.Add(e.Name);
                }
            }
            return written;
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>
    /// Extracts the selected EBOOT.PBP, unpacks its DATA.PSAR (an NPUMDIMG), and decrypts it to a PSP
    /// ISO at <paramref name="isoPath"/> — no RAP/license needed. Throws if there's no NPUMDIMG inside.
    /// </summary>
    public void ExtractSelectedPspIsoTo(string isoPath, CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        if (SelectedItem is not { IsDirectory: false, Name: { } leaf, Entry: { } entry })
            throw new InvalidOperationException("Select an EBOOT.PBP file.");

        string dir = Path.GetDirectoryName(isoPath) ?? ".";
        string pbpTmp = Path.Combine(dir, $".pkglens-pbp-{Guid.NewGuid():N}.tmp");
        string psarTmp = Path.Combine(dir, $".pkglens-psar-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var d = File.Create(pbpTmp))
                PkgReader.ExtractEntry(_stream, _header, entry, d, _keys, cancellationToken);

            using (var src = File.OpenRead(pbpTmp))
            {
                var pbp = PbpArchive.Parse(src);
                var psar = pbp.Entries.FirstOrDefault(e => e.Name.Equals("DATA.PSAR", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("This PBP has no DATA.PSAR.");
                using var dst = File.Create(psarTmp);
                PbpArchive.Extract(src, psar, dst);
            }

            using var psarStream = File.OpenRead(psarTmp);
            var head = new byte[0x100];
            psarStream.ReadExactly(head, 0, head.Length);
            if (!NpumdImg.IsNpumdImg(head))
                throw new InvalidOperationException("DATA.PSAR is not an NPUMDIMG (this game isn't a UMD/minis image).");
            psarStream.Position = 0;

            AtomicOutput.Write(isoPath, iso => NpumdImg.DecryptToIso(psarStream, iso,
                cancellationToken, progress));
        }
        finally
        {
            try { File.Delete(pbpTmp); } catch { /* best-effort */ }
            try { File.Delete(psarTmp); } catch { /* best-effort */ }
        }
    }

    /// <summary>Reads a decrypted sibling entry (same folder as the selection) by leaf name, or null if absent.</summary>
    private byte[]? ReadSiblingBytes(string leafName)
    {
        if (SelectedItem is not { FullPath: { } full })
            return null;
        int slash = full.LastIndexOf('/');
        string siblingPath = slash < 0 ? leafName : full[..(slash + 1)] + leafName;
        var entry = _info.Entries.FirstOrDefault(e =>
            e.IsFile && e.Name.Equals(siblingPath, StringComparison.OrdinalIgnoreCase));
        return entry is null ? null : PkgReader.ExtractEntryBytes(_stream, _header, entry, _keys);
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
            // Render each entry by what it actually is — several fields are u64, a 24-byte digest, or
            // a decoded version, not a plain 4-byte int.
            string value = e.Id switch
            {
                PkgMetadataId.ContentType when metadata.ContentType is { } ct => $"{(uint)ct} ({ct})",
                PkgMetadataId.DrmType when e.AsUInt32() is uint drm => $"{drm} ({DrmType.Name(drm)})",
                PkgMetadataId.SoftwareRevision when metadata.SoftwareRevisionText is { } sr => sr,
                PkgMetadataId.QaDigest => e.ToHex(),
                _ => e.AsUInt32() is uint u ? $"{u} (0x{u:X})"
                   : e.AsUInt64() is ulong u64 ? $"{u64} (0x{u64:X})"
                   : e.Data.Length <= 32 ? e.ToHex() : $"{e.Data.Length} bytes",
            };

            rows.Add(new MetadataRow { Label = e.Label, Value = value });
        }
        return rows;
    }

    private static string Plural(int n, string word) => n == 1 ? word : word + "s";

    public void Dispose() => _stream.Dispose();
}
