using System;
using System.IO;
using System.Threading;
using PkgLens.Core;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Shared;

namespace PkgLens.Gui.Services;

public enum EdatWorkbenchOperation { Decrypt, Encrypt, QuickRebuild, CustomRebuild }

public sealed record EdatKeySelection(string? RawKey = null, string? RapPath = null);

public sealed record EdatWorkbenchRequest(
    EdatWorkbenchOperation Operation, string SourcePath, string OutputFileName,
    EdatWriteOptions Options, EdatKeySelection InputKey, EdatKeySelection OutputKey,
    string? ReplacementPath = null, string? RapDirectory = null, string? KlicenseeDatabasePath = null);

/// <summary>Owns verified scratch output until the user saves or stages it.</summary>
public sealed class EdatWorkbenchResult : IDisposable
{
    internal EdatWorkbenchResult(string directory, string plaintext, string output, string name, bool encrypted)
    { DirectoryPath = directory; PlaintextPath = plaintext; OutputPath = output; FileName = name; IsEncrypted = encrypted; }
    public string DirectoryPath { get; }
    public string PlaintextPath { get; }
    public string OutputPath { get; }
    public string FileName { get; }
    public bool IsEncrypted { get; }

    public void SaveCopy(string destination, string sourcePath, string? replacementPath = null, CancellationToken token = default)
    {
        AtomicOutput.EnsureDifferentPath(sourcePath, destination);
        if (replacementPath is not null) AtomicOutput.EnsureDifferentPath(replacementPath, destination);
        if (IsEncrypted && !Path.GetFileName(destination).Equals(FileName, StringComparison.Ordinal))
            throw new IOException($"Keep the output filename '{FileName}' because it is included in the NPD title hash. Change Output filename and rebuild to rename it.");
        token.ThrowIfCancellationRequested();
        AtomicOutput.Write(destination, output =>
        {
            using var input = File.OpenRead(OutputPath);
            byte[] buffer = new byte[128 * 1024];
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                output.Write(buffer, 0, count);
            }
            token.ThrowIfCancellationRequested();
        });
    }

    public byte[] ReadForStaging()
    {
        if (!IsEncrypted) throw new InvalidOperationException("Build an encrypted EDAT/SDAT before staging it.");
        if (new FileInfo(OutputPath).Length > 128L * 1024 * 1024)
            throw new IOException("Files above 128 MiB must be saved to disk; they cannot be staged in memory.");
        return File.ReadAllBytes(OutputPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
    }
}

public static class EdatWorkbenchService
{
    public static EdatWorkbenchResult Build(EdatWorkbenchRequest request, CancellationToken token = default,
        IProgress<double>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.OutputFileName) || request.OutputFileName != Path.GetFileName(request.OutputFileName) ||
            request.OutputFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Choose a filename without a directory for the output.");
        string scratch = Path.Combine(Path.GetTempPath(), "pkglens-edat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string plainPath = Path.Combine(scratch, "plaintext.tmp");
        string outputPath = Path.Combine(scratch, "protected.tmp");
        try
        {
            using var source = File.OpenRead(request.SourcePath);
            byte[]? inputKey = null;
            NpdInfo? original = null;
            if (request.Operation != EdatWorkbenchOperation.Encrypt)
            {
                original = EdatFile.ParseHeader(source);
                inputKey = ResolveKey(original.ContentId, Path.GetFileName(request.SourcePath), original.IsSdat,
                    original.License, request.InputKey, request.RapDirectory, request.KlicenseeDatabasePath);
                using (var plaintext = File.Create(plainPath)) EdatFile.Decrypt(source, plaintext, inputKey, token);
                // This also verifies the original before a replacement is accepted.
                if (new FileInfo(plainPath).Length != original.FileSize)
                    throw new PkgFormatException("Decrypted size does not match the NPD header.");
            }
            else
            {
                using var plaintext = File.Create(plainPath);
                Copy(source, plaintext, token);
            }
            if (request.Operation == EdatWorkbenchOperation.Decrypt)
            {
                token.ThrowIfCancellationRequested();
                return new(scratch, plainPath, plainPath, request.OutputFileName, false);
            }
            if (request.ReplacementPath is not null && request.Operation is EdatWorkbenchOperation.QuickRebuild or EdatWorkbenchOperation.CustomRebuild)
            {
                using var replacement = File.OpenRead(request.ReplacementPath);
                using var plaintext = File.Create(plainPath);
                Copy(replacement, plaintext, token);
            }
            EdatWriteOptions options;
            byte[]? outputKey;
            if (request.Operation == EdatWorkbenchOperation.QuickRebuild)
            {
                options = EdatWriter.ForRebuild(source, request.OutputFileName);
                outputKey = inputKey;
            }
            else
            {
                options = request.Options with { FileName = request.OutputFileName };
                outputKey = ResolveKey(options.ContentId, request.OutputFileName, options.IsSdat,
                    options.License, request.OutputKey, request.RapDirectory, request.KlicenseeDatabasePath);
                // Free-license encryption and the NPD dev hash use the same developer key.
                if (!options.IsSdat && options.License == 3)
                    options = options with { DeveloperKey = options.DeveloperKey ?? outputKey ?? NpdKeys.KlicFree };
            }
            using (var plaintext = File.OpenRead(plainPath))
            using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                EdatWriter.WriteVerified(plaintext, output, options, outputKey, token, progress);
            token.ThrowIfCancellationRequested();
            return new(scratch, plainPath, outputPath, request.OutputFileName, true);
        }
        catch
        {
            Directory.Delete(scratch, recursive: true);
            throw;
        }
    }

    public static byte[]? ParseKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        byte[] key;
        try { key = Convert.FromHexString(value.Trim()); }
        catch (FormatException) { throw new ArgumentException("Raw keys must contain exactly 32 hexadecimal characters."); }
        if (key.Length != 16) throw new ArgumentException("Raw keys must contain exactly 32 hexadecimal characters.");
        return key;
    }

    private static byte[]? ResolveKey(string contentId, string fileName, bool sdat, int license,
        EdatKeySelection selection, string? rapDirectory, string? databasePath) =>
        EdatKeyValidationService.Resolve(contentId, fileName, sdat, license, selection, rapDirectory, databasePath).Key;

    private static void Copy(Stream source, Stream destination, CancellationToken token)
    {
        byte[] buffer = new byte[128 * 1024];
        int count;
        while ((count = source.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); destination.Write(buffer, 0, count); }
        token.ThrowIfCancellationRequested();
    }
}
