using System;
using System.IO;
using PkgLens.Core;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Ps3.Self;

namespace PkgLens.Gui.Services;

internal sealed record RapLicenseResolution(byte[]? Klicensee, string? Source, string? ContentId);

internal static class RapLicenseService
{
    public static RapLicenseResolution ResolveSelf(byte[] self, string? explicitRapPath,
        string? libraryDirectory, string? explicitKlicenseeHex = null)
    {
        SelfInfo info = SelfReader.ParseInfo(new MemoryStream(self));
        if (!string.IsNullOrWhiteSpace(explicitKlicenseeHex))
        {
            byte[] klicensee;
            try { klicensee = Convert.FromHexString(explicitKlicenseeHex.Trim()); }
            catch (FormatException ex)
            {
                throw new PkgFormatException("A raw klicensee must contain exactly 32 hexadecimal characters.", ex);
            }
            if (klicensee.Length != 16)
                throw new PkgFormatException("A raw klicensee must contain exactly 32 hexadecimal characters.");
            return new RapLicenseResolution(klicensee, "raw klicensee", info.Npdrm?.ContentId);
        }
        if (info.Npdrm is null || info.Npdrm.LicenseType == NpdrmLicenseType.Free)
            return new RapLicenseResolution(null, null, info.Npdrm?.ContentId);
        return ResolveContentId(info.Npdrm.ContentId, explicitRapPath, libraryDirectory);
    }

    public static RapLicenseResolution ResolveContentId(
        string contentId, string? explicitRapPath, string? libraryDirectory)
    {
        byte[]? rap;
        string? source;
        if (explicitRapPath is not null)
        {
            rap = File.ReadAllBytes(explicitRapPath);
            if (rap.Length != 16)
                throw new PkgFormatException("A RAP file must be exactly 16 bytes.");
            RapStore.Install(contentId, rap, libraryDirectory);
            source = "selected RAP";
        }
        else
        {
            rap = RapStore.Find(contentId, libraryDirectory);
            source = rap is null ? null : "RAP library";
        }

        return new RapLicenseResolution(
            rap is null ? null : NpdKeys.RapToKlicensee(rap), source, contentId);
    }
}
