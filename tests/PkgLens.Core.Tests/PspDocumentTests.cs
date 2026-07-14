using System;
using System.Text;
using PkgLens.Core;
using PkgLens.Core.Npd.Psp;
using Xunit;

namespace PkgLens.Core.Tests;

/// <summary>
/// Covers PSP DOCUMENT.DAT detection and guard paths. The full happy-path decrypt (→ PNG manual
/// pages) requires a real minis manual + its DOCINFO.EDAT and is verified against real data during
/// development (documented, not committed) — a synthetic fixture would have to reproduce the exact
/// HMAC/DES container.
/// </summary>
public class PspDocumentTests
{
    private static readonly byte[] DocHeader = Convert.FromHexString("00504744010000000100000000000000");

    [Fact]
    public void IsDocument_MatchesFixedHeaderOnly()
    {
        var buf = new byte[0xA0];
        DocHeader.CopyTo(buf, 0);
        Assert.True(PspDocument.IsDocument(buf));
        Assert.False(PspDocument.IsDocument(Encoding.ASCII.GetBytes("\0PSPEDAT and then some")));
        Assert.False(PspDocument.IsDocument(new byte[0xA0])); // zeros, no header
    }

    [Fact]
    public void DecryptPages_NotADocument_Throws()
    {
        Assert.Throws<PkgFormatException>(() => PspDocument.DecryptPages(new byte[0xA0]));
    }

    [Fact]
    public void DecryptPages_HeaderHmacMismatch_Throws()
    {
        // Valid magic + zero padding at 0x70, but zero HMAC fields → header HMAC check must fail.
        var buf = new byte[0xA0];
        DocHeader.CopyTo(buf, 0);
        Assert.Throws<PkgFormatException>(() => PspDocument.DecryptPages(buf));
    }
}
