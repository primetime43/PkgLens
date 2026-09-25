using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Tests.TestData;
using PkgLens.Gui.ViewModels;
using PkgLens.Gui.Views;

namespace PkgLens.Core.Tests.Gui;

public sealed class EdatWorkbenchTests
{
    [AvaloniaFact]
    public async Task KeyCheck_ReportsMatchWithoutEnablingSave_AndClearsStaleMatch()
    {
        var dialog = new EdatWorkbenchDialog();
        dialog.Show();
        try
        {
            dialog.LoadPackageEntry("source.edat", Protected("Content to verify"u8.ToArray()), "unused.pkg");
            Dispatcher.UIThread.RunJobs();
            await dialog.CheckKeyAsync();
            Assert.Contains("Key matches", dialog.FindControl<TextBlock>("KeyCheckStatus")!.Text);
            Assert.Contains("Built-in free", dialog.FindControl<TextBlock>("KeyCheckStatus")!.Text);
            Assert.False(dialog.FindControl<Button>("SaveButton")!.IsEnabled);
            Assert.False(dialog.FindControl<Button>("StageButton")!.IsEnabled);
            dialog.FindControl<TextBox>("InputRawKey")!.Text = new string('0', 32);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("not checked", dialog.FindControl<TextBlock>("KeyCheckStatus")!.Text);
            await dialog.CheckKeyAsync();
            Assert.Contains("Key not confirmed", dialog.FindControl<TextBlock>("KeyCheckStatus")!.Text);
            Assert.True(dialog.FindControl<Button>("CheckKeyButton")!.IsEnabled);
            dialog.FindControl<ComboBox>("Operation")!.SelectedIndex = 1;
            Assert.False(dialog.FindControl<StackPanel>("InputKeyPanel")!.IsVisible);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task PackageRebuild_UsesPendingContentAndStagesVerifiedResult()
    {
        string root = Path.Combine(Path.GetTempPath(), "pkglens-workbench-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var owner = new Window();
        owner.Show();
        var dialog = new EdatWorkbenchDialog();
        try
        {
            string source = Path.Combine(root, "source.pkg");
            byte[] original = new SyntheticPkgBuilder().AddFile("USRDIR/source.edat", Protected("Original"u8.ToArray())).Build();
            File.WriteAllBytes(source, original);
            using var package = PackageViewModel.Load(source, new InMemoryKeyProvider());
            var entry = package.Operations.Info.Entries.Single(e => e.Name == "USRDIR/source.edat");
            package.Operations.ReplaceEntry(entry, Protected("Pending edit"u8.ToArray()));
            dialog.LoadPackageEntry("source.edat", package.Operations.ReadEntryBytes(entry), source);
            var staged = dialog.ShowDialog<byte[]?>(owner);
            await dialog.BuildAsync();
            Assert.True(dialog.FindControl<Button>("PreviewButton")!.IsEnabled);
            Assert.True(dialog.FindControl<Button>("StageButton")!.IsEnabled);
            dialog.FindControl<Button>("StageButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            byte[] rebuilt = Assert.IsType<byte[]>(await staged);
            package.Operations.ReplaceEntry(entry, rebuilt);
            Assert.Equal("Pending edit"u8.ToArray(), EdatFile.DecryptToArray(new MemoryStream(package.Operations.ReadEntryBytes(entry))));
            Assert.True(package.HasPendingChanges);
            Assert.Equal(original, File.ReadAllBytes(source));
        }
        finally { dialog.Close(); owner.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task ChangedSettingsOrFailedBuild_DisableStaleResults()
    {
        var dialog = new EdatWorkbenchDialog();
        dialog.Show();
        try
        {
            dialog.LoadPackageEntry("source.edat", Protected("Preview content"u8.ToArray()), "unused.pkg");
            await dialog.BuildAsync();
            Assert.True(dialog.FindControl<Button>("SaveButton")!.IsEnabled);
            dialog.FindControl<TextBox>("InputRawKey")!.Text = "invalid";
            Dispatcher.UIThread.RunJobs();
            Assert.False(dialog.FindControl<Button>("PreviewButton")!.IsEnabled);
            Assert.False(dialog.FindControl<Button>("SaveButton")!.IsEnabled);
            Assert.False(dialog.FindControl<Button>("StageButton")!.IsEnabled);
            await dialog.BuildAsync();
            Assert.False(dialog.FindControl<Button>("SaveButton")!.IsEnabled);
            Assert.Contains("32 hexadecimal", dialog.FindControl<TextBlock>("Status")!.Text);
            dialog.FindControl<ComboBox>("Operation")!.SelectedIndex = 3;
            dialog.FindControl<ComboBox>("Format")!.SelectedIndex = 1;
            Assert.False(dialog.FindControl<TextBox>("OutputRawKey")!.IsVisible);
            Assert.False(dialog.FindControl<Grid>("OutputRapPanel")!.IsVisible);
        }
        finally { dialog.Close(); }
    }

    private static byte[] Protected(byte[] plaintext)
    {
        using var output = new MemoryStream();
        EdatWriter.WriteVerified(new MemoryStream(plaintext), output, new EdatWriteOptions
        { ContentId = "UP0001-NPUB12345_00-EDATTEST00000001", FileName = "source.edat" });
        return output.ToArray();
    }
}
