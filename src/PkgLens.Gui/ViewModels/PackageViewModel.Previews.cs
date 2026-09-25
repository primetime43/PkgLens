using System;
using System.Linq;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Gui.ViewModels;

public sealed partial class PackageViewModel
{
    public string? MetadataPreviewError { get; private set; }
    public bool HasMetadataPreviewError => MetadataPreviewError is not null;

    private void RefreshEntryPreviews(EntryNode root)
    {
        foreach (var node in DescendantFiles(root))
        {
            if (node.Entry is { } entry)
                node.UpdatePreviewState(_operations.GetEntrySize(entry), _operations.IsReplaced(entry));
        }
    }

    private void RefreshIconPreview()
    {
        var oldIcon = Icon;
        Icon = IsDecrypted ? TryLoadIcon() : null;
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(HasIcon));
        oldIcon?.Dispose();
    }

    private void RefreshMetadataPreview()
    {
        MetadataPreviewError = null;
        try
        {
            Sfo = _operations.Sfo;
        }
        catch (Exception ex)
        {
            // An invalid replacement must not leave the old SFO masquerading as the new one.
            Sfo = null;
            MetadataPreviewError = $"Cannot preview the replacement PARAM.SFO: {ex.Message} Revert or replace this file to restore its details.";
        }

        Title = Sfo?.Title ?? _info.ContentId.Name ?? _info.ContentId.Raw;
        TitleId = Sfo?.TitleId ?? _info.ContentId.TitleId ?? "";
        VersionText = Sfo?.AppVersion ?? Sfo?.Version ?? "";
        CategoryText = Sfo?.Category ?? "";
        RoleText = PackageLibraryMatcher.Classify(_info.Metadata.ContentType, Sfo?.Category).ToString();
        RegionText = PackageLibraryMatcher.ResolveRegion(_info.ContentId.Raw, TitleId);
        SfoRows = Sfo?.Entries.Select(entry => new SfoRow
        {
            Key = entry.Key, Format = entry.Format.ToString(), Value = entry.Value,
        }).ToArray() ?? Array.Empty<SfoRow>();
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

        // Use a presentation snapshot; the source's offsets and parsed metadata stay intact for saving.
        RecommendationSet = PackageRecommendationEngine.Analyze(new PkgInfo
        {
            Header = _info.Header, Metadata = _info.Metadata, Entries = _info.Entries,
            Sfo = Sfo, IsDecrypted = _info.IsDecrypted, DecryptionNote = _info.DecryptionNote,
        });
        foreach (string property in new[]
        {
            nameof(Sfo), nameof(SfoRows), nameof(HasSfo), nameof(CanEditSfo), nameof(Title), nameof(TitleId),
            nameof(VersionText), nameof(CategoryText), nameof(RoleText), nameof(RegionText),
            nameof(SummaryLine), nameof(ClassificationLine), nameof(ClassificationRows),
            nameof(RecommendationSet), nameof(Recommendations), nameof(RecommendationHeading),
            nameof(RecommendationSummary), nameof(HasRecommendations),
            nameof(MetadataPreviewError), nameof(HasMetadataPreviewError),
        })
            OnPropertyChanged(property);
    }
}
