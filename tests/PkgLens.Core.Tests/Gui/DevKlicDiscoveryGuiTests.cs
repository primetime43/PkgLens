using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PkgLens.Core.Ps3.Npd;
using PkgLens.Core.Tests.TestData;
using PkgLens.Gui.Views;

namespace PkgLens.Core.Tests.Gui;

public sealed class DevKlicDiscoveryGuiTests
{
    [AvaloniaFact]
    public async Task ConfirmedMatchCanBeSaved_AndChangedSettingsInvalidateIt()
    {
        using var fixture = new DevKlicFixture();
        var owner = new Window(); owner.Show();
        var dialog = new DevKlicDiscoveryDialog(fixture.Raps, fixture.Database);
        try
        {
            dialog.SetTarget(fixture.Target); dialog.SetExecutable(fixture.Executable);
            var result = dialog.ShowDialog<bool>(owner);
            Dispatcher.UIThread.RunJobs();
            await dialog.SearchAsync();
            Assert.True(dialog.FindControl<Button>("SaveKeyButton")!.IsEnabled);
            Assert.Contains("Confirmed", dialog.FindControl<TextBlock>("ResultText")!.Text);
            Assert.False(File.Exists(fixture.Database));
            dialog.FindControl<TextBox>("ExecutableKey")!.Text = "changed";
            Dispatcher.UIThread.RunJobs();
            Assert.False(dialog.FindControl<Button>("SaveKeyButton")!.IsEnabled);
            dialog.FindControl<TextBox>("ExecutableKey")!.Text = "";
            Dispatcher.UIThread.RunJobs();
            await dialog.SearchAsync();
            await dialog.SaveAsync();
            Assert.True(await result);
            Assert.NotNull(KlicenseeStore.Find(DevKlicFixture.ContentId, "target.edat", 3, fixture.Database));
        }
        finally { dialog.Close(); owner.Close(); }
    }
}
