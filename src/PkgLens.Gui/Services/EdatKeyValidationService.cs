using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using PkgLens.Core;
using PkgLens.Core.Ps3.Npd;

namespace PkgLens.Gui.Services;

public enum EdatKeyCheckStatus { Verified, MissingKey, InvalidKey, HeaderMismatch, DamagedContent, InvalidFile, Unverifiable, Unavailable }

/// <summary>Non-secret results only. Raw keys and RAP contents are never returned.</summary>
public sealed record EdatKeyCheckResult(EdatKeyCheckStatus Status, string Message, string? Source = null,
    string? Fingerprint = null, string? ContentId = null)
{
    public string DisplayText => Message + (Source is null ? "" : $"\nKey source: {Source}") +
        (Fingerprint is null ? "" : $"\nKey fingerprint: {Fingerprint}") +
        (string.IsNullOrEmpty(ContentId) ? "" : $"\nContent ID: {ContentId}");
}

internal sealed record ResolvedEdatKey(byte[]? Key, string Source)
{
    public string? Fingerprint => Key is null ? null : Convert.ToHexString(SHA256.HashData(Key))[..12];
}

public static class EdatKeyValidationService
{
    public static EdatKeyCheckResult Check(string sourcePath, EdatKeySelection selection,
        string? rapDirectory = null, string? databasePath = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var source = File.OpenRead(sourcePath);
            return Check(source, Path.GetFileName(sourcePath), selection, rapDirectory, databasePath, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(EdatKeyCheckStatus.Unavailable, "Could not read the file. " + ex.Message); }
    }

    /// <summary>Checks an already opened file without owning or closing its stream.</summary>
    public static EdatKeyCheckResult Check(Stream source, string fileName, EdatKeySelection selection,
        string? rapDirectory = null, string? databasePath = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ResolvedEdatKey? resolved = null;
        NpdInfo? info = null;
        bool authenticated = false;
        EdatKeyCheckResult Result(EdatKeyCheckStatus status, string message) =>
            new(status, message, resolved?.Source, resolved?.Fingerprint, info?.ContentId);
        try
        {
            info = EdatFile.ParseHeader(source);
            if (info.Version is < 0 or > 4)
                return Result(EdatKeyCheckStatus.Unverifiable, "This NPD version is not supported for key validation.");
            // Debug data skips authentication. Never claim that a supplied key matched it.
            if ((info.Flags & 0x80000000) != 0)
                return Result(EdatKeyCheckStatus.Unverifiable, "Debug file: authentication is disabled, so a key match cannot be verified.");
            resolved = Resolve(info.ContentId, fileName, info.IsSdat, info.License,
                selection, rapDirectory, databasePath);
            token.ThrowIfCancellationRequested();
            if (EdatFile.AuthenticateHeader(source, resolved.Key) != true)
                return Result(EdatKeyCheckStatus.HeaderMismatch, info.IsSdat
                    ? "SDAT header authentication failed. No RAP is needed; the header may be damaged."
                    : "Key not confirmed: it does not authenticate this header. The key may be incorrect or the header damaged. Try the correct RAP or raw klicensee, or check another copy of the file.");
            authenticated = true;
            using var sink = new CountingSink();
            EdatFile.Decrypt(source, sink, resolved.Key, token);
            token.ThrowIfCancellationRequested();
            if (sink.Length != info.FileSize)
                return Result(EdatKeyCheckStatus.DamagedContent, "The key matches the header, but decrypted content has an incorrect size. Check another copy of the file.");
            return Result(EdatKeyCheckStatus.Verified, info.IsSdat
                ? "SDAT verified. Its key comes from the header; no RAP or external klicensee is needed."
                : "Key matches. Header, metadata, and file content integrity verified. Ready to decrypt or rebuild.");
        }
        catch (OperationCanceledException) { throw; }
        catch (PkgKeyException ex) { return Result(EdatKeyCheckStatus.MissingKey, ex.Message); }
        catch (ArgumentException ex) { return Result(EdatKeyCheckStatus.InvalidKey, ex.Message); }
        catch (Exception ex) when (ex is PkgFormatException or EndOfStreamException)
        {
            return Result(authenticated ? EdatKeyCheckStatus.DamagedContent : EdatKeyCheckStatus.InvalidFile,
                (authenticated ? "The key matches the header, but content verification failed. " : "Cannot validate this file. ") + ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return Result(EdatKeyCheckStatus.Unavailable, "Could not read the file or key source. " + ex.Message); }
    }

    // Shared with actual processing so a successful check describes the key Build will select.
    internal static ResolvedEdatKey Resolve(string contentId, string fileName, bool sdat, int license,
        EdatKeySelection selection, string? rapDirectory, string? databasePath)
    {
        if (sdat) return new(null, "SDAT header (no external key)");
        if (EdatWorkbenchService.ParseKey(selection.RawKey) is { } raw) return new(raw, "Entered raw klicensee");
        if (!string.IsNullOrWhiteSpace(selection.RapPath))
        {
            if (license == 3) throw new ArgumentException("Free-license EDAT needs a developer klicensee, not a RAP. Clear the RAP selection or enter the raw developer key.");
            return ReadRap(selection.RapPath, "Selected RAP");
        }
        if (!string.IsNullOrWhiteSpace(contentId) && KlicenseeStore.ExtractTitleId(contentId) is not null)
        {
            var local = KlicenseeStore.Find(contentId, fileName, (uint)license, databasePath);
            if (local is not null) return FromCatalog(local, "Local klicensee database");
            var bundled = KnownKlicenseeStore.Find(contentId, fileName, (uint)license);
            if (bundled is not null) return FromCatalog(bundled, "Bundled klicensee catalog");
        }
        if (license == 3) return new(NpdKeys.KlicFree, "Built-in free developer key");
        string path = RapStore.PathFor(contentId, rapDirectory);
        if (File.Exists(path)) return ReadRap(path, "RAP library");
        throw new PkgKeyException($"Missing key for {contentId} ({(license == 1 ? "Network" : "Local")} license). No unambiguous catalog mapping or installed RAP was found. Select its RAP or enter its raw content klicensee. Package decryption keys cannot substitute for this key.");
    }

    private static ResolvedEdatKey FromCatalog(KlicenseeResolution resolution, string source)
    {
        var entry = resolution.Entry;
        string mapping = entry.ContentId ?? entry.TitleId;
        if (entry.FileName is not null) mapping += " / " + entry.FileName;
        return new(resolution.Klicensee, $"{source}: {mapping}");
    }

    private static ResolvedEdatKey ReadRap(string path, string source)
    {
        using var file = File.OpenRead(path);
        if (file.Length != 16) throw new ArgumentException($"Invalid RAP '{Path.GetFileName(path)}': expected 16 bytes, found {file.Length}. Select a valid RAP for this content.");
        byte[] rap = new byte[16];
        file.ReadExactly(rap);
        return new(NpdKeys.RapToKlicensee(rap), $"{source}: {Path.GetFileName(path)}");
    }

    private sealed class CountingSink : Stream
    {
        private long _count;
        public override void Write(byte[] buffer, int offset, int count) => _count += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _count += buffer.Length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _count;
        public override long Position { get => _count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
