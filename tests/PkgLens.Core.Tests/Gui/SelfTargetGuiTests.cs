using Avalonia.Headless.XUnit;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Tests.TestData;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Core.Tests.Gui;

public class SelfTargetGuiTests
{
    [AvaloniaFact]
    public void SelectionShowsEmbeddedFormatAndTracksReplacementAndRevert()
    {
        string root = Path.Combine(Path.GetTempPath(), "pkglens-target-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "test.pkg");
            // A debug PKG can contain retail SELF files: classify each file, not its container.
            File.WriteAllBytes(path, new SyntheticPkgBuilder()
                .AddFile("EBOOT.BIN", new SyntheticSelfBuilder().Build())
                .AddFile("readme.txt", "Hello").Build());
            using var model = PackageViewModel.Load(path, new InMemoryKeyProvider());
            var node = model.RootFolder.Children.Single(n => n.Name == "EBOOT.BIN");
            model.SelectedItem = node;
            Assert.True(model.HasSelectedTarget);
            Assert.Contains("CEX / retail", model.SelectedTargetLabel);
            model.Operations.ReplaceEntry(node.Entry!, SelfBuilder.MakeFakeSelf(DevKlicFixture.Elf(1024), false));
            Assert.Contains("DEX / debug", model.SelectedTargetLabel);
            Assert.Contains("also run on CEX", model.SelectedTargetDetail);
            model.Operations.RevertEntry(node.Entry!);
            Assert.Contains("CEX / retail", model.SelectedTargetLabel);
            model.Operations.ReplaceEntry(node.Entry!, [1, 2, 3]);
            Assert.Contains("Unknown", model.SelectedTargetLabel);
            model.SelectedItem = model.RootFolder.Children.Single(n => n.Name == "readme.txt");
            Assert.False(model.HasSelectedTarget);
            Assert.Empty(model.SelectedTargetDetail);
            model.SelectedItem = model.RootFolder;
            Assert.False(model.HasSelectedTarget);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InspectionAndFolderScanExposeSourceFormat()
    {
        string root = Path.Combine(Path.GetTempPath(), "pkglens-target-" + Guid.NewGuid());
        string source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        try
        {
            byte[] elf = DevKlicFixture.Elf(1024);
            byte[] retail = EncryptedSelfBuilder.Build(elf, new());
            File.WriteAllBytes(Path.Combine(source, "retail.self"), retail);
            File.WriteAllBytes(Path.Combine(source, "debug.self"), SelfBuilder.MakeFakeSelf(elf, false));
            File.WriteAllBytes(Path.Combine(source, "plain.elf"), elf);
            var plan = SelfFolderService.Scan(source, Path.Combine(root, "output"), new(SelfFolderOperation.Decrypt));
            Assert.Equal(SelfTargetFormat.CexRetail, plan.Jobs.Single(j => j.RelativePath == "retail.self").Target.Format);
            Assert.Equal(SelfTargetFormat.DexDebug, plan.Jobs.Single(j => j.RelativePath == "debug.self").Target.Format);
            Assert.Equal(SelfTargetFormat.Unknown, plan.Jobs.Single(j => j.RelativePath == "plain.elf").Target.Format);
            Assert.Contains("CEX / retail", PackagePresentationService.DescribeSelf(SelfReader.ParseInfo(new MemoryStream(retail))));
        }
        finally { Directory.Delete(root, true); }
    }
}
