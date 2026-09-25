using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PkgLens.Core.Ps3.Trophy;
using PkgLens.Core.Psp;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Shared.Sfo;

namespace PkgLens.Gui.Services;

public sealed class PackageOperationService : IDisposable
{
    private readonly Stream _stream;
    private readonly IKeyProvider _keys;
    private readonly Dictionary<PkgEntry, byte[]> _replacements = new();

    private PackageOperationService(string filePath, Stream stream, PkgInfo info, IKeyProvider keys)
    {
        FilePath = filePath;
        _stream = stream;
        Info = info;
        _keys = keys;
    }

    public string FilePath { get; }
    public PkgInfo Info { get; }
    public PspExportEligibility PspExportEligibility => PspPackageExporter.CheckEligibility(Info);
    public SfoTable? Sfo => FindSfoEntry() is { } entry && _replacements.TryGetValue(entry, out var content)
        ? SfoParser.Parse(content)
        : Info.Sfo;
    public bool HasPendingChanges => _replacements.Count > 0;
    public int PendingChangeCount => _replacements.Count;
    public IReadOnlyList<PendingPackageChange> PendingChanges => _replacements
        .OrderBy(pair => pair.Key.Name, StringComparer.Ordinal)
        .Select(pair => new PendingPackageChange(pair.Key, (ulong)pair.Value.LongLength)).ToArray();
    public bool CanEditSfo => FindSfoEntry() is not null;

    public event EventHandler<PackageEntryChangedEventArgs>? PendingChangesChanged;

    public static PackageOperationService Open(string path, IKeyProvider keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = File.OpenRead(path);
        try
        {
            PkgInfo info = PkgReader.Read(stream, keys);
            cancellationToken.ThrowIfCancellationRequested();
            return new PackageOperationService(path, stream, info, keys);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public PkgVerificationReport Verify() => PkgVerifier.Verify(_stream, _keys);

    public bool IsReplaced(PkgEntry entry) => _replacements.ContainsKey(entry);

    public ulong GetEntrySize(PkgEntry entry) => _replacements.TryGetValue(entry, out var content)
        ? (ulong)content.LongLength
        : entry.FileSize;

    public byte[] ReadEntryPrefix(PkgEntry entry, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        return _replacements.TryGetValue(entry, out var content)
            ? content.AsSpan(0, Math.Min(maxBytes, content.Length)).ToArray()
            : PkgReader.ExtractEntryPrefix(_stream, Info.Header, entry, _keys, maxBytes);
    }

    /// <summary>Reads the current editing version, including unsaved replacement bytes.</summary>
    public byte[] ReadEntryBytes(PkgEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        if (_replacements.TryGetValue(entry, out var content))
            return content.ToArray();
        using var output = new MemoryStream();
        PkgReader.ExtractEntry(_stream, Info.Header, entry, output, _keys, cancellationToken);
        return output.ToArray();
    }

    public byte[]? TryReadEntryBytes(string path)
    {
        PkgEntry? entry = Info.Entries.FirstOrDefault(candidate =>
            candidate.IsFile && candidate.Name.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return null;
        try
        {
            return ReadEntryBytes(entry);
        }
        catch
        {
            return null;
        }
    }

    public int ExtractAll(string directory, CancellationToken cancellationToken = default,
        IProgress<PkgOperationProgress>? progress = null) =>
        PkgReader.ExtractAll(_stream, Info, directory, _keys,
            cancellationToken: cancellationToken, progress: progress);

    public ContentDecryptReport DecryptContents(string directory, ContentDecryptOptions options,
        CancellationToken cancellationToken = default, IProgress<ContentDecryptProgress>? progress = null) =>
        PackageContentDecryptor.Export(_stream, Info, _keys, directory, options, _replacements,
            cancellationToken, progress);

    public void ExtractEntry(PkgEntry entry, string destinationPath,
        CancellationToken cancellationToken = default, IProgress<long>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        AtomicOutput.EnsureDifferentPath(FilePath, destinationPath);
        AtomicOutput.Write(destinationPath,
            destination => PkgReader.ExtractEntry(_stream, Info.Header, entry, destination, _keys,
                cancellationToken, progress));
    }

    public void ApplySfoEdits(IReadOnlyList<SfoEntry> edited)
    {
        ArgumentNullException.ThrowIfNull(edited);
        PkgEntry entry = FindSfoEntry() ??
            throw new InvalidOperationException("This package has no PARAM.SFO to edit.");
        _replacements[entry] = SfoWriter.Write(edited);
        PendingChangesChanged?.Invoke(this, new PackageEntryChangedEventArgs(entry));
    }

    public void ReplaceEntry(PkgEntry entry, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(content);
        _replacements[entry] = content;
        PendingChangesChanged?.Invoke(this, new PackageEntryChangedEventArgs(entry));
    }

    public void RevertEntry(PkgEntry entry)
    {
        if (_replacements.Remove(entry))
            PendingChangesChanged?.Invoke(this, new PackageEntryChangedEventArgs(entry));
    }

    public void SaveAs(string destinationPath, CancellationToken cancellationToken = default,
        IProgress<PkgOperationProgress>? progress = null)
    {
        AtomicOutput.EnsureDifferentPath(FilePath, destinationPath);
        AtomicOutput.Write(destinationPath,
            destination => PkgWriter.Repack(_stream, Info, _replacements, _keys, destination,
                cancellationToken, progress));
    }

    public IReadOnlyList<byte[]> DecryptDocument(PkgEntry documentEntry)
    {
        ArgumentNullException.ThrowIfNull(documentEntry);
        byte[] document = ReadEntryBytes(documentEntry);
        byte[]? documentInfo = ReadSiblingBytes(documentEntry, "DOCINFO.EDAT");
        return PspDocument.DecryptPages(document, documentInfo);
    }

    public TrophySet ReadTrophySet(PkgEntry trophyEntry)
    {
        ArgumentNullException.ThrowIfNull(trophyEntry);
        string? trophySetId = trophyEntry.Name.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Reverse().Skip(1).FirstOrDefault();
        return TrophyArchive.Read(ReadEntryBytes(trophyEntry), trophySetId);
    }

    public IReadOnlyList<string> UnpackPbp(PkgEntry pbpEntry, string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pbpEntry);
        Directory.CreateDirectory(destinationDirectory);
        string temporaryPbp = TemporaryPath(destinationDirectory, "pbp");
        try
        {
            using (Stream destination = File.Create(temporaryPbp))
                PkgReader.ExtractEntry(_stream, Info.Header, pbpEntry, destination, _keys, cancellationToken);

            var written = new List<string>();
            using Stream source = File.OpenRead(temporaryPbp);
            PbpArchive archive = PbpArchive.Parse(source);
            foreach (PbpEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AtomicOutput.Write(Path.Combine(destinationDirectory, entry.Name),
                    destination => PbpArchive.Extract(source, entry, destination));
                written.Add(entry.Name);
            }
            return written;
        }
        finally
        {
            TryDelete(temporaryPbp);
        }
    }

    public void ExportPspIso(PkgEntry pbpEntry, string isoPath,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(pbpEntry);
        string directory = Path.GetDirectoryName(isoPath) ?? ".";
        string temporaryPbp = TemporaryPath(directory, "pbp");
        string temporaryPsar = TemporaryPath(directory, "psar");
        try
        {
            using (Stream destination = File.Create(temporaryPbp))
                PkgReader.ExtractEntry(_stream, Info.Header, pbpEntry, destination, _keys, cancellationToken);

            using (Stream source = File.OpenRead(temporaryPbp))
            {
                PbpArchive archive = PbpArchive.Parse(source);
                PbpEntry psar = archive.Entries.FirstOrDefault(entry =>
                    entry.Name.Equals("DATA.PSAR", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("This PBP has no DATA.PSAR.");
                using Stream destination = File.Create(temporaryPsar);
                PbpArchive.Extract(source, psar, destination);
            }

            using Stream psarStream = File.OpenRead(temporaryPsar);
            var header = new byte[0x100];
            psarStream.ReadExactly(header, 0, header.Length);
            if (!NpumdImg.IsNpumdImg(header))
                throw new InvalidOperationException("DATA.PSAR is not an NPUMDIMG (this game isn't a UMD/minis image).");
            psarStream.Position = 0;
            AtomicOutput.Write(isoPath, iso => NpumdImg.DecryptToIso(psarStream, iso,
                cancellationToken, progress));
        }
        finally
        {
            TryDelete(temporaryPbp);
            TryDelete(temporaryPsar);
        }
    }

    private PkgEntry? FindSfoEntry() => Info.Entries.FirstOrDefault(entry =>
        entry.IsFile && (entry.Name.Equals("PARAM.SFO", StringComparison.OrdinalIgnoreCase) ||
                         entry.Name.EndsWith("/PARAM.SFO", StringComparison.OrdinalIgnoreCase)));

    private byte[]? ReadSiblingBytes(PkgEntry entry, string leafName)
    {
        int slash = entry.Name.LastIndexOf('/');
        string siblingPath = slash < 0 ? leafName : entry.Name[..(slash + 1)] + leafName;
        PkgEntry? sibling = Info.Entries.FirstOrDefault(candidate =>
            candidate.IsFile && candidate.Name.Equals(siblingPath, StringComparison.OrdinalIgnoreCase));
        return sibling is null ? null : ReadEntryBytes(sibling);
    }

    private static string TemporaryPath(string directory, string kind) =>
        Path.Combine(directory, $".pkglens-{kind}-{Guid.NewGuid():N}.tmp");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    public void Dispose() => _stream.Dispose();
}
