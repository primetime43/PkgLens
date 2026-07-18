using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using PkgLens.Core.Ps1;
using PkgLens.Core.Psp;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Tests.TestData;

namespace PkgLens.Core.Tests;

public sealed class Ps1ClassicPackageTests
{
    [Theory]
    [InlineData("PSISOIMG0000", Ps1ClassicImageKind.SingleDisc)]
    [InlineData("PSTITLEIMG000000", Ps1ClassicImageKind.MultiDisc)]
    public void CheckEligibility_IdentifiesPs1DiscSet(string magic, Ps1ClassicImageKind expected)
    {
        byte[] packageBytes = BuildPs1Package(BuildPbp(
            (0, Encoding.ASCII.GetBytes("PARAM")),
            (7, Encoding.ASCII.GetBytes(magic + "payload"))));
        using var package = new MemoryStream(packageBytes);

        Ps1ClassicExportEligibility result = Ps1ClassicPackageExporter.CheckEligibility(
            package, new FileKeyProvider());

        Assert.True(result.CanExport, result.Reason);
        Assert.Equal(Ps1ClassicSourceKind.EbootPbp, result.SourceKind);
        Assert.Equal(expected, result.ImageKind);
        Assert.True(result.RapAvailable);
    }

    [Fact]
    public void CheckEligibility_RejectsContentTypeFalsePositive()
    {
        var builder = new SyntheticPkgBuilder();
        builder.AddMetadataU32((uint)PkgMetadataId.ContentType, (uint)PkgContentType.GameExec);
        builder.AddFile("USRDIR/EBOOT.PBP", BuildPbp(
            (7, Encoding.ASCII.GetBytes("PSISOIMG0000payload"))));
        using var package = new MemoryStream(builder.Build());

        Ps1ClassicExportEligibility result = Ps1ClassicPackageExporter.CheckEligibility(
            package, new FileKeyProvider());

        Assert.False(result.CanExport);
        Assert.Contains("not a PS1 Classic", result.Reason);
    }

    [Fact]
    public void CheckEligibility_RejectsOrdinaryPbpInPs1Package()
    {
        byte[] packageBytes = BuildPs1Package(BuildPbp(
            (7, Encoding.ASCII.GetBytes("NPUMDIMGordinary-psp"))));
        using var package = new MemoryStream(packageBytes);

        Ps1ClassicExportEligibility result = Ps1ClassicPackageExporter.CheckEligibility(
            package, new FileKeyProvider());

        Assert.False(result.CanExport);
        Assert.Contains("not a PS1 Classic", result.Reason);
    }

    [Fact]
    public void Prepare_ExportsPbpMetadataDocumentAndManifest()
    {
        byte[] parameter = Encoding.ASCII.GetBytes("synthetic-param-sfo");
        byte[] icon = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        byte[] document = Encoding.ASCII.GetBytes("manual");
        byte[] pbp = BuildPbp(
            (0, parameter),
            (1, icon),
            (7, Encoding.ASCII.GetBytes("PSISOIMG0000payload")));
        var builder = new SyntheticPkgBuilder();
        builder.AddMetadataU32((uint)PkgMetadataId.ContentType, (uint)PkgContentType.Ps1Emu);
        builder.AddFile("USRDIR/CONTENT/EBOOT.PBP", pbp);
        builder.AddFile("USRDIR/CONTENT/DOCUMENT.DAT", document);

        string output = Path.Combine(Path.GetTempPath(), "pkglens-ps1-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var package = new MemoryStream(builder.Build());
            Ps1ClassicPreparedExport result = Ps1ClassicPackageExporter.Prepare(
                package, output, new FileKeyProvider());

            Assert.Equal(Ps1ClassicImageKind.SingleDisc, result.ImageKind);
            Assert.Equal(pbp, File.ReadAllBytes(result.EbootPath));
            Assert.Equal(parameter, File.ReadAllBytes(Path.Combine(result.MetadataDirectory, "PARAM.SFO")));
            Assert.Equal(icon, File.ReadAllBytes(Path.Combine(result.MetadataDirectory, "ICON0.PNG")));
            Assert.Equal(document, File.ReadAllBytes(result.DocumentPath!));
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(result.ManifestPath));
            Assert.Equal("SingleDisc", manifest.RootElement.GetProperty("ImageKind").GetString());
            Assert.Equal("EbootPbp", manifest.RootElement.GetProperty("SourceKind").GetString());
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    private static byte[] BuildPs1Package(byte[] pbp)
    {
        var builder = new SyntheticPkgBuilder();
        builder.AddMetadataU32((uint)PkgMetadataId.ContentType, (uint)PkgContentType.Ps1Emu);
        builder.AddFile("USRDIR/CONTENT/EBOOT.PBP", pbp);
        return builder.Build();
    }

    private static byte[] BuildPbp(params (int Slot, byte[] Data)[] sections)
    {
        var slots = Enumerable.Range(0, 8).Select(_ => Array.Empty<byte>()).ToArray();
        foreach ((int slot, byte[] data) in sections) slots[slot] = data;
        var offsets = new uint[8];
        uint cursor = PbpArchive.HeaderSize;
        for (int index = 0; index < offsets.Length; index++)
        {
            offsets[index] = cursor;
            cursor += (uint)slots[index].Length;
        }

        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[PbpArchive.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, PbpArchive.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 0x00010000);
        for (int index = 0; index < offsets.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(header[(8 + index * 4)..], offsets[index]);
        output.Write(header);
        foreach (byte[] section in slots) output.Write(section);
        return output.ToArray();
    }
}
