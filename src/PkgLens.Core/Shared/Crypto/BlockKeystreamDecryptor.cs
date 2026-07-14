namespace PkgLens.Core.Shared.Crypto;

/// <summary>
/// Shared plumbing for the PKG stream ciphers. Subclasses only implement
/// <see cref="FillBlock"/> to produce the 16-byte keystream for a given absolute block index;
/// this base handles arbitrary (unaligned) start offsets and XORs the keystream into the data.
/// </summary>
public abstract class BlockKeystreamDecryptor : IPkgDecryptor
{
    protected const int BlockSize = 16;

    /// <summary>Writes the 16-byte keystream for <paramref name="blockIndex"/> into <paramref name="block"/>.</summary>
    protected abstract void FillBlock(ulong blockIndex, Span<byte> block);

    public void DecryptInPlace(Span<byte> data, long dataRegionOffset)
    {
        if (dataRegionOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(dataRegionOffset));

        Span<byte> keystream = stackalloc byte[BlockSize];
        long absolute = dataRegionOffset;
        int written = 0;

        while (written < data.Length)
        {
            ulong blockIndex = (ulong)(absolute / BlockSize);
            int within = (int)(absolute % BlockSize);

            FillBlock(blockIndex, keystream);

            int n = Math.Min(BlockSize - within, data.Length - written);
            for (int i = 0; i < n; i++)
                data[written + i] ^= keystream[within + i];

            written += n;
            absolute += n;
        }
    }
}
