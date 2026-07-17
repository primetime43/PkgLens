using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    private async Task<int> OfferNearbyRapsForPathAsync(string sourcePath)
    {
        string? rapDirectory = Vm.RapDirectory;
        string? keysDirectory = Vm.KeysDirectory;
        string[] searchDirectories = Vm.RapSearchDirectories.ToArray();
        IReadOnlyList<RapDiscoveryCandidate>? candidates = null;

        await RunOperationAsync("Searching for nearby RAP licenses…", "Nearby RAP search failed", async (token, _) =>
        {
            candidates = await Task.Run(() =>
            {
                KeyLicenseAuditReport audit = KeyLicenseAudit.Inspect(sourcePath,
                    new FileKeyProvider(keysDirectory),
                    new KeyLicenseAuditOptions { RapDirectory = rapDirectory }, token);
                string[] missingContentIds = audit.Items
                    .Where(item => item.RapStatus == KeyLicenseAuditStatus.Missing &&
                                   !string.IsNullOrWhiteSpace(item.ContentId))
                    .Select(item => item.ContentId!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return RapDiscovery.Find(missingContentIds, sourcePath, searchDirectories);
            }, token);
        });

        return candidates is null
            ? 0
            : await OfferNearbyRapImportAsync(candidates, rapDirectory);
    }

    private async Task<int> OfferNearbyRapsAsync(string sourcePath, IEnumerable<string> contentIds)
    {
        string? rapDirectory = Vm.RapDirectory;
        string[] missingContentIds = contentIds
            .Where(contentId => !string.IsNullOrWhiteSpace(contentId) &&
                                RapStore.Find(contentId, rapDirectory) is null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingContentIds.Length == 0)
            return 0;

        string[] searchDirectories = Vm.RapSearchDirectories.ToArray();
        IReadOnlyList<RapDiscoveryCandidate>? candidates = null;
        await RunOperationAsync("Searching for nearby RAP licenses…", "Nearby RAP search failed", async (token, _) =>
        {
            candidates = await Task.Run(() =>
                RapDiscovery.Find(missingContentIds, sourcePath, searchDirectories), token);
        });
        return candidates is null
            ? 0
            : await OfferNearbyRapImportAsync(candidates, rapDirectory);
    }

    private async Task<int> OfferNearbyRapImportAsync(
        IReadOnlyList<RapDiscoveryCandidate> candidates, string? rapDirectory)
    {
        if (candidates.Count == 0)
            return 0;

        bool import = await new NearbyRapImportDialog(candidates, RapStore.DirectoryPath(rapDirectory))
            .ShowDialog<bool>(this);
        if (!import)
            return 0;

        RapDiscoveryCandidate[] selected = candidates.Where(candidate => candidate.IsValid)
            .GroupBy(candidate => candidate.ContentId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        int imported = 0;
        await RunOperationAsync("Importing nearby RAP licenses…", "RAP import failed", async (token, _) =>
        {
            imported = await Task.Run(() =>
            {
                int count = 0;
                foreach (RapDiscoveryCandidate candidate in selected)
                {
                    token.ThrowIfCancellationRequested();
                    byte[] rap = File.ReadAllBytes(candidate.Path);
                    RapStore.Install(candidate.ContentId, rap, rapDirectory);
                    count++;
                }
                return count;
            }, token);
            Vm.RefreshRapStatus();
            Vm.Status = $"Imported {imported} nearby RAP license(s).";
        });
        return imported;
    }
}
