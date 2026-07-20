using System.Buffers.Binary;
using System.Text;
using PkgLens.Core.Ps3.Trophy;
using PkgLens.Core.Psp;
using PkgLens.Core.Shared;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Core.Tests.TestData;
using PkgLens.Gui.Services;

namespace PkgLens.Core.Tests;

public sealed class PackageOperationServiceTests
{
    [Fact]
    public void ExtractReplaceAndSave_AreOwnedByOperationService()
    {
        using var directory = new TempDirectory();
        byte[] original = [1, 2, 3, 4, 5];
        string packagePath = directory.Write("source.pkg",
            new SyntheticPkgBuilder().AddFile("USRDIR/DATA.BIN", original).Build());

        using var service = PackageOperationService.Open(packagePath, new InMemoryKeyProvider());
        var entry = Assert.Single(service.Info.Entries, candidate => candidate.IsFile);
        Assert.Equal(original, service.ReadEntryBytes(entry));

        string extractedPath = Path.Combine(directory.Path, "DATA.BIN");
        service.ExtractEntry(entry, extractedPath);
        Assert.Equal(original, File.ReadAllBytes(extractedPath));

        int changes = 0;
        service.PendingChangesChanged += (_, _) => changes++;
        byte[] replacement = [9, 8, 7];
        service.ReplaceEntry(entry, replacement);
        Assert.True(service.HasPendingChanges);
        Assert.Equal(1, service.PendingChangeCount);
        Assert.Equal(1, changes);

        string rebuiltPath = Path.Combine(directory.Path, "rebuilt.pkg");
        service.SaveAs(rebuiltPath);
        using Stream rebuilt = File.OpenRead(rebuiltPath);
        var rebuiltInfo = PkgReader.Read(rebuilt, new InMemoryKeyProvider());
        var rebuiltEntry = Assert.Single(rebuiltInfo.Entries, candidate => candidate.IsFile);
        Assert.Equal(replacement,
            PkgReader.ExtractEntryBytes(rebuilt, rebuiltInfo.Header, rebuiltEntry, new InMemoryKeyProvider()));
    }

    [Fact]
    public void UnpackPbp_CleansTemporaryPackageEntry()
    {
        using var directory = new TempDirectory();
        byte[] param = [4, 3, 2, 1];
        byte[] executable = [8, 7, 6, 5];
        byte[] pbp = BuildPbp((0, param), (6, executable));
        string packagePath = directory.Write("source.pkg",
            new SyntheticPkgBuilder().AddFile("USRDIR/EBOOT.PBP", pbp).Build());

        using var service = PackageOperationService.Open(packagePath, new InMemoryKeyProvider());
        var entry = Assert.Single(service.Info.Entries, candidate => candidate.IsFile);
        string output = Path.Combine(directory.Path, "pbp");
        IReadOnlyList<string> written = service.UnpackPbp(entry, output);

        Assert.Equal(["PARAM.SFO", "DATA.PSP"], written);
        Assert.Equal(param, File.ReadAllBytes(Path.Combine(output, "PARAM.SFO")));
        Assert.Equal(executable, File.ReadAllBytes(Path.Combine(output, "DATA.PSP")));
        Assert.Empty(Directory.EnumerateFiles(output, ".pkglens-*.tmp"));
    }

    [Fact]
    public void ExportPspIso_FailureCleansAllTemporaryFiles()
    {
        using var directory = new TempDirectory();
        byte[] pbp = BuildPbp((7, new byte[0x100]));
        string packagePath = directory.Write("source.pkg",
            new SyntheticPkgBuilder().AddFile("USRDIR/EBOOT.PBP", pbp).Build());

        using var service = PackageOperationService.Open(packagePath, new InMemoryKeyProvider());
        var entry = Assert.Single(service.Info.Entries, candidate => candidate.IsFile);
        string isoPath = Path.Combine(directory.Path, "output.iso");

        Assert.Throws<InvalidOperationException>(() => service.ExportPspIso(entry, isoPath));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, ".pkglens-*.tmp"));
        Assert.False(File.Exists(isoPath));
    }

    [Fact]
    public void ReadTrophySet_UsesArchiveParentDirectoryAsSetId()
    {
        using var directory = new TempDirectory();
        byte[] trophyArchive = TrophyArchiveTests.BuildArchive(("TROPCONF.SFM", Encoding.UTF8.GetBytes("""
            <trophyconf>
              <title-name>Package Trophies</title-name>
              <trophy id="0" hidden="no" ttype="B"><name>Started</name><detail>Begin.</detail></trophy>
            </trophyconf>
            """)));
        string packagePath = directory.Write("trophies.pkg", new SyntheticPkgBuilder()
            .AddFile("TROPDIR/NPWR54321_00/TROPHY.TRP", trophyArchive)
            .Build());

        using var service = PackageOperationService.Open(packagePath, new InMemoryKeyProvider());
        PkgEntry entry = Assert.Single(service.Info.Entries, candidate => candidate.IsFile);
        TrophySet set = service.ReadTrophySet(entry);

        Assert.Equal("NPWR54321_00", set.Id);
        Assert.Equal("Package Trophies", set.Name);
        Assert.Equal("Started", Assert.Single(set.Trophies).Name);
    }

    private static byte[] BuildPbp(params (int Slot, byte[] Data)[] sections)
    {
        var slots = new byte[8][];
        for (int index = 0; index < slots.Length; index++)
            slots[index] = [];
        foreach ((int slot, byte[] data) in sections)
            slots[slot] = data;

        var offsets = new uint[8];
        uint cursor = PbpArchive.HeaderSize;
        for (int index = 0; index < slots.Length; index++)
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
        foreach (byte[] slot in slots)
            output.Write(slot);
        return output.ToArray();
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "pkglens-operation-service-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public string Write(string name, byte[] content)
        {
            string path = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
