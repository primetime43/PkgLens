using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(PkgLens.Core.Tests.Gui.HeadlessTestApp))]

namespace PkgLens.Core.Tests.Gui;

public static class HeadlessTestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<PkgLens.Gui.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
