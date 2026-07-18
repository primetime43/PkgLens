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
using PkgLens.Core.Shared.Sfo;
using PkgLens.Gui.Services;

namespace PkgLens.Gui.ViewModels;

/// <summary>
/// Wraps a parsed <see cref="PkgInfo"/> for display and keeps the underlying stream open so the
/// user can extract individual entries on demand. Dispose to release the file handle.
/// </summary>
public sealed partial class PackageViewModel : ObservableObject, IDisposable
{
    private readonly PackageOperationService _operations;
    private readonly PkgInfo _info;

    public PackageOperationService Operations => _operations;

    public string FilePath { get; }
    public string Title { get; }
    public string TitleId { get; }
    public string VersionText { get; }
    public string ContentIdRaw { get; }
    public string PlatformText { get; }
    public string RoleText { get; }
    public string RegionText { get; }
    public string CategoryText { get; }
    public string ContentTypeText { get; }
    public string FinalizationText { get; }
    public bool IsRetail { get; }

    public Bitmap? Icon { get; }

    /// <summary>True when an ICON0.PNG was decoded — drives the header-strip thumbnail's visibility.</summary>
    public bool HasIcon => Icon is not null;

    /// <summary>Single synthetic root shown at the top of the left navigation tree.</summary>
    public ObservableCollection<EntryNode> FolderRoots { get; }
    public EntryNode RootFolder { get; }

    public IReadOnlyList<MetadataRow> MetadataRows { get; }
    public IReadOnlyList<MetadataRow> ClassificationRows { get; }
    public IReadOnlyList<SfoRow> SfoRows { get; }

    public bool IsDecrypted => _info.IsDecrypted;
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

    public string ClassificationLine => string.Join(" · ", new[]
    {
        $"Platform: {PlatformText}",
        $"Role: {RoleText}",
        $"Region: {RegionText}",
        string.IsNullOrWhiteSpace(CategoryText) ? null : $"Category: {CategoryText}",
    }.Where(value => !string.IsNullOrEmpty(value)));

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

    public bool HasPendingChanges => _operations.HasPendingChanges;
    public int PendingChangeCount => _operations.PendingChangeCount;

    /// <summary>The parsed PARAM.SFO, if present.</summary>
    public SfoTable? Sfo => _operations.Sfo;
    public bool CanEditSfo => _operations.CanEditSfo;

    private void OnPendingChangesChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(PendingChangeCount));
    }
    private PackageViewModel(PackageOperationService operations)
    {
        _operations = operations;
        _operations.PendingChangesChanged += OnPendingChangesChanged;
        PkgInfo info = operations.Info;
        _info = info;
        FilePath = operations.FilePath;

        ContentIdRaw = info.ContentId.Raw;
        Title = info.Sfo?.Title ?? info.ContentId.Name ?? info.ContentId.Raw;
        TitleId = info.Sfo?.TitleId ?? info.ContentId.TitleId ?? "";
        VersionText = info.Sfo?.AppVersion ?? info.Sfo?.Version ?? "";
        PlatformText = info.Header.PlatformDisplay;
        RoleText = PackageLibraryMatcher.Classify(info.Metadata.ContentType, info.Sfo?.Category).ToString();
        RegionText = PackageLibraryMatcher.ResolveRegion(info.ContentId.Raw, TitleId);
        CategoryText = info.Sfo?.Category ?? string.Empty;
        ContentTypeText = info.Metadata.ContentType?.ToString() ??
            (info.Metadata.ContentTypeRaw is uint raw ? $"0x{raw:X}" : "Unknown");
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
        ClassificationRows =
        [
            new MetadataRow { Label = "Title", Value = Title },
            new MetadataRow { Label = "Title ID", Value = string.IsNullOrEmpty(TitleId) ? "Unknown" : TitleId },
            new MetadataRow { Label = "Platform", Value = PlatformText },
            new MetadataRow { Label = "Role", Value = RoleText },
            new MetadataRow { Label = "Region", Value = RegionText },
            new MetadataRow { Label = "Category", Value = string.IsNullOrEmpty(CategoryText) ? "Unknown" : CategoryText },
            new MetadataRow { Label = "Content type", Value = ContentTypeText },
            new MetadataRow { Label = "Version", Value = string.IsNullOrEmpty(VersionText) ? "Unknown" : VersionText },
            new MetadataRow { Label = "Content ID", Value = ContentIdRaw },
        ];
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

    /// <summary>Opens a package through the operation service and creates its presentation model.</summary>
    public static PackageViewModel Load(string path, IKeyProvider keys, CancellationToken cancellationToken = default)
    {
        PackageOperationService operations = PackageOperationService.Open(path, keys, cancellationToken);
        try
        {
            return new PackageViewModel(operations);
        }
        catch
        {
            operations.Dispose();
            throw;
        }
    }
    /// <summary>Navigates into a folder (from a double-click in the file list).</summary>
    public void OpenFolder(EntryNode folder)
    {
        if (folder.IsDirectory)
            SelectedFolder = folder;
    }

    private Bitmap? TryLoadIcon()
    {
        byte[]? png = _operations.TryReadEntryBytes("ICON0.PNG");
        if (png is not { Length: > 0 })
            return null;
        try
        {
            using var stream = new MemoryStream(png);
            return new Bitmap(stream);
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

    public void Dispose()
    {
        _operations.PendingChangesChanged -= OnPendingChangesChanged;
        _operations.Dispose();
    }
}
