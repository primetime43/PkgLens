using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PkgLens.Core;
using PkgLens.Core.Ps3.Self;

namespace PkgLens.Gui.Services;

internal enum SelfFolderOperation { Decrypt, Rebuild, FakeSign, LegacySign }
internal enum SelfFolderStatus { Pending, Running, Complete, Failed, Cancelled, Skipped }

internal sealed record SelfFolderOptions(SelfFolderOperation Operation, ushort Revision = 0x0A,
    bool Compress = true, bool Recursive = true);

internal sealed record SelfFolderJob(string Source, string Output, string RelativePath)
{
    public string Type { get; set; } = "Unknown";
    public SelfFolderStatus Status { get; set; }
    public int Attempts { get; set; }
    public string Message { get; set; } = "Ready";
    public string OutputName { get; init; } = "";
}

internal sealed record SelfFolderPlan(string SourceRoot, string OutputRoot, SelfFolderOptions Options,
    IReadOnlyList<SelfFolderJob> Jobs);
internal sealed record SelfFolderProgress(int Index, SelfFolderJob Job, int Finished, int Total);

/// <summary>Sequential, bounded-memory processing into a separate directory. No source or existing output is replaced.</summary>
internal static class SelfFolderService
{
    internal static SelfFolderPlan Scan(string source, string output, SelfFolderOptions options,
        CancellationToken token = default)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output));
        ValidateRoots(source, output);
        var jobs = new List<SelfFolderJob>();
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = options.Recursive, IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false,
        };
        foreach (string path in Directory.EnumerateFiles(source, "*", enumeration))
        {
            token.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(path);
            bool candidate = extension.Equals(".self", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".sprx", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".elf", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase);
            if (!candidate) continue;
            string relative = Path.GetRelativePath(source, path);
            // Appending, rather than replacing extensions, keeps foo.self and foo.sprx distinct.
            string outputName = options.Operation == SelfFolderOperation.Decrypt ? relative + ".elf"
                : extension.Equals(".elf", StringComparison.OrdinalIgnoreCase) ? relative + ".self" : relative;
            var job = new SelfFolderJob(path, Path.Combine(output, outputName), relative) { OutputName = outputName };
            try
            {
                var info = Inspect(path);
                SetType(job, info);
                if (options.Operation == SelfFolderOperation.Decrypt && info is null)
                    SetResult(job, SelfFolderStatus.Skipped, "Already a plaintext ELF.");
                else if (File.Exists(job.Output) || Directory.Exists(job.Output))
                    SetResult(job, SelfFolderStatus.Skipped, "Output already exists; choose another output folder to rebuild it.");
                else
                    CheckProfile(info, options);
            }
            catch (Exception ex) when (IsFileError(ex)) { SetResult(job, SelfFolderStatus.Failed, ex.Message); }
            jobs.Add(job);
        }
        jobs.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        // Also catch .elf -> .elf.self collisions and names differing only by case (portable output).
        foreach (var group in jobs.GroupBy(j => j.Output, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var job in group) SetResult(job, SelfFolderStatus.Failed, "Output name collision. Rename the conflicting inputs and scan again.");
        return new(source, output, options, jobs);
    }

    internal static void Run(SelfFolderPlan plan, EdatKeySelection key, string? rapDirectory,
        bool retryFailed = false, CancellationToken token = default, IProgress<SelfFolderProgress>? progress = null)
    {
        ValidateRoots(plan.SourceRoot, plan.OutputRoot);
        var collisions = plan.Jobs.GroupBy(j => j.Output, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = plan.Jobs.Select((job, index) => (job, index)).Where(x => retryFailed
            ? x.job.Status == SelfFolderStatus.Failed
            : x.job.Status is SelfFolderStatus.Pending or SelfFolderStatus.Cancelled).ToArray();
        int finished = 0;
        foreach (var (job, index) in selected)
        {
            if (token.IsCancellationRequested) break;
            job.Attempts++;
            SetResult(job, SelfFolderStatus.Running, "Processing…");
            progress?.Report(new(index, job with { }, finished, selected.Length));
            try
            {
                if (collisions.Contains(job.Output)) throw new IOException("Output name collision. Rename the conflicting inputs and scan again.");
                ValidateRoots(plan.SourceRoot, plan.OutputRoot);
                EnsureChild(plan.SourceRoot, job.Source);
                EnsureChild(plan.OutputRoot, job.Output);
                EnsureNoLinks(job.Source);
                EnsureNoLinks(job.Output);
                if (File.Exists(job.Output) || Directory.Exists(job.Output))
                {
                    SetResult(job, SelfFolderStatus.Skipped, "Output already exists; left untouched.");
                }
                else
                {
                    SelfInfo? info = Inspect(job.Source);
                    SetType(job, info);
                    CheckProfile(info, plan.Options);
                    if (plan.Options.Operation == SelfFolderOperation.Decrypt && info is null)
                        SetResult(job, SelfFolderStatus.Skipped, "Already a plaintext ELF.");
                    else
                    {
                        token.ThrowIfCancellationRequested();
                        Directory.CreateDirectory(Path.GetDirectoryName(job.Output)!);
                        EnsureNoLinks(job.Output);
                        if (plan.Options.Operation == SelfFolderOperation.Decrypt)
                        {
                            SelfBuildService.DecryptFile(job.Source, job.Output, key, rapDirectory, token, overwrite: false);
                            SetResult(job, SelfFolderStatus.Complete, "Decrypted; recovered ELF validated.");
                        }
                        else
                        {
                            var metadata = new SelfBuilder.FakeSelfOptions
                            {
                                Npdrm = info?.RawProgramType == 8,
                                NpLicenseType = info?.Npdrm?.RawLicenseType,
                                CompressSegments = plan.Options.Compress,
                            };
                            SelfBuildService.BuildFile(job.Source, job.Output,
                                plan.Options.Operation != SelfFolderOperation.FakeSign, metadata,
                                plan.Options.Revision, key, key, rapDirectory, token,
                                signHeader: plan.Options.Operation == SelfFolderOperation.LegacySign, overwrite: false);
                            SetResult(job, SelfFolderStatus.Complete, plan.Options.Operation == SelfFolderOperation.LegacySign
                                ? "ELF round-trip and legacy header signature verified."
                                : "Rebuilt; ELF round-trip verified.");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                SetResult(job, SelfFolderStatus.Cancelled, "Cancelled before publishing output; resume to retry.");
            }
            catch (Exception ex) when (IsFileError(ex)) { SetResult(job, SelfFolderStatus.Failed, ex.Message); }
            finished++;
            progress?.Report(new(index, job with { }, finished, selected.Length));
        }
    }

    private static SelfInfo? Inspect(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 128 * 1024 * 1024) throw new PkgFormatException("Input exceeds the 128 MiB limit.");
        Span<byte> header = stackalloc byte[20];
        file.ReadExactly(header);
        if (header.StartsWith("SCE\0"u8))
        {
            SelfInfo info = SelfReader.ParseInfo(file);
            if (info.RawProgramType is not 4 and not 8)
                throw new PkgFormatException("Only APP and NPDRM SELF files are supported.");
            if (info.RawProgramType == 8 && info.Npdrm is null)
                throw new PkgFormatException("NPDRM file has no readable license metadata.");
            if (info.KeyRevision is not 0x8000 and not 0xC000 && SelfKeyset.Find(info.RawProgramType, info.KeyRevision) is null)
                throw new PkgFormatException($"No decryption profile for key revision {info.KeyRevision:X2}.");
            return info;
        }
        if (!header.StartsWith("\x7f"u8) || !header.Slice(1, 3).SequenceEqual("ELF"u8)
            || header[4] != 2 || header[5] != 2 || header[18] != 0 || header[19] != 21)
            throw new PkgFormatException("Expected a PS3 PPU ELF or SELF executable.");
        return null;
    }

    private static void CheckProfile(SelfInfo? info, SelfFolderOptions options)
    {
        uint programType = info?.RawProgramType ?? 4;
        if (options.Operation == SelfFolderOperation.LegacySign && !LegacySelfSigning.IsSupported(programType, options.Revision))
            throw new PkgFormatException($"No legacy signing profile for {(programType == 8 ? "NPDRM" : "APP")} key {options.Revision:X2}.");
        if (options.Operation == SelfFolderOperation.Rebuild && SelfKeyset.Find(programType, options.Revision) is null)
            throw new PkgFormatException($"No encryption profile for {(programType == 8 ? "NPDRM" : "APP")} key {options.Revision:X2}.");
    }

    private static void SetType(SelfFolderJob job, SelfInfo? info) => job.Type = info is null ? "ELF"
        : info.Npdrm is null ? "APP" : $"NPDRM ({info.Npdrm.RawLicenseType switch { 1 => "network", 2 => "local", 3 => "free", _ => "unknown" }})";

    private static void SetResult(SelfFolderJob job, SelfFolderStatus status, string message)
    { job.Status = status; job.Message = message; }

    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or PkgFormatException or ArgumentException or InvalidOperationException
        or System.Security.Cryptography.CryptographicException or OverflowException or NotSupportedException;

    private static void ValidateRoots(string source, string output)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("The source folder does not exist.");
        if (Within(source, output) || Within(output, source))
            throw new IOException("Choose separate source and output folders; neither may contain the other.");
        EnsureNoLinks(source);
        EnsureNoLinks(output);
    }

    private static bool Within(string parent, string child)
    {
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return child.Equals(parent, comparison) || child.StartsWith(Path.EndsInDirectorySeparator(parent)
            ? parent : parent + Path.DirectorySeparatorChar, comparison);
    }

    private static void EnsureChild(string parent, string child)
    {
        if (!Within(parent, child) || Path.GetFullPath(parent) == Path.GetFullPath(child))
            throw new IOException("File path is outside its selected folder.");
    }

    private static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked files and folders are not supported in folder processing: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
