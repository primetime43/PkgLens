using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PkgLens.Core.Ps3.Psarc;

namespace PkgLens.Core.Tests;

public sealed class PsarcTests
{
    [Fact]
    public void Read_AndExtract_StandardZlibArchive()
    {
        byte[] first = Encoding.UTF8.GetBytes("hello from psarc\n");
        byte[] second = Enumerable.Range(0, 173).Select(index => (byte)(index * 37)).ToArray();
        byte[] bytes = BuildArchive(("text/readme.txt", first), ("data/pattern.bin", second));

        using var source = new MemoryStream(bytes);
        PsarcArchiveInfo archive = PsarcReader.Read(source);

        Assert.Equal((ushort)1, archive.Header.MajorVersion);
        Assert.Equal((ushort)4, archive.Header.MinorVersion);
        Assert.Equal("zlib", archive.Header.Compression);
        Assert.Equal(2, archive.Entries.Count);
        Assert.Equal("text/readme.txt", archive.Entries[0].Path);
        Assert.Equal(first, PsarcReader.ExtractEntryBytes(source, archive, archive.Entries[0]));
        Assert.Equal(second, PsarcReader.ExtractEntryBytes(source, archive, archive.Entries[1]));
    }

    [Fact]
    public void Repack_ReplacesOneEntry_AndPreservesMultiBlockEntry()
    {
        byte[] preserved = Enumerable.Range(0, 211).Select(index => (byte)(index * 17 + 9)).ToArray();
        byte[] sourceBytes = BuildArchive(("replace.txt", Encoding.ASCII.GetBytes("old")),
            ("folder/preserved.bin", preserved));
        string sourcePath = Path.GetTempFileName();
        string destinationPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.psarc");
        try
        {
            File.WriteAllBytes(sourcePath, sourceBytes);
            PsarcArchiveInfo archive = PsarcReader.Read(sourcePath);
            byte[] replacement = Enumerable.Repeat((byte)'A', 321).ToArray();
            var replacements = new Dictionary<PsarcEntry, PsarcReplacement>
            {
                [archive.Entries[0]] = PsarcReplacement.FromBytes(replacement),
            };

            PsarcWriter.Repack(sourcePath, archive, replacements, destinationPath);

            PsarcArchiveInfo rebuilt = PsarcReader.Read(destinationPath);
            using var output = File.OpenRead(destinationPath);
            Assert.Equal(replacement, PsarcReader.ExtractEntryBytes(output, rebuilt, rebuilt.Entries[0]));
            Assert.Equal(preserved, PsarcReader.ExtractEntryBytes(output, rebuilt, rebuilt.Entries[1]));
            Assert.Equal(archive.Header.Flags, rebuilt.Header.Flags);
            Assert.Equal(archive.Header.BlockSize, rebuilt.Header.BlockSize);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(destinationPath);
        }
    }

    [Fact]
    public void ExtractAll_RecreatesSafeDirectoryTree()
    {
        byte[] bytes = BuildArchive(("one.txt", [1, 2]), ("nested/two.bin", [3, 4, 5]));
        string sourcePath = Path.GetTempFileName();
        string destination = Path.Combine(Path.GetTempPath(), $"pkglens-psarc-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(sourcePath, bytes);
            PsarcArchiveInfo archive = PsarcReader.Read(sourcePath);
            PsarcReader.ExtractAll(sourcePath, archive, destination);
            Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(Path.Combine(destination, "one.txt")));
            Assert.Equal(new byte[] { 3, 4, 5 }, File.ReadAllBytes(Path.Combine(destination, "nested", "two.bin")));
        }
        finally
        {
            File.Delete(sourcePath);
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
        }
    }

    [Fact]
    public void Read_RejectsTraversalManifestPath()
    {
        byte[] bytes = BuildArchive(("../escape.bin", new byte[] { 1 }));
        using var source = new MemoryStream(bytes);
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => PsarcReader.Read(source));
        Assert.Contains("unsafe path", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_RejectsLzmaAndEncryptedArchives()
    {
        byte[] lzma = BuildArchive(("file.bin", new byte[] { 1 }));
        Encoding.ASCII.GetBytes("lzma").CopyTo(lzma, 8);
        using var lzmaStream = new MemoryStream(lzma);
        Assert.Throws<NotSupportedException>(() => PsarcReader.Read(lzmaStream));

        byte[] encrypted = BuildArchive(("file.bin", new byte[] { 1 }));
        BinaryPrimitives.WriteUInt32BigEndian(encrypted.AsSpan(28, 4), (uint)PsarcArchiveFlags.Encrypted);
        using var encryptedStream = new MemoryStream(encrypted);
        Assert.Throws<NotSupportedException>(() => PsarcReader.Read(encryptedStream));
    }

    [Fact]
    public void Repack_RejectsSourceOverwrite()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, BuildArchive(("file.bin", new byte[] { 1 })));
            PsarcArchiveInfo archive = PsarcReader.Read(path);
            Assert.Throws<InvalidOperationException>(() =>
                PsarcWriter.Repack(path, archive, new Dictionary<PsarcEntry, PsarcReplacement>(), path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] BuildArchive(params (string Path, byte[] Data)[] files)
    {
        const int blockSize = 64;
        const int headerSize = 0x20;
        const int tocSize = 0x1E;
        string manifestText = string.Join('\n', files.Select(file => file.Path)) + '\n';
        var entries = new List<(string Path, byte[] Data)> { (string.Empty, Encoding.UTF8.GetBytes(manifestText)) };
        entries.AddRange(files);

        var payloads = new List<byte[]>();
        var blockLengths = new List<ushort>();
        var records = new List<(byte[] Digest, uint FirstBlock, ulong Length, ulong StoredLength)>();
        foreach ((string path, byte[] data) in entries)
        {
            uint firstBlock = (uint)blockLengths.Count;
            ulong storedLength = 0;
            for (int offset = 0; offset < data.Length; offset += blockSize)
            {
                byte[] plain = data.AsSpan(offset, Math.Min(blockSize, data.Length - offset)).ToArray();
                using var compressed = new MemoryStream();
                using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
                    zlib.Write(plain);
                byte[] stored = compressed.ToArray();
                if (stored.Length >= plain.Length)
                {
                    blockLengths.Add(0);
                    payloads.Add(plain);
                    storedLength += (uint)plain.Length;
                }
                else
                {
                    blockLengths.Add(checked((ushort)stored.Length));
                    payloads.Add(stored);
                    storedLength += (uint)stored.Length;
                }
            }
            byte[] digest = path.Length == 0 ? new byte[16] : MD5.HashData(Encoding.UTF8.GetBytes(path));
            records.Add((digest, firstBlock, (ulong)data.Length, storedLength));
        }

        uint dataOffset = checked((uint)(headerSize + tocSize * records.Count + sizeof(ushort) * blockLengths.Count));
        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[headerSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0x50534152);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], 4);
        Encoding.ASCII.GetBytes("zlib", header[8..12]);
        BinaryPrimitives.WriteUInt32BigEndian(header[12..], dataOffset);
        BinaryPrimitives.WriteUInt32BigEndian(header[16..], tocSize);
        BinaryPrimitives.WriteUInt32BigEndian(header[20..], (uint)records.Count);
        BinaryPrimitives.WriteUInt32BigEndian(header[24..], blockSize);
        output.Write(header);

        ulong offsetInArchive = dataOffset;
        byte[] tocBytes = new byte[tocSize];
        foreach (var record in records)
        {
            Span<byte> toc = tocBytes;
            toc.Clear();
            record.Digest.CopyTo(toc);
            BinaryPrimitives.WriteUInt32BigEndian(toc[16..], record.FirstBlock);
            WriteUInt40(toc[20..25], record.Length);
            WriteUInt40(toc[25..30], offsetInArchive);
            output.Write(toc);
            offsetInArchive += record.StoredLength;
        }
        byte[] blockLengthBytes = new byte[2];
        foreach (ushort length in blockLengths)
        {
            Span<byte> bytes = blockLengthBytes;
            BinaryPrimitives.WriteUInt16BigEndian(bytes, length);
            output.Write(bytes);
        }
        foreach (byte[] payload in payloads) output.Write(payload);
        return output.ToArray();
    }

    private static void WriteUInt40(Span<byte> bytes, ulong value)
    {
        bytes[0] = (byte)(value >> 32);
        bytes[1] = (byte)(value >> 24);
        bytes[2] = (byte)(value >> 16);
        bytes[3] = (byte)(value >> 8);
        bytes[4] = (byte)value;
    }
}
