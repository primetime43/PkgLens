using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Tests.TestData;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;
using PkgLens.Gui.Views;
using PkgLens.Gui.Views.Pages;

namespace PkgLens.Core.Tests.Gui;

public class SelfBuildTests
{
    [Fact]
    public void RebuildUsesSeparateKeysAndPreservesInputAndMetadata()
    {
        using var fixture = new DevKlicFixture();
        byte[] elf = DevKlicFixture.Elf(2048);
        byte[] original = EncryptedSelfBuilder.Build(elf, new()
        {
            Metadata = new() { Npdrm = true, ContentId = DevKlicFixture.ContentId, NpLicenseType = 2,
                AuthId = 0x1010000001000007, VendorId = 0x1234, AppVersion = 0x0003005500000000,
                FirmwareVersion = 35500, ControlFlags = Enumerable.Repeat((byte)1,32).ToArray() },
            Klicensee = DevKlicFixture.Key,
        });
        File.WriteAllBytes(fixture.Executable, original);
        string dest = Path.Combine(fixture.Root, "rebuilt.self");
        string rap = Path.Combine(fixture.Root, "output.rap");
        byte[] rapBytes = Enumerable.Range(0,16).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(rap, rapBytes);
        var metadata = new SelfBuilder.FakeSelfOptions { Npdrm = true, NpLicenseType = 2 };
        SelfBuildService.BuildFile(fixture.Executable, dest, true, metadata, 0x0A,
            new(Convert.ToHexString(DevKlicFixture.Key)), new(RapPath: rap), fixture.Raps);
        byte[] result = File.ReadAllBytes(dest);
        Assert.Equal(original, File.ReadAllBytes(fixture.Executable));
        Assert.Equal(elf, SelfDecryptor.Decrypt(result, NpdKeys.RapToKlicensee(rapBytes)).Elf);
        Assert.Throws<PkgFormatException>(() => SelfDecryptor.Decrypt(result, DevKlicFixture.Key));
        SelfInfo info = SelfReader.ParseInfo(new MemoryStream(result));
        Assert.Equal(0x1010000001000007UL, info.AuthId);
        Assert.Equal(0x1234u, info.VendorId);
        Assert.Equal(35500UL, info.FirmwareVersion);
        Assert.Equal(DevKlicFixture.ContentId, info.Npdrm!.ContentId);
        Assert.Null(metadata.AuthId);
        Assert.Null(metadata.ContentId);
        Assert.False(Directory.Exists(fixture.Raps)); // Selecting a RAP never installs it implicitly.
    }

    [Fact]
    public void FailureOrCancellationNeverOverwritesDestination()
    {
        using var fixture = new DevKlicFixture();
        string dest = Path.Combine(fixture.Root, "keep.self");
        byte[] previous = "Existing destination"u8.ToArray(); File.WriteAllBytes(dest, previous);
        Assert.Throws<PkgFormatException>(() => SelfBuildService.BuildFile(fixture.Executable, dest,
            true, new(), 0x7F, new(), new(), fixture.Raps));
        Assert.Equal(previous, File.ReadAllBytes(dest));
        Assert.Throws<OperationCanceledException>(() => SelfBuildService.BuildFile(fixture.Executable, dest,
            true, new(), 0x0A, new(), new(), fixture.Raps, new CancellationToken(true)));
        Assert.Equal(previous, File.ReadAllBytes(dest));
        Assert.Throws<IOException>(() => SelfBuildService.BuildFile(fixture.Executable, fixture.Executable,
            true, new(), 0x0A, new(), new(), fixture.Raps));
    }

    [Fact]
    public void FakeSelfInputDoesNotRequireItsTaggedLocalLicense()
    {
        using var fixture = new DevKlicFixture();
        var elf = DevKlicFixture.Elf(1024);
        File.WriteAllBytes(fixture.Executable, SelfBuilder.MakeFakeSelf(elf, npdrm: true));
        string dest = Path.Combine(fixture.Root, "out.self");
        SelfBuildService.BuildFile(fixture.Executable, dest, true, new(), 0x0A, new(), new(), fixture.Raps);
        Assert.Equal(elf, SelfDecryptor.Decrypt(File.ReadAllBytes(dest)).Elf);
        SelfBuildService.BuildFile(dest, fixture.Executable, false, new(), 0x0A, new(), new(), fixture.Raps);
        Assert.Equal(elf, SelfDecryptor.Decrypt(File.ReadAllBytes(fixture.Executable)).Elf);
    }

    [AvaloniaFact]
    public void BuildPageLoadsEncryptedDefaultAndSeparateKeyFields()
    {
        var window = new MainWindow { DataContext = new MainWindowViewModel { ActiveTool = ToolPage.Resign } };
        window.Show();
        var page = window.FindControl<ResignPage>("ResignPage")!;
        window.OnResignCardClick(new Button { Tag = "fakesign" }, new());
        Assert.True(page.FindControl<Control>("ResignOpFakeSign")!.IsVisible);
        Assert.True(page.FindControl<CheckBox>("SelfEncryptCheck")!.IsChecked);
        Assert.NotSame(page.FindControl<TextBox>("SelfInputKeyBox"), page.FindControl<TextBox>("SelfOutputKeyBox"));
        Assert.Equal("0A", page.FindControl<TextBox>("SelfRevisionBox")!.Text);
        var signing = page.FindControl<CheckBox>("SelfSignCheck")!;
        var profiles = page.FindControl<ComboBox>("SelfSigningProfileBox")!;
        signing.IsChecked = true;
        Assert.False(page.FindControl<Grid>("SelfRevisionGrid")!.IsVisible);
        Assert.Equal(4u, Assert.IsType<LegacySelfProfile>(profiles.SelectedItem).ProgramType);
        profiles.SelectedItem = profiles.Items.Cast<LegacySelfProfile>().First(p => p.Revision == 0);
        page.FindControl<CheckBox>("FselfNpdrmCheck")!.IsChecked = true;
        Assert.Equal(8u, Assert.IsType<LegacySelfProfile>(profiles.SelectedItem).ProgramType);
        Assert.Equal(10, Assert.IsType<LegacySelfProfile>(profiles.SelectedItem).Revision);
        Assert.Equal(4, profiles.ItemCount);
        signing.IsChecked = false;
        Assert.True(page.FindControl<Grid>("SelfRevisionGrid")!.IsVisible);
        page.FindControl<CheckBox>("SelfEncryptCheck")!.IsChecked = false;
        Assert.False(page.FindControl<Grid>("SelfRevisionGrid")!.IsVisible);
        window.Close();
    }

    [Fact]
    public void LegacySigningIsVerifiedAndUnsupportedRequestsPreserveDestination()
    {
        using var fixture = new DevKlicFixture();
        byte[] source = File.ReadAllBytes(fixture.Executable);
        string output = Path.Combine(fixture.Root, "signed.self");
        SelfBuildService.BuildFile(fixture.Executable, output, true, new(), 0x0A,
            new(), new(), fixture.Raps, signHeader: true);
        byte[] signed = File.ReadAllBytes(output);
        Assert.Equal(SelfSignatureStatus.Valid, SelfSignature.VerifyHeader(signed));
        Assert.Equal(source, File.ReadAllBytes(fixture.Executable));
        Assert.Throws<PkgFormatException>(() => SelfBuildService.BuildFile(fixture.Executable, output,
            true, new(), 0x10, new(), new(), fixture.Raps, signHeader: true));
        Assert.Equal(signed, File.ReadAllBytes(output));
        Assert.Throws<PkgFormatException>(() => SelfBuildService.BuildFile(fixture.Executable, output,
            false, new(), 0x0A, new(), new(), fixture.Raps, signHeader: true));
        Assert.Equal(signed, File.ReadAllBytes(output));
    }
}
