using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using PkgLens.Core.Ps3.Npd;

namespace PkgLens.Core.Ps3.Self;

/// <summary>The outcome of decrypting a SELF back to its plaintext ELF.</summary>
public sealed class SelfDecryptResult
{
    public required byte[] Elf { get; init; }
    /// <summary>The NPDRM license type of the source SELF, if it carried an NPDRM control block.</summary>
    public NpdrmLicenseType? License { get; init; }
    public string? ContentId { get; init; }
    public ushort KeyRevision { get; init; }
    public bool WasNpdrm { get; init; }
}

/// <summary>
/// Decrypts a retail / NPDRM SELF (<c>EBOOT.BIN</c>, <c>.self</c>, <c>.sprx</c>) back to its plaintext
/// ELF — the inverse of signing, needed when you only have the encrypted executable and want to
/// inspect it or fake-sign it (<see cref="SelfBuilder"/>). Ported from RPCS3's
/// <c>unself.cpp</c> (<c>SELFDecrypter::LoadMetadata / DecryptNPDRM / DecryptData / MakeElf</c>):
/// the NPDRM klicensee layer, the AES-256-CBC metadata-info decrypt, the AES-128-CTR metadata-header
/// and per-section decrypt, and the ELF rebuild. Uses only public decryption keys
/// (<see cref="SelfKeyset"/>); no signing keys, and it produces a plaintext ELF, not a resigned file.
/// Debug (non-finalized) SELFs decrypt with no keys at all.
/// </summary>
public static class SelfDecryptor
{
    private const uint SceMagic = 0x53434500;
    private const int SceHeaderLen = 0x20;
    private const int MetadataInfoLen = 0x40;
    private const int MetadataHeaderLen = 0x20;
    private const int MetadataSectionHeaderLen = 0x30;

    /// <summary>Decrypts the SELF in <paramref name="self"/> to a plaintext ELF.</summary>
    /// <param name="self">The SELF file bytes (EBOOT.BIN etc.).</param>
    /// <param name="klicensee">
    /// Optional 16-byte NPDRM klicensee override (from a RAP via <see cref="NpdKeys.RapToKlicensee"/>).
    /// For free-license SELFs none is needed; for local/network licenses this must be supplied.
    /// </param>
    public static SelfDecryptResult Decrypt(byte[] self, byte[]? klicensee = null)
    {
        ArgumentNullException.ThrowIfNull(self);
        if (self.Length < SceHeaderLen || BinaryPrimitives.ReadUInt32BigEndian(self) != SceMagic)
            throw new PkgFormatException("Not a SELF/SCE file: missing 'SCE\\0' magic at offset 0.");

        // ---- SCE header ----
        ushort keyRevision = BinaryPrimitives.ReadUInt16BigEndian(self.AsSpan(0x08));
        ushort seType = BinaryPrimitives.ReadUInt16BigEndian(self.AsSpan(0x0A));
        uint seMeta = BinaryPrimitives.ReadUInt32BigEndian(self.AsSpan(0x0C));
        ulong seHsize = BinaryPrimitives.ReadUInt64BigEndian(self.AsSpan(0x10));

        if (seType != 1)
            throw new PkgFormatException($"SCE file is category 0x{seType:X} (not a SELF).");

        // ---- SELF extended header (at 0x20) ----
        ulong progIdOffset = ReadU64(self, 0x28);
        ulong ehdrOffset = ReadU64(self, 0x30);
        ulong phdrOffset = ReadU64(self, 0x38);
        ulong shdrOffset = ReadU64(self, 0x40);
        ulong sectionInfoOffset = ReadU64(self, 0x48);
        ulong supplOffset = ReadU64(self, 0x58);
        ulong supplSize = ReadU64(self, 0x60);

        // ---- app_info (program identification header) ----
        RequireWithin((long)progIdOffset, 0x10, self.Length, "app_info header");
        uint programType = BinaryPrimitives.ReadUInt32BigEndian(self.AsSpan((int)progIdOffset + 0x0C));

        // ---- embedded plaintext ELF header + program headers ----
        RequireWithin((long)ehdrOffset, 0x40, self.Length, "ELF header");
        if (self[(int)ehdrOffset + 4] != 2 || self[(int)ehdrOffset + 5] != 2)
            throw new PkgFormatException("Only 64-bit big-endian PS3 ELF SELFs are supported.");
        int ePhnum = BinaryPrimitives.ReadUInt16BigEndian(self.AsSpan((int)ehdrOffset + 0x38));
        int eShnum = BinaryPrimitives.ReadUInt16BigEndian(self.AsSpan((int)ehdrOffset + 0x3C));
        ulong eShoff = BinaryPrimitives.ReadUInt64BigEndian(self.AsSpan((int)ehdrOffset + 0x28));

        // ---- NPDRM control info (license type + content id) ----
        var npd = FindNpdrm(self, supplOffset, supplSize);

        // ---- Debug / fake-signed SELF (key_version 0x80 or 0xC0) ----
        // These carry the ELF appended in the clear after the header — no metadata to decrypt. Our own
        // fSELF (key revision 0x8000 big-endian = bytes 80 00 = 0x80 little-endian) is exactly this
        // format, as are DEX/debug EBOOTs. RPCS3's CheckDebugSelf: read the ELF start from se_hsize
        // (@0x10, big-endian for 0x80 / little-endian for 0xC0) and copy from there to end of file.
        int keyVersionLe = self[0x08] | (self[0x09] << 8);
        if (keyVersionLe is 0x80 or 0xC0)
        {
            ulong elfStart = keyVersionLe == 0x80
                ? BinaryPrimitives.ReadUInt64BigEndian(self.AsSpan(0x10))
                : BinaryPrimitives.ReadUInt64LittleEndian(self.AsSpan(0x10));
            if (elfStart >= (ulong)self.Length)
                throw new PkgFormatException("Debug/fake-signed SELF: the ELF offset is past end of file.");

            byte[] plainElf;
            if (keyVersionLe == 0x80 && HasCompressedSegments(
                    self, sectionInfoOffset, ePhnum))
            {
                plainElf = RebuildCompressedFakeSelf(self, ehdrOffset, phdrOffset, shdrOffset,
                    sectionInfoOffset, ePhnum, eShnum, eShoff);
            }
            else if (self.Length - (int)elfStart >= 4 &&
                     BinaryPrimitives.ReadUInt32BigEndian(self.AsSpan((int)elfStart)) == 0x7F454C46)
            {
                plainElf = self[(int)elfStart..];
            }
            else
            {
                throw new PkgFormatException("Debug/fake-signed SELF: no ELF found at the appended offset and no compressed segment layout was detected.");
            }

            return new SelfDecryptResult
            {
                Elf = plainElf,
                KeyRevision = keyRevision,
                WasNpdrm = npd is not null,
                License = npd?.License,
                ContentId = npd?.ContentId,
            };
        }

        bool isDebug = (keyRevision & 0x8000) == 0x8000;

        // ---- metadata region ----
        int metaInfoPos = (int)seMeta + SceHeaderLen;
        int metaHeadersPos = metaInfoPos + MetadataInfoLen;
        int metaHeadersLen = (int)seHsize - metaHeadersPos;
        if (metaHeadersLen < MetadataHeaderLen || metaHeadersPos + metaHeadersLen > self.Length)
            throw new PkgFormatException("SELF metadata region is out of bounds or truncated.");

        var metaInfo = self.AsSpan(metaInfoPos, MetadataInfoLen).ToArray();
        var metaHeaders = self.AsSpan(metaHeadersPos, metaHeadersLen).ToArray();

        if (!isDebug)
        {
            SelfKey keyset = SelfKeyset.Find(programType, keyRevision)
                ?? throw new PkgFormatException(
                    $"No SELF keyset for program type 0x{programType:X} key revision 0x{keyRevision:X4}. " +
                    "This firmware/app revision is not supported for decryption.");

            // NPDRM layer (removed first), then the appldr/NPDRM keyset AES-256-CBC layer.
            DecryptNpdrmLayer(metaInfo, npd, klicensee);
            AesCbcDecrypt(keyset.Erk, keyset.Riv, metaInfo);
        }

        // If padding didn't clear, the key/klic/RAP was wrong.
        if (metaInfo[0x10] != 0x00 || metaInfo[0x30] != 0x00)
        {
            string licenseHint = npd?.RawLicense == 3 && klicensee is null
                ? "This free-license SELF may require a title-specific raw klicensee override."
                : klicensee is not null
                    ? "The supplied raw klicensee may be incorrect for this SELF."
                    : "A licensed NPDRM SELF may need its RAP or raw klicensee.";
            throw new PkgFormatException(
                $"Failed to decrypt SELF metadata — wrong key revision or license key. {licenseHint}");
        }

        byte[] metaKey = metaInfo[0x00..0x10];
        byte[] metaIv = metaInfo[0x20..0x30];

        // AES-128-CTR over the metadata header + section headers + data keys.
        AesCtr(metaKey, metaIv, metaHeaders, 0, metaHeaders.Length);

        // Read the counts as long so the offset math below can't overflow int before it's bounds-checked;
        // the check then guarantees both fit the (few-KB) metadata-header buffer.
        long sectionCountL = BinaryPrimitives.ReadUInt32BigEndian(metaHeaders.AsSpan(0x0C));
        long keyCountL = BinaryPrimitives.ReadUInt32BigEndian(metaHeaders.AsSpan(0x10));

        int shdrBase = MetadataHeaderLen;
        long dataKeysOffsetL = shdrBase + sectionCountL * MetadataSectionHeaderLen;
        if (dataKeysOffsetL + keyCountL * 0x10 > metaHeaders.Length)
            throw new PkgFormatException("SELF metadata headers are truncated.");

        int sectionCount = (int)sectionCountL;
        int keyCount = (int)keyCountL;
        int dataKeysOffset = (int)dataKeysOffsetL;

        var sections = new MetaSection[sectionCount];
        for (int i = 0; i < sectionCount; i++)
        {
            var s = metaHeaders.AsSpan(shdrBase + i * MetadataSectionHeaderLen);
            sections[i] = new MetaSection
            {
                DataOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(s),
                DataSize = (long)BinaryPrimitives.ReadUInt64BigEndian(s[0x08..]),
                Type = BinaryPrimitives.ReadUInt32BigEndian(s[0x10..]),
                ProgramIdx = (int)BinaryPrimitives.ReadUInt32BigEndian(s[0x14..]),
                Encrypted = BinaryPrimitives.ReadUInt32BigEndian(s[0x20..]),
                KeyIdx = (int)BinaryPrimitives.ReadUInt32BigEndian(s[0x24..]),
                IvIdx = (int)BinaryPrimitives.ReadUInt32BigEndian(s[0x28..]),
                Compressed = BinaryPrimitives.ReadUInt32BigEndian(s[0x2C..]),
            };
        }

        // ---- DecryptData: per-section AES-128-CTR into one contiguous buffer ----
        var dataBuf = new MemoryStream();
        foreach (var s in sections)
        {
            // RPCS3's DecryptData buffer contains every metadata section in table order. Plaintext
            // and auxiliary sections must remain in the buffer too, otherwise every later PHDR
            // section is read from the wrong position while rebuilding the ELF.
            RequireWithin(s.DataOffset, s.DataSize, self.Length, "section data");
            var buf = self.AsSpan((int)s.DataOffset, (int)s.DataSize).ToArray();
            if (s.Encrypted == 3)
            {
                if ((uint)s.KeyIdx >= (uint)keyCount || (uint)s.IvIdx >= (uint)keyCount)
                    throw new PkgFormatException("SELF encrypted section references a metadata key outside the key table.");
                byte[] dataKey = metaHeaders[(dataKeysOffset + s.KeyIdx * 0x10)..(dataKeysOffset + s.KeyIdx * 0x10 + 0x10)];
                byte[] dataIv = metaHeaders[(dataKeysOffset + s.IvIdx * 0x10)..(dataKeysOffset + s.IvIdx * 0x10 + 0x10)];
                AesCtr(dataKey, dataIv, buf, 0, buf.Length);
            }
            dataBuf.Write(buf, 0, buf.Length);
        }
        byte[] data = dataBuf.ToArray();

        // ---- MakeElf: rebuild the plaintext ELF ----
        byte[] elf = WriteElf(self, (int)ehdrOffset, (int)phdrOffset, (int)shdrOffset,
            ePhnum, eShnum, (long)eShoff, sections, data);
        if (elf.Length < 4 || BinaryPrimitives.ReadUInt32BigEndian(elf) != 0x7F454C46)
            throw new PkgFormatException(
                "SELF data decrypted, but the reconstructed output does not have a valid ELF header. " +
                "The klicensee may be incorrect or the section layout may be unsupported.");

        return new SelfDecryptResult
        {
            Elf = elf,
            KeyRevision = keyRevision,
            WasNpdrm = npd is not null,
            License = npd?.License,
            ContentId = npd?.ContentId,
        };
    }

    private static bool HasCompressedSegments(byte[] self, ulong sectionInfoOffset, int segmentCount)
    {
        const int SectionInfoLen = 0x20;
        if (sectionInfoOffset == 0 || sectionInfoOffset > int.MaxValue || segmentCount <= 0)
            return false;
        long tableLength = (long)segmentCount * SectionInfoLen;
        if ((long)sectionInfoOffset + tableLength > self.LongLength)
            return false;
        for (int i = 0; i < segmentCount; i++)
        {
            int entry = (int)sectionInfoOffset + i * SectionInfoLen;
            if (BinaryPrimitives.ReadUInt32BigEndian(self.AsSpan(entry + 0x10)) == 2)
                return true;
        }
        return false;
    }

    private static byte[] RebuildCompressedFakeSelf(byte[] self, ulong ehdrOffset,
        ulong phdrOffset, ulong shdrOffset, ulong sectionInfoOffset, int phnum, int shnum,
        ulong eShoff)
    {
        const int EhdrLen = 0x40, PhdrLen = 0x38, ShdrLen = 0x40, SectionInfoLen = 0x20;
        RequireWithin((long)ehdrOffset, EhdrLen, self.Length, "embedded ELF header");
        RequireWithin((long)phdrOffset, (long)phnum * PhdrLen, self.Length,
            "embedded ELF program headers");
        RequireWithin((long)sectionInfoOffset, (long)phnum * SectionInfoLen, self.Length,
            "fake-signed SELF section-info table");

        int ehdr = (int)ehdrOffset;
        ulong ePhoffValue = BinaryPrimitives.ReadUInt64BigEndian(self.AsSpan(ehdr + 0x20));
        ushort ePhentsize = BinaryPrimitives.ReadUInt16BigEndian(self.AsSpan(ehdr + 0x36));
        ushort eShentsize = BinaryPrimitives.ReadUInt16BigEndian(self.AsSpan(ehdr + 0x3A));
        if (ePhentsize != PhdrLen)
            throw new PkgFormatException($"Compressed fSELF has ELF program-header size 0x{ePhentsize:X}; expected 0x{PhdrLen:X}.");
        if (shnum > 0 && eShentsize != ShdrLen)
            throw new PkgFormatException($"Compressed fSELF has ELF section-header size 0x{eShentsize:X}; expected 0x{ShdrLen:X}.");
        if (ePhoffValue > (ulong)MaxElfSize || eShoff > (ulong)MaxElfSize)
            throw new PkgFormatException("Compressed fSELF ELF header offsets exceed the supported size limit.");

        long outputLength = Math.Max(EhdrLen, checked((long)ePhoffValue + (long)phnum * PhdrLen));
        var placements = new List<(long TargetOffset, int TargetSize, int SourceOffset,
            int SourceSize, bool Compressed)>(phnum);
        for (int i = 0; i < phnum; i++)
        {
            int program = (int)phdrOffset + i * PhdrLen;
            ulong targetOffsetValue = BinaryPrimitives.ReadUInt64BigEndian(self.AsSpan(program + 0x08));
            ulong targetSizeValue = BinaryPrimitives.ReadUInt64BigEndian(self.AsSpan(program + 0x20));
            int section = (int)sectionInfoOffset + i * SectionInfoLen;
            ulong sourceOffsetValue = BinaryPrimitives.ReadUInt64BigEndian(self.AsSpan(section + 0x00));
            ulong sourceSizeValue = BinaryPrimitives.ReadUInt64BigEndian(self.AsSpan(section + 0x08));
            uint compression = BinaryPrimitives.ReadUInt32BigEndian(self.AsSpan(section + 0x10));
            if (targetOffsetValue > (ulong)MaxElfSize || targetSizeValue > int.MaxValue ||
                sourceOffsetValue > int.MaxValue || sourceSizeValue > int.MaxValue)
                throw new PkgFormatException($"Compressed fSELF segment {i} exceeds the supported size limit.");

            long targetOffset = (long)targetOffsetValue;
            int targetSize = (int)targetSizeValue;
            int sourceOffset = (int)sourceOffsetValue;
            int sourceSize = (int)sourceSizeValue;
            RequireWithin(targetOffset, targetSize, MaxElfSize, $"compressed fSELF segment {i} placement");
            RequireWithin(sourceOffset, sourceSize, self.Length, $"compressed fSELF segment {i} data");
            if (compression is not 1 and not 2)
                throw new PkgFormatException($"Compressed fSELF segment {i} has unsupported compression flag {compression}.");
            if (compression == 1 && sourceSize != targetSize)
                throw new PkgFormatException($"Plain fSELF segment {i} stores {sourceSize:n0} bytes but its ELF size is {targetSize:n0} bytes.");

            placements.Add((targetOffset, targetSize, sourceOffset, sourceSize, compression == 2));
            outputLength = Math.Max(outputLength, targetOffset + targetSize);
        }

        long sectionHeadersLength = (long)shnum * ShdrLen;
        if (sectionHeadersLength > 0)
        {
            RequireWithin((long)shdrOffset, sectionHeadersLength, self.Length,
                "compressed fSELF section headers");
            RequireWithin((long)eShoff, sectionHeadersLength, MaxElfSize,
                "compressed fSELF section-header placement");
            outputLength = Math.Max(outputLength, (long)eShoff + sectionHeadersLength);
        }

        var elf = new byte[checked((int)outputLength)];
        self.AsSpan((int)ehdrOffset, EhdrLen).CopyTo(elf);
        self.AsSpan((int)phdrOffset, phnum * PhdrLen).CopyTo(elf.AsSpan((int)ePhoffValue));
        for (int i = 0; i < placements.Count; i++)
        {
            var placement = placements[i];
            Span<byte> destination = elf.AsSpan((int)placement.TargetOffset, placement.TargetSize);
            if (!placement.Compressed)
            {
                self.AsSpan(placement.SourceOffset, placement.SourceSize).CopyTo(destination);
                continue;
            }

            try
            {
                using var input = new MemoryStream(self, placement.SourceOffset,
                    placement.SourceSize, writable: false);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                zlib.ReadExactly(destination);
                if (zlib.ReadByte() != -1)
                    throw new PkgFormatException($"Compressed fSELF segment {i} expands beyond its ELF size.");
            }
            catch (EndOfStreamException ex)
            {
                throw new PkgFormatException($"Compressed fSELF segment {i} ended before its ELF size was restored.", ex);
            }
            catch (InvalidDataException ex)
            {
                throw new PkgFormatException($"Compressed fSELF segment {i} contains invalid zlib data.", ex);
            }
        }

        if (sectionHeadersLength > 0)
            self.AsSpan((int)shdrOffset, (int)sectionHeadersLength)
                .CopyTo(elf.AsSpan((int)eShoff));
        return elf;
    }

    private readonly struct MetaSection
    {
        public long DataOffset { get; init; }
        public long DataSize { get; init; }
        public uint Type { get; init; }
        public int ProgramIdx { get; init; }
        public uint Encrypted { get; init; }
        public int KeyIdx { get; init; }
        public int IvIdx { get; init; }
        public uint Compressed { get; init; }
    }

    /// <summary>The NPDRM control block, if the SELF carries one.</summary>
    private sealed class NpdInfo
    {
        public NpdrmLicenseType? License { get; init; }
        public uint RawLicense { get; init; }
        public string ContentId { get; init; } = string.Empty;
    }

    private static NpdInfo? FindNpdrm(byte[] self, ulong offset, ulong size)
    {
        if (offset == 0 || size == 0 || offset + size > (ulong)self.Length) return null;
        int pos = (int)offset;
        int end = (int)(offset + size);
        while (pos + 0x10 <= end)
        {
            uint type = BinaryPrimitives.ReadUInt32BigEndian(self.AsSpan(pos));
            uint blockSize = BinaryPrimitives.ReadUInt32BigEndian(self.AsSpan(pos + 0x04));
            if (blockSize < 0x10 || pos + (int)blockSize > end) break;

            if (type == 3) // NPDRM: sub-header (0x10) then NPD_HEADER
            {
                int npd = pos + 0x10;
                if (npd + 0x50 <= self.Length &&
                    BinaryPrimitives.ReadUInt32BigEndian(self.AsSpan(npd)) == 0x4E504400) // "NPD\0"
                {
                    uint lic = BinaryPrimitives.ReadUInt32BigEndian(self.AsSpan(npd + 0x08));
                    return new NpdInfo
                    {
                        RawLicense = lic,
                        License = Enum.IsDefined(typeof(NpdrmLicenseType), lic) ? (NpdrmLicenseType)lic : null,
                        ContentId = System.Text.Encoding.ASCII.GetString(self, npd + 0x10, 0x30).TrimEnd('\0'),
                    };
                }
            }
            pos += (int)blockSize;
        }
        return null;
    }

    private static void DecryptNpdrmLayer(byte[] metadataInfo, NpdInfo? npd, byte[]? klicensee)
    {
        if (npd is null) return; // no NPDRM layer

        byte[] klic;
        if (klicensee is not null)
        {
            if (klicensee.Length != 0x10) throw new ArgumentException("klicensee must be 16 bytes.");
            klic = (byte[])klicensee.Clone();
        }
        else if (npd.RawLicense == 3) // free
        {
            klic = (byte[])SelfKeyset.NpKlicFree.Clone();
        }
        else
        {
            throw new PkgFormatException(
                $"This NPDRM SELF uses license type {npd.License?.ToString() ?? npd.RawLicense.ToString()} " +
                "and needs its RAP license file to decrypt (import it into the RAP library or supply --rap).");
        }

        // Unwrap the klicensee, then AES-128-CBC (iv = 0) decrypt the metadata info.
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = SelfKeyset.NpKlicKey;
        byte[] npdrmKey = aes.DecryptEcb(klic, PaddingMode.None);

        AesCbcDecrypt(npdrmKey, new byte[0x10], metadataInfo);
    }

    private static void AesCbcDecrypt(byte[] key, byte[] iv, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = iv;
        using var dec = aes.CreateDecryptor();
        byte[] outp = dec.TransformFinalBlock(data, 0, data.Length);
        outp.CopyTo(data, 0);
    }

    /// <summary>AES-128 counter mode: keystream = ECB(counter), counter = iv + blockIndex (128-bit BE).</summary>
    private static void AesCtr(byte[] key, byte[] iv, byte[] data, int offset, int length)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;

        Span<byte> counter = stackalloc byte[0x10];
        iv.CopyTo(counter);
        Span<byte> keystream = stackalloc byte[0x10];

        int pos = offset;
        int endEx = offset + length;
        while (pos < endEx)
        {
            aes.EncryptEcb(counter, keystream, PaddingMode.None);
            int n = Math.Min(0x10, endEx - pos);
            for (int i = 0; i < n; i++) data[pos + i] ^= keystream[i];
            pos += n;
            IncrementBe(counter);
        }
    }

    private static void IncrementBe(Span<byte> counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
            if (++counter[i] != 0) break;
    }

    /// <summary>
    /// Upper bound on a reconstructed ELF. The PS3 has 256 MiB of RAM, so a plaintext executable can't
    /// legitimately exceed it — this caps the output <see cref="MemoryStream"/> so a crafted header
    /// (huge <c>p_offset</c>/<c>e_shoff</c>/<c>p_filesz</c>) can't drive an unbounded allocation.
    /// </summary>
    private const long MaxElfSize = 256L << 20;

    private static byte[] WriteElf(byte[] self, int ehdrOffset, int phdrOffset, int shdrOffset,
        int phnum, int shnum, long eShoff, MetaSection[] sections, byte[] data)
    {
        const int EhdrLen = 0x40, PhdrLen = 0x38, ShdrLen = 0x40;

        // All of these offsets/counts come from the (plaintext) SELF/ELF headers — validate every
        // read against the file and every write position against the ELF-size cap.
        if (phnum < 0 || shnum < 0)
            throw new PkgFormatException("SELF has a negative program/section header count.");
        RequireWithin(ehdrOffset, EhdrLen, self.Length, "ELF header");
        RequireWithin(phdrOffset, (long)phnum * PhdrLen, self.Length, "program headers");

        var e = new MemoryStream();

        // ELF header + program headers, verbatim from the SELF (they are stored plaintext).
        e.Write(self, ehdrOffset, EhdrLen);
        e.Write(self, phdrOffset, phnum * PhdrLen);

        // Section data: place each PHDR-type section at its program header's file offset.
        int dataOff = 0;
        foreach (var s in sections)
        {
            RequireWithin(dataOff, s.DataSize, data.Length, "decrypted section data");
            int sectionSize = (int)s.DataSize;
            if (s.Type != 2)
            {
                dataOff += sectionSize;
                continue;
            }
            if (s.ProgramIdx < 0 || s.ProgramIdx >= phnum)
                throw new PkgFormatException($"SELF section references program header {s.ProgramIdx} (only {phnum} present).");
            int p = phdrOffset + s.ProgramIdx * PhdrLen;
            long pOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(self.AsSpan(p + 0x08));
            long pFilesz = (long)BinaryPrimitives.ReadUInt64BigEndian(self.AsSpan(p + 0x20));

            // pOffset/pFilesz land the segment in the output ELF; bound both against the size cap.
            RequireWithin(pOffset, pFilesz, MaxElfSize, "ELF segment placement");

            e.Position = pOffset;
            if (s.Compressed == 2)
            {
                using var zin = new MemoryStream(data, dataOff, sectionSize, writable: false);
                using var z = new ZLibStream(zin, CompressionMode.Decompress);
                byte[] outBuf = new byte[pFilesz];
                int read = 0;
                while (read < outBuf.Length)
                {
                    int r = z.Read(outBuf, read, outBuf.Length - read);
                    if (r == 0) break;
                    read += r;
                }
                e.Write(outBuf, 0, read);
            }
            else
            {
                e.Write(data, dataOff, sectionSize);
            }
            dataOff += sectionSize;
        }

        // Section headers, verbatim, at e_shoff.
        if (shdrOffset != 0 && shnum > 0)
        {
            RequireWithin(shdrOffset, (long)shnum * ShdrLen, self.Length, "section headers");
            RequireWithin(eShoff, (long)shnum * ShdrLen, MaxElfSize, "section-header placement");
            e.Position = eShoff;
            e.Write(self, shdrOffset, shnum * ShdrLen);
        }

        return e.ToArray();
    }

    private static ulong ReadU64(byte[] buf, int offset)
    {
        if (offset + 8 > buf.Length)
            throw new PkgFormatException($"SELF header truncated at 0x{offset:X}.");
        return BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(offset));
    }

    /// <summary>
    /// Requires <c>[offset, offset+length)</c> to lie within <paramref name="total"/> bytes, throwing
    /// <see cref="PkgFormatException"/> (never a raw index exception) otherwise. Uses a subtraction
    /// guard so attacker-controlled 64-bit offsets/lengths can't overflow past the check.
    /// </summary>
    private static void RequireWithin(long offset, long length, long total, string what)
    {
        if (offset < 0 || length < 0 || offset > total || length > total - offset)
            throw new PkgFormatException(
                $"SELF {what} is out of bounds (offset 0x{offset:X}, length 0x{length:X}, file 0x{total:X}).");
    }
}
