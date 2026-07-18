using System.Buffers.Binary;
using System.IO.Compression;
using SharpCompress.Compressors.LZMA;

namespace PkgLens.Core.Ps3.Psarc;

internal static class PsarcBlockCodec
{
    internal const string Zlib = "zlib";
    internal const string Lzma = "lzma";
    private const int LzmaHeaderSize = 13;

    internal static bool IsSupported(string compression) => compression is Zlib or Lzma;

    internal static byte[] Encode(string compression, byte[] plain, int logicalSize, uint blockSize)
    {
        ArgumentNullException.ThrowIfNull(plain);
        if ((uint)logicalSize > (uint)plain.Length) throw new ArgumentOutOfRangeException(nameof(logicalSize));

        return compression switch
        {
            Zlib => EncodeZlib(plain, logicalSize),
            Lzma => EncodeLzma(plain, logicalSize, blockSize),
            _ => throw new NotSupportedException($"PSARC compression '{compression}' is not supported."),
        };
    }

    internal static void Decode(string compression, byte[] stored, int storedSize, byte[] plain, int logicalSize,
        uint blockSize, string? item)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(plain);
        if ((uint)storedSize > (uint)stored.Length) throw new ArgumentOutOfRangeException(nameof(storedSize));
        if ((uint)logicalSize > (uint)plain.Length) throw new ArgumentOutOfRangeException(nameof(logicalSize));

        if (storedSize == logicalSize)
        {
            stored.AsSpan(0, logicalSize).CopyTo(plain);
            return;
        }

        switch (compression)
        {
            case Zlib:
                DecodeZlib(stored, storedSize, plain, logicalSize, item);
                break;
            case Lzma:
                DecodeLzma(stored, storedSize, plain, logicalSize, blockSize, item);
                break;
            default:
                throw new NotSupportedException($"PSARC compression '{compression}' is not supported.");
        }
    }

    private static byte[] EncodeZlib(byte[] plain, int logicalSize)
    {
        using var output = new MemoryStream(logicalSize);
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(plain, 0, logicalSize);
        return output.ToArray();
    }

    private static byte[] EncodeLzma(byte[] plain, int logicalSize, uint blockSize)
    {
        using var payload = new MemoryStream(logicalSize);
        byte[] properties;
        var encoderProperties = new LzmaEncoderProperties(eos: false, dictionary: checked((int)blockSize), numFastBytes: 32);
        using (var lzma = LzmaStream.Create(encoderProperties, isLzma2: false, payload))
        {
            properties = lzma.Properties.ToArray();
            lzma.Write(plain, 0, logicalSize);
        }
        if (properties.Length != 5)
            throw new InvalidDataException($"The LZMA encoder returned {properties.Length} property bytes instead of 5.");

        using var output = new MemoryStream(checked(LzmaHeaderSize + (int)payload.Length));
        output.Write(properties);
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(size, checked((ulong)logicalSize));
        output.Write(size);
        payload.Position = 0;
        payload.CopyTo(output);
        return output.ToArray();
    }

    private static void DecodeZlib(byte[] stored, int storedSize, byte[] plain, int logicalSize, string? item)
    {
        using var input = new MemoryStream(stored, 0, storedSize, writable: false, publiclyVisible: true);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true);
        ReadDecodedBlock(zlib, plain, logicalSize, item);
    }

    private static void DecodeLzma(byte[] stored, int storedSize, byte[] plain, int logicalSize, uint blockSize,
        string? item)
    {
        if (storedSize < LzmaHeaderSize)
            throw InvalidBlock(item, "is shorter than its 13-byte LZMA header");

        ulong declaredSize = BinaryPrimitives.ReadUInt64LittleEndian(stored.AsSpan(5, 8));
        if (declaredSize != (ulong)logicalSize)
            throw InvalidBlock(item, $"declares {declaredSize:n0} decompressed bytes instead of {logicalSize:n0}");
        uint dictionarySize = BinaryPrimitives.ReadUInt32LittleEndian(stored.AsSpan(1, 4));
        if (dictionarySize == 0 || dictionarySize > blockSize)
            throw InvalidBlock(item, $"declares an invalid {dictionarySize:n0}-byte LZMA dictionary");

        byte[] properties = stored.AsSpan(0, 5).ToArray();
        int payloadSize = storedSize - LzmaHeaderSize;
        using var input = new MemoryStream(stored, LzmaHeaderSize, payloadSize, writable: false, publiclyVisible: true);
        using var lzma = LzmaStream.Create(properties, input, payloadSize, logicalSize, leaveOpen: true);
        ReadDecodedBlock(lzma, plain, logicalSize, item);
    }

    private static void ReadDecodedBlock(Stream decoder, byte[] plain, int logicalSize, string? item)
    {
        int decoded = 0;
        while (decoded < logicalSize)
        {
            int read = decoder.Read(plain, decoded, logicalSize - decoded);
            if (read == 0) break;
            decoded += read;
        }
        if (decoded != logicalSize || decoder.ReadByte() != -1)
            throw InvalidBlock(item, "has an invalid decompressed size");
    }

    private static InvalidDataException InvalidBlock(string? item, string reason) =>
        new($"A compressed PSARC block for {item ?? "the manifest"} {reason}.");
}
