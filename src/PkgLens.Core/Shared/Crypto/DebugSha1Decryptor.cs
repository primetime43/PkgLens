using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PkgLens.Core.Shared.Crypto;

/// <summary>
/// Keystream for non-finalized (debug) packages. Needs no external key — it is derived entirely
/// from the header's 16-byte QA digest, so debug packages decrypt out of the box.
///
/// For each 16-byte block, a 64-byte SHA-1 input buffer is built as (verified against RPCS3's
/// <c>unpkg.cpp</c>, where the buffer is <c>be_t&lt;u64&gt; input[8]</c>):
/// <code>
/// [0x00..0x07] = qa_digest[0x00..0x07]   (input[0])
/// [0x08..0x0F] = qa_digest[0x00..0x07]   (input[1])
/// [0x10..0x17] = qa_digest[0x08..0x0F]   (input[2])
/// [0x18..0x1F] = qa_digest[0x08..0x0F]   (input[3])
/// [0x20..0x37] = 0                        (input[4..6])
/// [0x38..0x3F] = block index, big-endian u64 (input[7])
/// </code>
/// The first 16 bytes of SHA-1(buffer) are the keystream block.
/// </summary>
public sealed class DebugSha1Decryptor : BlockKeystreamDecryptor
{
    private readonly byte[] _template = new byte[64];

    public DebugSha1Decryptor(ReadOnlySpan<byte> qaDigest)
    {
        if (qaDigest.Length < 16)
            throw new ArgumentException("QA digest must be 16 bytes.", nameof(qaDigest));

        qaDigest[..8].CopyTo(_template.AsSpan(0x00));
        qaDigest[..8].CopyTo(_template.AsSpan(0x08));
        qaDigest.Slice(8, 8).CopyTo(_template.AsSpan(0x10));
        qaDigest.Slice(8, 8).CopyTo(_template.AsSpan(0x18));
        // bytes 0x20..0x3F remain zero; the block index is written per-block.
    }

    protected override void FillBlock(ulong blockIndex, Span<byte> block)
    {
        Span<byte> input = stackalloc byte[64];
        _template.CopyTo(input);
        BinaryPrimitives.WriteUInt64BigEndian(input[0x38..], blockIndex);

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);
        hash[..16].CopyTo(block);
    }
}
