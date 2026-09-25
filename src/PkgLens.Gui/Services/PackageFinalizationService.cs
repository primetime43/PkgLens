using System;
using System.IO;
using System.Threading;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;

namespace PkgLens.Gui.Services;

internal static class PackageFinalizationService
{
    internal static PkgFinalizationReport FinalizeFile(string sourcePath, string destinationPath,
        CancellationToken token = default, IProgress<PkgOperationProgress>? progress = null)
    {
        AtomicOutput.EnsureDifferentPath(sourcePath, destinationPath);
        // Finalized output always uses the standard retail key, even when a custom input key is configured.
        var keys = new InMemoryKeyProvider(BundledKeys.Ps3GpkgAesKey);
        using var source = File.OpenRead(sourcePath);
        PkgFinalizationReport? report = null;
        AtomicOutput.Write(destinationPath,
            output => report = PkgFinalizer.Convert(source, output, keys, token, progress),
            validate: temporary =>
            {
                using var output = File.OpenRead(temporary);
                PkgFinalizer.Verify(output, keys, report!, token, progress);
            });
        return report!;
    }
}
