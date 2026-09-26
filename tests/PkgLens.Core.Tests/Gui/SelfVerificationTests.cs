using System.Buffers.Binary;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Ps3.Self;
using PkgLens.Core.Shared;
using PkgLens.Core.Tests.TestData;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;
using PkgLens.Gui.Views;
using PkgLens.Gui.Views.Pages;

namespace PkgLens.Core.Tests.Gui;

public class SelfVerificationTests
{
    private static byte[] Signed() => EncryptedSelfBuilder.Build(DevKlicFixture.Elf(2048), new() { SignHeader = true });
    private static SelfVerificationReport Check(byte[] bytes) => SelfVerificationService.Verify(bytes, "EBOOT.BIN", new());

    [Fact]
    public void ValidLegacySelfIsReadOnlyAndReturnsIndependentResults()
    {
        using var fixture = new DevKlicFixture();
        byte[] bytes = Signed();
        File.WriteAllBytes(fixture.Executable, bytes);
        var files = Directory.GetFiles(fixture.Root).Order().ToArray();
        var report = SelfVerificationService.VerifyFile(fixture.Executable, new());
        Assert.Equal("Valid", report.Signature.Status);
        Assert.Equal("Succeeded", report.Decryption.Status);
        Assert.Equal(SelfTargetFormat.CexRetail, report.Target.Format);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Executable));
        Assert.Equal(files, Directory.GetFiles(fixture.Root).Order());
    }

    [Fact]
    public void InvalidHeaderSignatureDoesNotHideSuccessfulDecryption()
    {
        byte[] bytes = Signed();
        bytes[0x70] ^= 1; // Auth ID is signed but does not affect ELF recovery.
        var report = Check(bytes);
        Assert.Equal("Invalid", report.Signature.Status);
        Assert.Equal("Succeeded", report.Decryption.Status);
    }

    [Fact]
    public void TruncatedPayloadCanFailDecryptionWithAValidHeaderSignature()
    {
        byte[] bytes = Signed();
        int headerLength = checked((int)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(0x10)));
        var report = Check(bytes[..headerLength]);
        Assert.Equal("Valid", report.Signature.Status);
        Assert.Equal("Failed", report.Decryption.Status);
    }

    [Fact]
    public void UnsignedRetailAndFakeSelfAreNotCalledValid()
    {
        byte[] elf = DevKlicFixture.Elf(1024);
        foreach (var bytes in new[] { EncryptedSelfBuilder.Build(elf, new()), SelfBuilder.MakeFakeSelf(elf, false) })
        {
            var report = Check(bytes);
            Assert.Equal("Unsigned", report.Signature.Status);
            Assert.Equal("Succeeded", report.Decryption.Status);
        }
    }

    [Fact]
    public void UnsupportedSignatureCanStillDecryptAndUnknownRevisionStaysUnsupported()
    {
        byte[] bytes = EncryptedSelfBuilder.Build(DevKlicFixture.Elf(1024), new() { KeyRevision = 0x10 });
        var report = Check(bytes);
        Assert.Equal("Unsupported", report.Signature.Status);
        Assert.Equal("Succeeded", report.Decryption.Status);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), 0x1234);
        report = Check(bytes);
        Assert.Equal("Unsupported", report.Signature.Status);
        Assert.Equal("Unsupported", report.Decryption.Status);
    }

    [Fact]
    public void MissingWrongAndCorrectNpdrmKeysHaveDistinctResults()
    {
        using var fixture = new DevKlicFixture();
        byte[] bytes = EncryptedSelfBuilder.Build(DevKlicFixture.Elf(1024), new()
        {
            SignHeader = true, Klicensee = DevKlicFixture.Key,
            Metadata = new() { Npdrm = true, ContentId = "UP0001-NPUB98761_00-VERIFYKEYTEST001", NpLicenseType = 2 },
        });
        var missing = SelfVerificationService.Verify(bytes, "EBOOT.BIN", new(), fixture.Raps);
        Assert.Equal("Missing key", missing.Signature.Status);
        Assert.Equal("Missing key", missing.Decryption.Status);
        var wrong = SelfVerificationService.Verify(bytes, "EBOOT.BIN", new(new string('0', 32)), fixture.Raps);
        Assert.Equal("Failed", wrong.Signature.Status);
        Assert.Equal("Failed", wrong.Decryption.Status);
        var correct = SelfVerificationService.Verify(bytes, "EBOOT.BIN", new(Convert.ToHexString(DevKlicFixture.Key)), fixture.Raps);
        Assert.Equal("Valid", correct.Signature.Status);
        Assert.Equal("Succeeded", correct.Decryption.Status);
        var malformed = SelfVerificationService.Verify(bytes, "EBOOT.BIN", new("invalid"), fixture.Raps);
        Assert.Equal("Key error", malformed.Decryption.Status);
    }

    [Fact]
    public void FreeLicenseAndSelectedOrLibraryRapResolveAutomatically()
    {
        using var fixture = new DevKlicFixture();
        var elf = DevKlicFixture.Elf(1024);
        byte[] free = EncryptedSelfBuilder.Build(elf, new() { SignHeader = true,
            Metadata = new() { Npdrm = true, ContentId = DevKlicFixture.ContentId, NpLicenseType = 3 } });
        Assert.Equal("Valid", Check(free).Signature.Status);
        const string id = "UP0001-NPUB98761_00-VERIFYRAPTEST001";
        byte[] rap = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        byte[] licensed = EncryptedSelfBuilder.Build(elf, new() { SignHeader = true, Klicensee = NpdKeys.RapToKlicensee(rap),
            Metadata = new() { Npdrm = true, ContentId = id, NpLicenseType = 2 } });
        Directory.CreateDirectory(fixture.Raps);
        string path = Path.Combine(fixture.Raps, id + ".rap");
        File.WriteAllBytes(path, rap);
        foreach (var selection in new[] { new EdatKeySelection(), new EdatKeySelection(RapPath: path) })
        {
            var result = SelfVerificationService.Verify(licensed, "EBOOT.BIN", selection, fixture.Raps);
            Assert.Equal("Valid", result.Signature.Status);
            Assert.Equal("Succeeded", result.Decryption.Status);
        }
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Equal("Key error", SelfVerificationService.Verify(licensed, "EBOOT.BIN", new(RapPath: path), fixture.Raps).Signature.Status);
    }

    [Fact]
    public void BadMetadataIsAFailureAndForeignHeadersAreUnsupported()
    {
        byte[] bytes = Signed();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), uint.MaxValue);
        var report = Check(bytes);
        Assert.Equal("Failed", report.Signature.Status);
        Assert.Equal("Failed", report.Decryption.Status);
        bytes[7] = 3;
        Assert.Equal("Unsupported", Check(bytes).Signature.Status);
        Assert.Throws<PkgFormatException>(() => Check("not a self"u8.ToArray()));
    }

    [Fact]
    public void SizeLimitAndCancellationStopBeforeVerification()
    {
        using var fixture = new DevKlicFixture();
        using (var stream = File.Create(fixture.Executable)) stream.SetLength(SelfVerificationService.MaxInputBytes + 1L);
        Assert.Throws<PkgFormatException>(() => SelfVerificationService.VerifyFile(fixture.Executable, new()));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SelfVerificationService.VerifyFile(fixture.Executable, new(), token: cancellation.Token));
    }

    [AvaloniaFact]
    public async Task DialogClearsOldResultsOnKeyChangeAndFileFailure()
    {
        using var fixture = new DevKlicFixture();
        File.WriteAllBytes(fixture.Executable, Signed());
        var dialog = new VerifySelfDialog(fixture.Raps); dialog.Show();
        await dialog.VerifyAsync(fixture.Executable);
        Assert.Equal("Header signature: Valid", dialog.FindControl<TextBlock>("SignatureText")!.Text);
        Assert.Equal("Decryption: Succeeded", dialog.FindControl<TextBlock>("DecryptionText")!.Text);
        Assert.Contains("Signature status is shown separately", dialog.FindControl<TextBlock>("TargetDetail")!.Text);
        Assert.True(dialog.FindControl<StackPanel>("ResultsPanel")!.IsVisible);
        dialog.FindControl<TextBox>("KeyBox")!.Text = "00";
        Dispatcher.UIThread.RunJobs();
        Assert.False(dialog.FindControl<StackPanel>("ResultsPanel")!.IsVisible);
        await dialog.VerifyAsync(fixture.Executable);
        Assert.True(dialog.FindControl<StackPanel>("ResultsPanel")!.IsVisible);
        await dialog.VerifyAsync(Path.Combine(fixture.Root, "missing.self"));
        Assert.False(dialog.FindControl<StackPanel>("ResultsPanel")!.IsVisible);
        Assert.Contains("Could not verify", dialog.FindControl<TextBlock>("StatusText")!.Text);
        Assert.True(dialog.FindControl<Button>("BrowseButton")!.IsEnabled);
        Assert.False(dialog.FindControl<ProgressBar>("Progress")!.IsVisible);
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task ClosingDuringVerificationCancelsAndClosesWithoutShowingAStaleResult()
    {
        using var fixture = new DevKlicFixture();
        File.WriteAllBytes(fixture.Executable, Signed());
        var dialog = new VerifySelfDialog(fixture.Raps); dialog.Show();
        Task verification = dialog.VerifyAsync(fixture.Executable);
        dialog.Close();
        await verification;
        Assert.False(dialog.IsVisible);
        Assert.False(dialog.FindControl<StackPanel>("ResultsPanel")!.IsVisible);
    }

    [AvaloniaFact]
    public void VerificationIsReachableWithoutAPackage()
    {
        var operation = OperationCatalog.All.Single(o => o.Id == OperationId.VerifySelf);
        Assert.Equal(OperationEligibilityRule.Always, operation.MenuEligibility);
        var window = new MainWindow { DataContext = new MainWindowViewModel { ActiveTool = ToolPage.Resign } };
        window.Show();
        var page = window.FindControl<ResignPage>("ResignPage")!;
        page.FindControl<Button>("VerifySelfButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsType<VerifySelfDialog>(Assert.Single(window.OwnedWindows)).Close();
        window.Close();
    }
}
