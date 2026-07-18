using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
    public SfoTable? Sfo => Info.Sfo;
    public bool HasPendingChanges => _replacements.Count > 0;
    public int PendingChangeCount => _replacements.Count;
    public bool CanEditSfo => Info.Sfo is not null && FindSfoEntry() is not null;

    public event EventHandler? PendingChangesChanged;

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

    public byte[] ReadEntryBytes(PkgEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return PkgReader.ExtractEntryBytes(_stream, Info.Header, entry, _keys);
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
        PendingChangesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ReplaceEntry(PkgEntry entry, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(content);
        _replacements[entry] = content;
        PendingChangesChanged?.Invoke(this, EventArgs.Empty);
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
