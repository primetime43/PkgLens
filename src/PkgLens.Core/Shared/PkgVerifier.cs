using PkgLens.Core.Ps3;
using System.Security.Cryptography;
using PkgLens.Core.Shared.Crypto;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;

namespace PkgLens.Core.Shared;

public enum PkgCheckStatus { Pass, Fail, Skipped }

/// <summary>One integrity check and its outcome.</summary>
public sealed record PkgCheck(string Name, PkgCheckStatus Status, string Detail);

/// <summary>The result of verifying a package: an ordered list of checks.</summary>
public sealed class PkgVerificationReport
{
    public IReadOnlyList<PkgCheck> Checks { get; }
    public PkgVerificationReport(IReadOnlyList<PkgCheck> checks) => Checks = checks;

    public int Failures => Checks.Count(c => c.Status == PkgCheckStatus.Fail);
    public bool Passed => Failures == 0;
}

/// <summary>
/// Verifies a PS3 package's integrity. Checks:
/// <list type="bullet">
///   <item>structural bounds — magic, total_size vs file length, data/metadata region ranges,</item>
///   <item>header SHA-1 digest — last 8 bytes of <c>SHA1(header[0x00:0x80])</c> at 0xB8 (no key),</item>
///   <item>header CMAC — <c>AES-CMAC(gpkg_key, header[0x00:0x80])</c> at 0x80 (retail; needs the key),</item>
///   <item>header ECDSA — the NPDRM signature at 0x90 verified against Sony's public key (no key needed),</item>
///   <item>item table — decrypts and all entry offsets/sizes lie within the data region.</item>
/// </list>
/// The ECDSA check uses Sony's <em>public</em> NPDRM key (see <see cref="NpdrmSignature"/>) — it can
/// tell a genuine retail signature from a repacked/fake-signed one, but never forges or re-signs.
/// </summary>
public static class PkgVerifier
{
    public static PkgVerificationReport Verify(Stream source, IKeyProvider keys)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(keys);

        var checks = new List<PkgCheck>();
        long fileLen = source.Length;

        int want = (int)Math.Min(0xC0, fileLen);
        var head = new byte[want];
        source.Position = 0;
        if (want > 0) source.ReadExactly(head, 0, want);

        PkgHeader header;
        try
        {
            header = PkgHeader.Parse(head);
        }
        catch (PkgFormatException ex)
        {
            checks.Add(new PkgCheck("Header", PkgCheckStatus.Fail, ex.Message));
            return new PkgVerificationReport(checks);
        }

        checks.Add(new PkgCheck("Magic", PkgCheckStatus.Pass, "0x7F504B47 (.PKG)"));

        // Total size vs actual file length.
        long total = (long)header.TotalSize;
        if (total > fileLen)
            checks.Add(new PkgCheck("Total size", PkgCheckStatus.Fail,
                $"header claims {total:n0} bytes but the file is only {fileLen:n0} (truncated)"));
        else if (total < fileLen)
            checks.Add(new PkgCheck("Total size", PkgCheckStatus.Pass,
                $"{total:n0} bytes ({fileLen - total:n0} trailing bytes present)"));
        else
            checks.Add(new PkgCheck("Total size", PkgCheckStatus.Pass, $"{total:n0} bytes == file length"));

        // Data region. data_offset/data_size are untrusted u64s — compare in ulong with a
        // subtraction guard so a huge value can't overflow a signed cast and read as in-range.
        if (header.DataOffset > (ulong)fileLen || header.DataSize > (ulong)fileLen - header.DataOffset)
            checks.Add(new PkgCheck("Data region", PkgCheckStatus.Fail, "extends past the end of the file (truncated)"));
        else
            checks.Add(new PkgCheck("Data region", PkgCheckStatus.Pass,
                $"offset 0x{header.DataOffset:X}, size {header.DataSize:n0}"));

        // Metadata block must sit before the data region (widen to ulong before the u32+u32 add).
        if ((ulong)header.MetadataOffset + header.MetadataSize > header.DataOffset)
            checks.Add(new PkgCheck("Metadata block", PkgCheckStatus.Fail, "overruns into the data region"));
        else
            checks.Add(new PkgCheck("Metadata block", PkgCheckStatus.Pass, $"{header.MetadataCount} entries"));

        // Header SHA-1 digest (last 8 bytes of SHA1 over the first 0x80 header bytes).
        if (want >= 0xC0)
        {
            Span<byte> sha = stackalloc byte[20];
            SHA1.HashData(head.AsSpan(0, 0x80), sha);
            var shaField = head.AsSpan(0xB8, 0x08);
            if (AllZero(shaField))
                checks.Add(new PkgCheck("Header SHA-1", PkgCheckStatus.Skipped, "no digest present"));
            else if (sha.Slice(12, 8).SequenceEqual(shaField))
                checks.Add(new PkgCheck("Header SHA-1", PkgCheckStatus.Pass, "matches"));
            else
                checks.Add(new PkgCheck("Header SHA-1", PkgCheckStatus.Fail, "mismatch — header corrupted or altered"));

            // Header CMAC (PS3 retail only; needs the gpkg key). PSP/PSVita use a different scheme.
            var cmacField = head.AsSpan(0x80, 0x10);
            if (header.Finalization != PkgFinalization.Retail)
                checks.Add(new PkgCheck("Header CMAC", PkgCheckStatus.Skipped, "non-finalized (debug) package"));
            else if (!header.IsPs3)
                checks.Add(new PkgCheck("Header CMAC", PkgCheckStatus.Skipped, "PSP/PSVita package (gpkg CMAC not applicable)"));
            else if (AllZero(cmacField))
                checks.Add(new PkgCheck("Header CMAC", PkgCheckStatus.Skipped, "no CMAC present"));
            else if (!keys.TryResolve(header, out var ctx, out string? reason) || !ctx.TryGetHeaderCmacKey(out var macKey))
                checks.Add(new PkgCheck("Header CMAC", PkgCheckStatus.Skipped, reason ?? "gpkg key unavailable"));
            else
            {
                var mac = AesCmac.Compute(macKey, head.AsSpan(0, 0x80));
                checks.Add(mac.AsSpan().SequenceEqual(cmacField)
                    ? new PkgCheck("Header CMAC", PkgCheckStatus.Pass, "matches (AES-CMAC with gpkg key)")
                    : new PkgCheck("Header CMAC", PkgCheckStatus.Fail, "mismatch — header tampered or corrupted"));
            }

            // Header ECDSA (NPDRM) signature — public-key verification, no secret involved. A fake-signed
            // or homebrew package leaves this blank (Skipped); a repacked/altered retail package fails it.
            checks.Add(NpdrmSignature.VerifyHeader(head) switch
            {
                PkgSignatureResult.Valid => new PkgCheck("Header ECDSA", PkgCheckStatus.Pass,
                    "valid — genuine retail signature (Sony's NPDRM key)"),
                PkgSignatureResult.Invalid => new PkgCheck("Header ECDSA", PkgCheckStatus.Fail,
                    "signature present but does not verify — repacked, fake-signed, or altered"),
                PkgSignatureResult.Unsupported => new PkgCheck("Header ECDSA", PkgCheckStatus.Skipped,
                    "explicit-curve ECDSA unavailable on this platform"),
                _ => new PkgCheck("Header ECDSA", PkgCheckStatus.Skipped,
                    "no signature present (fake-signed / homebrew)"),
            });
        }

        // Item table: decrypt and confirm every entry lies within the data region. The reader only
        // validates ranges lazily (at extraction), so the verifier checks each entry itself here —
        // otherwise the "all offsets in range" claim would be asserted without ever being tested.
        try
        {
            var info = PkgReader.Read(source, keys);
            if (!info.IsDecrypted)
                checks.Add(new PkgCheck("Item table", PkgCheckStatus.Skipped, "not decrypted (no key)"));
            else
            {
                PkgEntry? bad = null;
                foreach (var e in info.Entries)
                {
                    if (e.IsDirectory || e.FileSize == 0) continue;
                    // Subtraction guard: FileOffset/FileSize are untrusted u64s.
                    if (e.FileOffset > header.DataSize || e.FileSize > header.DataSize - e.FileOffset)
                    {
                        bad = e;
                        break;
                    }
                }
                checks.Add(bad is null
                    ? new PkgCheck("Item table", PkgCheckStatus.Pass,
                        $"{info.FileCount} files, {info.DirectoryCount} directories — all offsets in range")
                    : new PkgCheck("Item table", PkgCheckStatus.Fail,
                        $"entry '{bad.Name}' data range [0x{bad.FileOffset:X}, +0x{bad.FileSize:X}) exceeds data_size 0x{header.DataSize:X}"));
            }
        }
        catch (PkgFormatException ex)
        {
            checks.Add(new PkgCheck("Item table", PkgCheckStatus.Fail, ex.Message));
        }

        return new PkgVerificationReport(checks);
    }

    private static bool AllZero(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) if (b != 0) return false;
        return true;
    }
}
