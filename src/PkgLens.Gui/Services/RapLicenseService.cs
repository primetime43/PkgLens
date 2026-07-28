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
        string? libraryDirectory, string? explicitKlicenseeHex = null, string? fileName = null)
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
        if (info.Npdrm is null)
            return new RapLicenseResolution(null, null, info.Npdrm?.ContentId);
        if (explicitRapPath is not null)
            return ResolveContentId(info.Npdrm.ContentId, explicitRapPath, libraryDirectory);

        KlicenseeResolution? stored = TryFindKlicensee(info.Npdrm.ContentId, fileName,
            info.Npdrm.RawLicenseType);
        if (stored is not null)
            return new RapLicenseResolution(stored.Klicensee, SourceName(stored), info.Npdrm.ContentId);

        if (info.Npdrm.LicenseType == NpdrmLicenseType.Free)
            return new RapLicenseResolution(null, null, info.Npdrm.ContentId);
        return ResolveContentId(info.Npdrm.ContentId, null, libraryDirectory);
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
            KlicenseeResolution? stored = TryFindKlicensee(contentId, null, null);
            if (stored is not null)
                return new RapLicenseResolution(stored.Klicensee, SourceName(stored), contentId);
            rap = RapStore.Find(contentId, libraryDirectory);
            source = rap is null ? null : "RAP library";
        }

        return new RapLicenseResolution(
            rap is null ? null : NpdKeys.RapToKlicensee(rap), source, contentId);
    }

    private static KlicenseeResolution? TryFindKlicensee(string contentId, string? fileName,
        uint? licenseType)
    {
        KlicenseeResolution? local = null;
        try { local = KlicenseeStore.Find(contentId, fileName, licenseType); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // The Keys page reports database damage; operations can still use RAP/default paths.
        }
        return local ?? KnownKlicenseeStore.Find(contentId, fileName, licenseType);
    }

    private static string SourceName(KlicenseeResolution resolution) =>
        resolution.DatabasePath == KnownKlicenseeStore.DatabaseId
            ? "bundled klicensee catalog"
            : "klicensee library";
}
