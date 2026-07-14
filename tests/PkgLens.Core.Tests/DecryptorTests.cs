using System.Security.Cryptography;
using PkgLens.Core.Shared.Crypto;
using Xunit;

namespace PkgLens.Core.Tests;

public class DecryptorTests
{
    [Fact]
    public void DebugKeystream_IsSymmetric_RoundTrips()
    {
        var qa = RandomNumberGenerator.GetBytes(16);
        var plaintext = RandomNumberGenerator.GetBytes(1000);
        var buffer = (byte[])plaintext.Clone();

        new DebugSha1Decryptor(qa).DecryptInPlace(buffer, 0); // encrypt
        Assert.NotEqual(plaintext, buffer);
        new DebugSha1Decryptor(qa).DecryptInPlace(buffer, 0); // decrypt
        Assert.Equal(plaintext, buffer);
    }

    [Fact]
    public void RetailKeystream_IsSymmetric_RoundTrips()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        var riv = RandomNumberGenerator.GetBytes(16);
        var plaintext = RandomNumberGenerator.GetBytes(1000);
        var buffer = (byte[])plaintext.Clone();

        using (var enc = new RetailAesCtrDecryptor(key, riv)) enc.DecryptInPlace(buffer, 0);
        Assert.NotEqual(plaintext, buffer);
        using (var dec = new RetailAesCtrDecryptor(key, riv)) dec.DecryptInPlace(buffer, 0);
        Assert.Equal(plaintext, buffer);
    }

    [Fact]
    public void PartialOffsetDecrypt_MatchesFullDecrypt()
    {
        // Decrypting a sub-range at its true region offset must match the same bytes from a
        // whole-region decrypt (verifies unaligned-offset keystream indexing).
        var qa = RandomNumberGenerator.GetBytes(16);
        var full = RandomNumberGenerator.GetBytes(512);

        var whole = (byte[])full.Clone();
        new DebugSha1Decryptor(qa).DecryptInPlace(whole, 0);

        const int start = 37, len = 100; // deliberately unaligned
        var slice = full.Skip(start).Take(len).ToArray();
        new DebugSha1Decryptor(qa).DecryptInPlace(slice, start);

        Assert.Equal(whole.Skip(start).Take(len).ToArray(), slice);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(255u)]
    [InlineData(256u)]
    public void AddBigEndian_MatchesBigIntegerArithmetic(ulong add)
    {
        var counter = new byte[16];
        for (int i = 0; i < 16; i++) counter[i] = (byte)(i * 17 + 3);
        var expected = new System.Numerics.BigInteger(counter, isUnsigned: true, isBigEndian: true) + add;

        RetailAesCtrDecryptor.AddBigEndian(counter, add);

        var actual = new System.Numerics.BigInteger(counter, isUnsigned: true, isBigEndian: true);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AddBigEndian_CarryPropagatesAcrossAllBytes()
    {
        var counter = new byte[16];
        for (int i = 8; i < 16; i++) counter[i] = 0xFF; // low 8 bytes all set

        RetailAesCtrDecryptor.AddBigEndian(counter, 1);

        // Should roll the low qword to zero and carry into byte 7.
        for (int i = 8; i < 16; i++) Assert.Equal(0, counter[i]);
        Assert.Equal(1, counter[7]);
    }
}
