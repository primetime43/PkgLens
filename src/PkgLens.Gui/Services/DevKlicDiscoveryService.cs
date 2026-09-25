using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using PkgLens.Core;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Ps3.Self;

namespace PkgLens.Gui.Services;

public sealed record DevKlicProgress(string Phase, double Percent, long CandidatesTested);

/// <summary>Owns one confirmed key; display metadata never contains the raw key.</summary>
public sealed class DevKlicMatch : IDisposable
{
    private readonly byte[] _key;
    private readonly string _target;
    private bool _disposed;
    internal DevKlicMatch(byte[] key, string target, NpdInfo info, int offset, string representation)
    {
        _key = (byte[])key.Clone(); _target = target; ContentId = info.ContentId; License = info.License;
        FileName = Path.GetFileName(target); ElfOffset = offset; Representation = representation;
        Fingerprint = Convert.ToHexString(SHA256.HashData(_key))[..12];
    }
    public string ContentId { get; }
    public string FileName { get; }
    public int License { get; }
    public int ElfOffset { get; }
    public string Representation { get; }
    public string Fingerprint { get; }
    public string Description => $"Confirmed against the EDAT header and content.\n{ContentId} / {FileName}\n{Representation} at decrypted ELF offset 0x{ElfOffset:X}\nKey fingerprint: {Fingerprint}";

    public void Save(string? databasePath = null, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Recheck on save in case the target changed after discovery.
        using var input = File.OpenRead(_target);
        var info = EdatFile.ParseHeader(input);
        if (info.IsSdat || info.ContentId != ContentId || info.License != License)
            throw new IOException("The target EDAT changed. Search again before saving a key.");
        DevKlicDiscoveryService.Verify(_target, _key, token);
        token.ThrowIfCancellationRequested();
        KlicenseeStore.Install(ContentId, FileName, (uint)License, _key,
            "verified executable discovery", databasePath);
    }

    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(_key);
        _disposed = true;
    }
}

public static class DevKlicDiscoveryService
{
    public const long MaxExecutableBytes = 64L * 1024 * 1024;

    public static DevKlicMatch? Discover(string executablePath, string edatPath,
        EdatKeySelection executableKey, string? rapDirectory = null, string? databasePath = null,
        CancellationToken token = default, IProgress<DevKlicProgress>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        using var target = File.OpenRead(edatPath);
        var info = EdatFile.ParseHeader(target);
        if (info.IsSdat) throw new PkgFormatException("SDAT uses a header-derived key. Use Check key and file; executable discovery is unnecessary.");
        if ((info.Flags & 0x80000000) != 0) throw new PkgFormatException("Debug EDAT has no authenticated header, so it cannot confirm discovered keys.");
        if (info.Version is < 0 or > 4) throw new PkgFormatException("This EDAT version is not supported for discovery.");
        byte[] header = new byte[0xB0];
        target.Position = 0;
        target.ReadExactly(header);
        using var probe = new MemoryStream(header, writable: false);
        byte[]? executable = null, elf = null;
        try
        {
            progress?.Report(new("Reading executable", 0, 0));
            using (var input = File.OpenRead(executablePath))
            {
                if (input.Length > MaxExecutableBytes) throw new IOException("Executable discovery supports executable inputs up to 64 MiB.");
                executable = new byte[checked((int)input.Length)];
                input.ReadExactly(executable);
            }
            token.ThrowIfCancellationRequested();
            if (executable.AsSpan().StartsWith("SCE\0"u8))
            {
                progress?.Report(new("Decrypting SELF with available keys", 0, 0));
                var selfInfo = SelfReader.ParseInfo(new MemoryStream(executable));
                try
                {
                    byte[]? key = null;
                    ushort revision = BinaryPrimitives.ReadUInt16BigEndian(executable.AsSpan(8));
                    if ((revision & 0x8000) == 0 && selfInfo.Npdrm is { } npd)
                        key = EdatKeyValidationService.Resolve(npd.ContentId, Path.GetFileName(executablePath),
                            false, (int)npd.RawLicenseType, executableKey, rapDirectory, databasePath).Key;
                    elf = SelfDecryptor.Decrypt(executable, key).Elf;
                }
                catch (PkgKeyException ex) { throw new PkgKeyException("Cannot decrypt the executable. Supply its own RAP or raw klicensee. " + ex.Message); }
            }
            else elf = executable;
            token.ThrowIfCancellationRequested();
            if (elf.Length < 64 || !elf.AsSpan().StartsWith("\x7f"u8) ||
                !elf.AsSpan(1, 3).SequenceEqual("ELF"u8) || elf[4] != 2 || elf[5] != 2 ||
                BinaryPrimitives.ReadUInt16BigEndian(elf.AsSpan(18)) != 21)
                throw new PkgFormatException("Choose a PS3 EBOOT/SELF/SPRX or a decrypted 64-bit big-endian PowerPC ELF.");
            if (elf.Length > MaxExecutableBytes) throw new IOException("The decrypted executable exceeds the 64 MiB discovery limit.");

            // Bounded deduplication avoids repeatedly testing padding without retaining a key pool.
            var seen = new HashSet<(ulong, ulong)>();
            long tested = 0;
            long lastProgress = Environment.TickCount64;
            void ReportScan(string phase, double percent)
            {
                if (Environment.TickCount64 - lastProgress < 100) return;
                lastProgress = Environment.TickCount64;
                progress?.Report(new(phase, percent, tested));
            }
            byte[] candidate = new byte[16];
            DevKlicMatch? TryCandidate(int offset, string representation)
            {
                token.ThrowIfCancellationRequested();
                var identity = (BinaryPrimitives.ReadUInt64LittleEndian(candidate), BinaryPrimitives.ReadUInt64LittleEndian(candidate.AsSpan(8)));
                if (!seen.Add(identity)) return null;
                if (seen.Count >= 65536) seen.Clear();
                tested++;
                if (EdatFile.AuthenticateHeader(probe, candidate) != true) return null;
                progress?.Report(new("Verifying matched key against complete EDAT", 99, tested));
                Verify(edatPath, candidate, token);
                return new DevKlicMatch(candidate, edatPath, info, offset, representation);
            }

            // Text keys are inexpensive to locate; scan every offset before testing binary windows.
            progress?.Report(new("Searching hexadecimal strings", 0, tested));
            for (int offset = 0; offset <= elf.Length - 32; offset++)
            {
                if ((offset & 4095) == 0) { token.ThrowIfCancellationRequested(); ReportScan("Searching hexadecimal strings", offset * 10d / elf.Length); }
                bool hex = true;
                for (int i = 0; i < 16; i++)
                {
                    int high = Hex(elf[offset + i * 2]), low = Hex(elf[offset + i * 2 + 1]);
                    if (high < 0 || low < 0) { hex = false; break; }
                    candidate[i] = (byte)((high << 4) | low);
                }
                if (hex && TryCandidate(offset, "Hexadecimal string") is { } match) return match;
            }
            // Prefer aligned constants, then cover every remaining byte offset without heuristics.
            progress?.Report(new("Searching raw 16-byte values", 10, tested));
            for (int pass = 0; pass < 16; pass++)
            for (int offset = pass; offset <= elf.Length - 16; offset += 16)
            {
                if (((offset - pass) & 4095) == 0)
                {
                    token.ThrowIfCancellationRequested();
                    ReportScan("Searching raw 16-byte values", 10 + 89d * (pass + offset / (double)elf.Length) / 16);
                }
                elf.AsSpan(offset, 16).CopyTo(candidate);
                if (TryCandidate(offset, "Raw 16-byte value") is { } match) return match;
            }
            token.ThrowIfCancellationRequested();
            progress?.Report(new("Search complete — no confirmed match", 100, tested));
            return null;
        }
        finally
        {
            if (elf is not null) CryptographicOperations.ZeroMemory(elf);
            if (executable is not null && !ReferenceEquals(executable, elf)) CryptographicOperations.ZeroMemory(executable);
        }
    }

    internal static void Verify(string path, byte[] key, CancellationToken token)
    {
        var result = EdatKeyValidationService.Check(path, new(Convert.ToHexString(key)), token: token);
        if (result.Status != EdatKeyCheckStatus.Verified)
            throw new PkgFormatException("Candidate could not be confirmed; no key was saved. " + result.Message);
    }

    private static int Hex(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
        _ => -1,
    };
}
