using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Psp;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Shared;

public enum BatchOperation
{
    Audit,
    Classify,
    Verify,
    Extract,
    ExportPbp,
    ExportIso,
    ExportCso,
    ConvertCfw,
}

public enum BatchJobStatus
{
    Pending,
    Running,
    Completed,
    Skipped,
    Failed,
}

public sealed class BatchJob
{
    public required string SourcePath { get; init; }
    public required string RelativePath { get; init; }
    public required string OutputPath { get; init; }
    public required PackageScanRow Package { get; init; }
    public BatchJobStatus Status { get; set; }
    public string? Message { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
}

public sealed class BatchManifest
{
    public int FormatVersion { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string SourceDirectory { get; init; }
    public required string OutputDirectory { get; init; }
    public required BatchOperation Operation { get; init; }
    public bool Recursive { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<BatchJob> Jobs { get; init; } = new();

    [JsonIgnore]
    public string ManifestPath => BatchProcessor.ManifestPath(OutputDirectory, Operation);

    [JsonIgnore]
    public int CompletedCount => Jobs.Count(job => job.Status is BatchJobStatus.Completed or BatchJobStatus.Skipped);

    [JsonIgnore]
    public int FailedCount => Jobs.Count(job => job.Status == BatchJobStatus.Failed);
}

public sealed record BatchDiscoveryProgress(int Completed, int Total, string Source);

public sealed record BatchRunProgress(
    int Completed, int Total, string Source, BatchJobStatus Status, string Message,
    double? ItemPercent = null);

public sealed class BatchProcessorOptions
{
    public string? RapDirectory { get; init; }
}

/// <summary>
/// Persistent sequential batch runner for package audit, classification, verification, extraction,
/// PSP export, and CFW conversion. The manifest is saved after every job-state transition so an
/// interrupted run can resume without repeating completed work.
/// </summary>
public static class BatchProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static BatchManifest Create(
        string sourceDirectory,
        string outputDirectory,
        BatchOperation operation,
        bool recursive,
        IKeyProvider keys,
        CancellationToken cancellationToken = default,
        IProgress<BatchDiscoveryProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(keys);

        string sourceRoot = Path.GetFullPath(sourceDirectory);
        string outputRoot = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Batch source folder was not found: {sourceRoot}");
        if (PathComparer.Equals(sourceRoot.TrimEnd(Path.DirectorySeparatorChar),
                outputRoot.TrimEnd(Path.DirectorySeparatorChar)))
            throw new ArgumentException("The batch output folder must be different from the source folder.",
                nameof(outputDirectory));

        SearchOption search = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        string[] files = Directory.EnumerateFiles(sourceRoot, "*", search)
            .Where(path => Path.GetExtension(path).Equals(".pkg", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsWithin(path, outputRoot))
            .OrderBy(path => path, PathComparer)
            .ToArray();

        var manifest = new BatchManifest
        {
            SourceDirectory = sourceRoot,
            OutputDirectory = outputRoot,
            Operation = operation,
            Recursive = recursive,
        };

        for (int index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sourcePath = files[index];
            string relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            PackageScanRow package = PackageScanner.Inspect(sourcePath, keys);
            manifest.Jobs.Add(new BatchJob
            {
                SourcePath = sourcePath,
                RelativePath = relativePath,
                OutputPath = BuildOutputPath(outputRoot, relativePath, operation),
                Package = package,
                Status = BatchJobStatus.Pending,
                Message = package.Failed ? package.Note : "Ready",
            });
            progress?.Report(new BatchDiscoveryProgress(index + 1, files.Length, relativePath));
        }

        Save(manifest);
        return manifest;
    }

    public static BatchManifest Load(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        string fullPath = Path.GetFullPath(manifestPath);
        BatchManifest manifest = JsonSerializer.Deserialize<BatchManifest>(File.ReadAllText(fullPath), JsonOptions)
            ?? throw new PkgFormatException("The batch manifest is empty or invalid.");
        if (manifest.FormatVersion != 1)
            throw new PkgFormatException($"Batch manifest version {manifest.FormatVersion} is not supported.");
        if (!PathComparer.Equals(fullPath, manifest.ManifestPath))
            throw new PkgFormatException(
                $"This manifest belongs at '{manifest.ManifestPath}'. Move it back or choose that file instead.");
        ValidateManifest(manifest);
        return manifest;
    }

    public static void Run(
        BatchManifest manifest,
        IKeyProvider keys,
        BatchProcessorOptions? options = null,
        CancellationToken cancellationToken = default,
        IProgress<BatchRunProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(keys);
        options ??= new BatchProcessorOptions();
        ValidateManifest(manifest);

        foreach (BatchJob interrupted in manifest.Jobs.Where(job => job.Status == BatchJobStatus.Running))
        {
            interrupted.Status = BatchJobStatus.Pending;
            interrupted.Message = "Interrupted; ready to resume";
        }
        Save(manifest);

        for (int index = 0; index < manifest.Jobs.Count; index++)
        {
            BatchJob job = manifest.Jobs[index];
            if (job.Status is BatchJobStatus.Completed or BatchJobStatus.Skipped)
                continue;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                job.Status = BatchJobStatus.Running;
                job.Message = "Starting";
                job.Attempts++;
                job.StartedUtc = DateTimeOffset.UtcNow;
                job.FinishedUtc = null;
                Save(manifest);
                Report(progress, manifest, job, "Starting");

                string message = ProcessJob(manifest, job, keys, options, cancellationToken,
                    new InlineProgress<double>(percent =>
                        progress?.Report(new BatchRunProgress(manifest.CompletedCount, manifest.Jobs.Count,
                            job.RelativePath, BatchJobStatus.Running, job.Message ?? "Working", percent))));
                job.Status = BatchJobStatus.Completed;
                job.Message = message;
            }
            catch (BatchSkipException ex)
            {
                job.Status = BatchJobStatus.Skipped;
                job.Message = ex.Message;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                job.Status = BatchJobStatus.Pending;
                job.Message = "Paused; ready to resume";
                job.FinishedUtc = null;
                Save(manifest);
                Report(progress, manifest, job, job.Message);
                throw;
            }
            catch (Exception ex) when (ex is PkgFormatException or PkgKeyException or IOException or
                                       UnauthorizedAccessException or CryptographicException)
            {
                job.Status = BatchJobStatus.Failed;
                job.Message = ex.Message;
            }

            job.FinishedUtc = DateTimeOffset.UtcNow;
            Save(manifest);
            Report(progress, manifest, job, job.Message ?? job.Status.ToString());
        }

        if (manifest.Operation == BatchOperation.Classify)
            WriteTextAtomic(Path.Combine(manifest.OutputDirectory, "classify", "library-report.json"),
                PackageLibraryMatcher.ToJson(PackageLibraryMatcher.Analyze(
                    manifest.Jobs.Select(job => job.Package))));
    }

    public static void RetryFailed(BatchManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        foreach (BatchJob job in manifest.Jobs.Where(job => job.Status == BatchJobStatus.Failed))
        {
            job.Status = BatchJobStatus.Pending;
            job.Message = "Ready to retry";
        }
        Save(manifest);
    }

    public static void Save(BatchManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.UpdatedUtc = DateTimeOffset.UtcNow;
        WriteTextAtomic(manifest.ManifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    public static string ManifestPath(string outputDirectory, BatchOperation operation) =>
        Path.Combine(Path.GetFullPath(outputDirectory), $".pkglens-batch-{OperationSlug(operation)}.json");

    private static string ProcessJob(BatchManifest manifest, BatchJob job, IKeyProvider keys,
        BatchProcessorOptions options, CancellationToken cancellationToken, IProgress<double> itemProgress)
    {
        if (job.Package.Failed)
            throw new PkgFormatException(job.Package.Note ?? "The package could not be read.");

        return manifest.Operation switch
        {
            BatchOperation.Audit => Audit(job, keys, options, cancellationToken),
            BatchOperation.Classify => Classify(job),
            BatchOperation.Verify => Verify(job, keys),
            BatchOperation.Extract => Extract(job, keys, cancellationToken, itemProgress),
            BatchOperation.ExportPbp => ExportPsp(job, keys, PspExportFormat.Pbp, cancellationToken, itemProgress),
            BatchOperation.ExportIso => ExportPsp(job, keys, PspExportFormat.Iso, cancellationToken, itemProgress),
            BatchOperation.ExportCso => ExportPsp(job, keys, PspExportFormat.Cso, cancellationToken, itemProgress),
            BatchOperation.ConvertCfw => ConvertCfw(job, keys, options, cancellationToken, itemProgress),
            _ => throw new ArgumentOutOfRangeException(nameof(manifest.Operation)),
        };
    }

    private static string Audit(BatchJob job, IKeyProvider keys, BatchProcessorOptions options,
        CancellationToken cancellationToken)
    {
        KeyLicenseAuditReport report = KeyLicenseAudit.Inspect(job.SourcePath, keys,
            new KeyLicenseAuditOptions { RapDirectory = options.RapDirectory }, cancellationToken);
        WriteTextAtomic(job.OutputPath, KeyLicenseAudit.ToJson(report));
        return $"Audited: {report.MissingRapCount} missing RAP(s), {report.UnsupportedCount} unsupported";
    }

    private static string Classify(BatchJob job)
    {
        WriteTextAtomic(job.OutputPath, PackageScanner.ToJson(new[] { job.Package }));
        return $"Classified as {job.Package.Platform} {job.Package.Role}";
    }

    private static string Verify(BatchJob job, IKeyProvider keys)
    {
        using var source = File.OpenRead(job.SourcePath);
        PkgVerificationReport report = PkgVerifier.Verify(source, keys);
        WriteTextAtomic(job.OutputPath, JsonSerializer.Serialize(report, JsonOptions));
        if (!report.Passed)
            throw new PkgFormatException($"Package verification found {report.Failures} failure(s). See the saved report.");
        return $"Verified {report.Checks.Count} check(s)";
    }

    private static string Extract(BatchJob job, IKeyProvider keys, CancellationToken cancellationToken,
        IProgress<double> progress)
    {
        string partial = job.OutputPath + ".partial";
        if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
        if (Directory.Exists(job.OutputPath))
            throw new BatchSkipException("Output folder already exists; treated as previously completed");

        Directory.CreateDirectory(partial);
        try
        {
            using var source = File.OpenRead(job.SourcePath);
            PkgInfo info = PkgReader.Read(source, keys);
            OperationEligibilityResult eligibility = OperationCatalog.CheckBatch(BatchOperation.Extract, info);
            if (!eligibility.Eligible)
                throw new BatchSkipException(eligibility.Reason);
            var pkgProgress = new InlineProgress<PkgOperationProgress>(value => progress.Report(value.Percent));
            int count = PkgReader.ExtractAll(source, info, partial, keys,
                cancellationToken: cancellationToken, progress: pkgProgress);
            Directory.Move(partial, job.OutputPath);
            return $"Extracted {count} file(s)";
        }
        catch
        {
            try { if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true); } catch { }
            throw;
        }
    }

    private static string ExportPsp(BatchJob job, IKeyProvider keys, PspExportFormat format,
        CancellationToken cancellationToken, IProgress<double> progress)
    {
        using (var source = File.OpenRead(job.SourcePath))
        {
            PkgInfo info = PkgReader.Read(source, keys);
            BatchOperation operation = format switch
            {
                PspExportFormat.Pbp => BatchOperation.ExportPbp,
                PspExportFormat.Iso => BatchOperation.ExportIso,
                PspExportFormat.Cso => BatchOperation.ExportCso,
                _ => throw new ArgumentOutOfRangeException(nameof(format)),
            };
            OperationEligibilityResult eligibility = OperationCatalog.CheckBatch(operation, info);
            if (!eligibility.Eligible) throw new BatchSkipException(eligibility.Reason);
        }

        PspExportResult? result = null;
        WriteFileAtomic(job.OutputPath, destination =>
        {
            using var source = File.OpenRead(job.SourcePath);
            var exportProgress = new InlineProgress<PspExportProgress>(value => progress.Report(value.Percentage));
            result = PspPackageExporter.Export(source, destination, keys, format,
                cancellationToken: cancellationToken, progress: exportProgress);
        });
        PostOperationVerifier.VerifyPspExport(job.OutputPath, result!, cancellationToken);
        return $"Exported and verified {format.ToString().ToUpperInvariant()} ({result!.OutputSize:n0} bytes)";
    }

    private static string ConvertCfw(BatchJob job, IKeyProvider keys, BatchProcessorOptions options,
        CancellationToken cancellationToken, IProgress<double> progress)
    {
        using (var source = File.OpenRead(job.SourcePath))
        {
            PkgInfo info = PkgReader.Read(source, keys);
            OperationEligibilityResult eligibility = OperationCatalog.CheckBatch(BatchOperation.ConvertCfw, info);
            if (!eligibility.Eligible)
                throw new BatchSkipException(eligibility.Reason);
        }

        CfwConversionReport? report = null;
        WriteFileAtomic(job.OutputPath, destination =>
        {
            using var source = File.OpenRead(job.SourcePath);
            var conversionProgress = new InlineProgress<CfwConversionProgress>(value =>
                progress.Report(value.Percent ?? 0));
            report = CfwPackageConverter.Convert(source, destination, keys, new CfwConversionOptions
            {
                SourceName = job.SourcePath,
                OutputName = job.OutputPath,
                KlicenseeResolver = contentId =>
                {
                    byte[]? rap = RapStore.Find(contentId, options.RapDirectory);
                    return new CfwLicenseResolution(
                        rap is null ? null : NpdKeys.RapToKlicensee(rap),
                        rap is null ? null : "RAP library");
                },
            }, cancellationToken, conversionProgress);
        });
        PostOperationVerifier.VerifyPackage(job.OutputPath, keys,
            report!.Executables.Select(executable => executable.Path));
        WriteTextAtomic(Path.ChangeExtension(job.OutputPath, ".report.txt"), report.ToText());
        return $"Converted and verified {report.Executables.Count} executable(s)";
    }

    private static string BuildOutputPath(string outputRoot, string relativePath, BatchOperation operation)
    {
        string? parent = Path.GetDirectoryName(relativePath);
        string baseName = Path.GetFileNameWithoutExtension(relativePath);
        string operationRoot = Path.Combine(outputRoot, OperationSlug(operation));
        if (!string.IsNullOrEmpty(parent)) operationRoot = Path.Combine(operationRoot, parent);
        return operation switch
        {
            BatchOperation.Audit => Path.Combine(operationRoot, baseName + ".audit.json"),
            BatchOperation.Classify => Path.Combine(operationRoot, baseName + ".classification.json"),
            BatchOperation.Verify => Path.Combine(operationRoot, baseName + ".verification.json"),
            BatchOperation.Extract => Path.Combine(operationRoot, baseName),
            BatchOperation.ExportPbp => Path.Combine(operationRoot, baseName + ".pbp"),
            BatchOperation.ExportIso => Path.Combine(operationRoot, baseName + ".iso"),
            BatchOperation.ExportCso => Path.Combine(operationRoot, baseName + ".cso"),
            BatchOperation.ConvertCfw => Path.Combine(operationRoot, baseName + "-cfw.pkg"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    private static string OperationSlug(BatchOperation operation) => operation switch
    {
        BatchOperation.Audit => "audit",
        BatchOperation.Classify => "classify",
        BatchOperation.Verify => "verify",
        BatchOperation.Extract => "extract",
        BatchOperation.ExportPbp => "psp-pbp",
        BatchOperation.ExportIso => "psp-iso",
        BatchOperation.ExportCso => "psp-cso",
        BatchOperation.ConvertCfw => "cfw",
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static bool IsExecutable(string path)
    {
        string leaf = Path.GetFileName(path);
        return leaf.Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase) ||
               Path.GetExtension(leaf).Equals(".self", StringComparison.OrdinalIgnoreCase) ||
               Path.GetExtension(leaf).Equals(".sprx", StringComparison.OrdinalIgnoreCase);
    }

    private static void Report(IProgress<BatchRunProgress>? progress, BatchManifest manifest,
        BatchJob job, string message) =>
        progress?.Report(new BatchRunProgress(manifest.CompletedCount, manifest.Jobs.Count,
            job.RelativePath, job.Status, message));

    private static void WriteTextAtomic(string path, string content) =>
        WriteFileAtomic(path, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.Write(content);
        });

    private static void WriteFileAtomic(string path, Action<Stream> write)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ?? ".";
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".pkglens-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                write(output);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static bool IsWithin(string path, string directory)
    {
        string fullPath = Path.GetFullPath(path);
        string fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullDirectory, PathComparison);
    }

    private static void ValidateManifest(BatchManifest manifest)
    {
        string sourceRoot = Path.GetFullPath(manifest.SourceDirectory);
        string outputRoot = Path.GetFullPath(manifest.OutputDirectory);
        foreach (BatchJob job in manifest.Jobs)
        {
            string expectedSource = Path.GetFullPath(Path.Combine(sourceRoot, job.RelativePath));
            if (!IsWithin(expectedSource, sourceRoot) || !PathComparer.Equals(expectedSource, Path.GetFullPath(job.SourcePath)))
                throw new PkgFormatException($"Batch manifest source path is outside its source folder: {job.SourcePath}");
            string expectedOutput = BuildOutputPath(outputRoot, job.RelativePath, manifest.Operation);
            if (!IsWithin(expectedOutput, outputRoot) || !PathComparer.Equals(expectedOutput, Path.GetFullPath(job.OutputPath)))
                throw new PkgFormatException($"Batch manifest output path is outside its output folder: {job.OutputPath}");
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed class BatchSkipException(string message) : Exception(message);

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
