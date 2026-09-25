using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using PkgLens.Core;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Gui.Services;

internal sealed record PackageSelfSettings(SelfFolderOperation Operation, ushort Revision = 0x0A,
    bool Compress = true, string? RapDirectory = null, string? RawKey = null);
internal sealed record PackageSelfCheck(string Name, string Status, string Message);

/// <summary>Owns disk-backed, verified executable replacements until a package build finishes.</summary>
internal sealed class PreparedPackageExecutables : IDisposable
{
    private const long MaxInputBytes = 128 * 1024 * 1024;
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "pkglens-pack-self-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, byte[]> _sourceHashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _outputHashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PkgReplacement> _files = new(StringComparer.Ordinal);
    private readonly List<PackageSelfCheck> _checks = new();
    private readonly object _origin;
    private bool _disposed;
    private PreparedPackageExecutables(object origin) { _origin = origin; Directory.CreateDirectory(_temporary); }
    internal IReadOnlyList<PackageSelfCheck> Checks => _checks;
    internal bool CanBuild => !_disposed && _checks.All(c => c.Status == "Ready");
    internal IReadOnlyDictionary<string, PkgReplacement> Files => _files;

    internal static bool IsExecutable(string name)
    {
        string leaf = name.Replace('\\', '/').Split('/').Last();
        return leaf.Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase)
            || new[] { ".self", ".sprx", ".elf" }.Any(ext => leaf.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record Source(string Name, Func<byte[]> Read);
    private static IEnumerable<Source> FolderSources(string folder, CancellationToken token)
    {
        var root = new DirectoryInfo(folder);
        if (!root.Exists) throw new DirectoryNotFoundException("The package source folder does not exist.");
        var pending = new Stack<DirectoryInfo>(); pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            RejectLink(dir);
            foreach (var item in dir.EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                RejectLink(item);
                if (item is DirectoryInfo child) pending.Push(child);
                else if (item is FileInfo file && IsExecutable(file.Name))
                {
                    string name = Path.GetRelativePath(folder, file.FullName).Replace('\\', '/');
                    yield return new(name, () =>
                    {
                        RejectLink(new FileInfo(file.FullName));
                        using var stream = File.OpenRead(file.FullName);
                        if (stream.Length > MaxInputBytes) throw new PkgFormatException("Executable exceeds the 128 MiB input limit.");
                        byte[] data = new byte[(int)stream.Length]; stream.ReadExactly(data); return data;
                    });
                }
            }
        }
    }

    private static void RejectLink(FileSystemInfo item)
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0 || item.LinkTarget is not null)
            throw new IOException("Linked files and folders cannot be packed: " + item.FullName);
    }

    private static IEnumerable<Source> PackageSources(PackageOperationService package, CancellationToken token)
    {
        if (package.Info.Header.Platform != PkgPlatform.Ps3)
            throw new PkgFormatException("PS3 executable rebuilding requires a PS3 package.");
        foreach (var entry in package.Info.Entries.Where(e => e.IsFile && IsExecutable(e.Name)))
        {
            token.ThrowIfCancellationRequested();
            yield return new(entry.Name, () =>
            {
                if (package.GetEntrySize(entry) > MaxInputBytes) throw new PkgFormatException("Executable exceeds the 128 MiB input limit.");
                return package.ReadEntryBytes(entry, token);
            });
        }
    }

    internal static PreparedPackageExecutables ForFolder(string folder, PackageSelfSettings settings,
        CancellationToken token = default, IProgress<PackageSelfCheck>? progress = null) =>
        Prepare(Path.GetFullPath(folder), FolderSources(folder, token), settings, token, progress);

    internal static PreparedPackageExecutables ForPackage(PackageOperationService package, PackageSelfSettings settings,
        CancellationToken token = default, IProgress<PackageSelfCheck>? progress = null) =>
        Prepare(package, PackageSources(package, token), settings, token, progress);

    private static PreparedPackageExecutables Prepare(object origin, IEnumerable<Source> sources,
        PackageSelfSettings settings, CancellationToken token, IProgress<PackageSelfCheck>? progress)
    {
        if (settings.Operation == SelfFolderOperation.Decrypt) throw new ArgumentException("Choose a SELF output mode.");
        EdatWorkbenchService.ParseKey(settings.RawKey);
        var prepared = new PreparedPackageExecutables(origin);
        try
        {
            var entries = sources.OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();
            if (entries.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length)
                throw new PkgFormatException("Executable paths collide. Resolve duplicate names before packing.");
            for (int i = 0; i < entries.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var source = entries[i];
                progress?.Report(new(source.Name, "Checking", "Resolving keys, rebuilding and verifying…"));
                PackageSelfCheck check;
                string work = Path.Combine(prepared._temporary, i.ToString());
                try
                {
                    byte[] bytes = source.Read();
                    SelfInfo? info = bytes.AsSpan().StartsWith("SCE\0"u8)
                        ? SelfReader.ParseInfo(new MemoryStream(bytes)) : null;
                    if (info is not null && (info.RawProgramType is not 4 and not 8 || info.RawProgramType == 8 && info.Npdrm is null))
                        throw new PkgFormatException("Only APP and NPDRM SELF files with readable metadata are supported.");
                    string leaf = source.Name.Replace('\\', '/').Split('/').Last();
                    string input = Path.Combine(work, "input", leaf), output = Path.Combine(work, "output", leaf);
                    Directory.CreateDirectory(Path.GetDirectoryName(input)!);
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    File.WriteAllBytes(input, bytes);
                    prepared._sourceHashes.Add(source.Name, SHA256.HashData(bytes));
                    var metadata = new SelfBuilder.FakeSelfOptions
                    {
                        Npdrm = info?.RawProgramType == 8, NpLicenseType = info?.Npdrm?.RawLicenseType,
                        CompressSegments = settings.Compress,
                    };
                    var key = new EdatKeySelection(settings.RawKey);
                    SelfBuildService.BuildFile(input, output, settings.Operation != SelfFolderOperation.FakeSign,
                        metadata, settings.Revision, key, key, settings.RapDirectory, token,
                        signHeader: settings.Operation == SelfFolderOperation.LegacySign, overwrite: false);
                    File.Delete(input);
                    prepared._files.Add(source.Name, PkgReplacement.FromFile(output));
                    using var stream = File.OpenRead(output);
                    prepared._outputHashes.Add(source.Name, SHA256.HashData(stream));
                    check = new(source.Name, "Ready", settings.Operation == SelfFolderOperation.LegacySign
                        ? "ELF round-trip and legacy header signature verified."
                        : "ELF round-trip verified.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PkgFormatException
                    or ArgumentException or InvalidOperationException or NotSupportedException or OverflowException or CryptographicException)
                {
                    check = new(source.Name, "Blocked", ex.Message);
                }
                prepared._checks.Add(check);
                progress?.Report(check);
            }
            token.ThrowIfCancellationRequested();
            return prepared;
        }
        catch { prepared.Dispose(); throw; }
    }

    internal void ValidateFolder(string folder, CancellationToken token)
    {
        if (_origin is not string root || !root.Equals(Path.GetFullPath(folder),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new PkgFormatException("The source folder changed. Check executables again.");
        Validate(FolderSources(folder, token), token);
    }
    internal void ValidatePackage(PackageOperationService package, CancellationToken token)
    {
        if (!ReferenceEquals(_origin, package)) throw new PkgFormatException("The source package changed. Check executables again.");
        Validate(PackageSources(package, token), token);
    }
    private void Validate(IEnumerable<Source> sources, CancellationToken token)
    {
        if (!CanBuild) throw new PkgFormatException("Resolve all executable errors and check again before building the package.");
        int count = 0;
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested(); count++;
            if (!_sourceHashes.TryGetValue(source.Name, out var hash) || !SHA256.HashData(source.Read()).AsSpan().SequenceEqual(hash))
                throw new PkgFormatException($"{source.Name} changed after checking. Check executables again.");
        }
        if (count != _sourceHashes.Count) throw new PkgFormatException("The executable list changed. Check again before building.");
    }

    internal void VerifyEmbedded(string packagePath, IKeyProvider keys, CancellationToken token)
    {
        using var stream = File.OpenRead(packagePath);
        var info = PkgReader.Read(stream, keys);
        foreach (var pair in _files)
        {
            token.ThrowIfCancellationRequested();
            var entry = info.Entries.SingleOrDefault(e => e.IsFile && e.Name == pair.Key);
            if (entry is null || entry.FileSize != (ulong)pair.Value.Length)
                throw new PkgFormatException("Rebuilt executable missing or wrong size: " + pair.Key);
            using var content = PkgReader.OpenEntry(stream, info.Header, entry, keys);
            if (!SHA256.HashData(content).AsSpan().SequenceEqual(_outputHashes[pair.Key]))
                throw new PkgFormatException("Packaged executable differs from the verified output: " + pair.Key);
        }
        token.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Only our own randomly named temporary directory is ever removed.
        try { Directory.Delete(_temporary, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
