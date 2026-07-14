using PkgLens.Core.Ps3;
using PkgLens.Core.Shared.Crypto;
using Xunit;

namespace PkgLens.Core.Tests;

/// <summary>
/// Covers the NPDRM header-signature verifier's decision paths. The positive (Valid) path can't be
/// unit-tested with a synthetic fixture — a valid signature requires Sony's private key, which we
/// never have or forge — so it is verified empirically against real retail PS3 and PSP packages
/// during development (documented, not committed, like the other real-dump checks).
/// </summary>
public class NpdrmSignatureTests
{
    private static byte[] HeaderWithSignature(byte[]? signature)
    {
        var head = new byte[NpdrmSignature.RequiredLength]; // 0xB8
        for (int i = 0; i < 0x80; i++) head[i] = (byte)(i * 3 + 1); // arbitrary non-trivial signed body
        signature?.CopyTo(head, 0x90);
        return head;
    }

    [Fact]
    public void VerifyHeader_AllZeroSignature_IsAbsent() =>
        Assert.Equal(PkgSignatureResult.Absent, NpdrmSignature.VerifyHeader(HeaderWithSignature(null)));

    [Fact]
    public void VerifyHeader_TooShort_IsAbsent() =>
        Assert.Equal(PkgSignatureResult.Absent, NpdrmSignature.VerifyHeader(new byte[0x80]));

    [Fact]
    public void VerifyHeader_WellFormedButWrongSignature_IsInvalid()
    {
        // r = s = 1: in range for the curve order, so verification runs to completion and returns
        // false (rather than throwing on an out-of-range scalar). Also confirms the explicit 160-bit
        // curve is constructible on the test platform (else this would be Unsupported).
        var sig = new byte[0x28];
        sig[0x13] = 0x01; // r = 1
        sig[0x27] = 0x01; // s = 1
        Assert.Equal(PkgSignatureResult.Invalid, NpdrmSignature.VerifyHeader(HeaderWithSignature(sig)));
    }
}
