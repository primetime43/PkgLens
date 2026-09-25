using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Sfo;
using PkgLens.Core.Tests.TestData;
using PkgLens.Gui.ViewModels;
using PkgLens.Gui.Views;
using PkgLens.Gui.Views.Pages;

namespace PkgLens.Core.Tests.Gui;

public sealed class EditedPreviewTests
{
    [AvaloniaFact]
    public void ReplacementReads_CurrentBytesAndSize_ThenRevertsWithoutChangingSource()
    {
        using var fixture = new PreviewFixture();
        var package = fixture.Model.Package!;
        var operations = package.Operations;
        var entry = operations.Info.Entries.Single(item => item.Name == "USRDIR/readme.txt");
        byte[] replacement = Encoding.UTF8.GetBytes("This is the unsaved replacement text.");
        operations.ReplaceEntry(entry, replacement);
        Assert.Equal(replacement, operations.ReadEntryBytes(entry));
        Assert.Equal(replacement, operations.TryReadEntryBytes(entry.Name));
        byte[] returned = operations.ReadEntryBytes(entry);
        returned[0] = 0;
        Assert.Equal(replacement, operations.ReadEntryBytes(entry));

        package.FileFilter = "readme";
        EntryNode filtered = Assert.Single(package.CurrentItems);
        package.SelectedItem = filtered;
        Assert.True(filtered.IsModified);
        Assert.Equal((ulong)replacement.Length, filtered.Size);
        Assert.Contains("unsaved replacement", package.SelectedFileDetail);
        operations.RevertEntry(entry);
        Assert.Same(filtered, package.SelectedItem);
        Assert.False(filtered.IsModified);
        Assert.Equal((ulong)3, filtered.Size);
        Assert.Equal("old", Encoding.UTF8.GetString(operations.ReadEntryBytes(entry)));
        Assert.DoesNotContain("unsaved replacement", package.SelectedFileDetail);
        package.ClearFileFilter();
        package.OpenFolder(Assert.Single(package.RootFolder.Children, node => node.Name == "USRDIR"));
        Assert.False(Assert.Single(package.CurrentItems).IsModified);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    [AvaloniaFact]
    public void SfoEdits_UpdateOpenInfoDialogTitleAndMetadata_AndRevertRestoresThem()
    {
        using var fixture = new PreviewFixture();
        var package = fixture.Model.Package!;
        var window = new MainWindow { DataContext = fixture.Model };
        window.Show();
        var info = new PackageInfoDialog { DataContext = package };
        info.Show(window);
        package.Operations.ApplySfoEdits(PreviewFixture.Fields("Edited game", "NPUB22222", "02.50", "GD"));

        Assert.Equal("Edited game", package.Title);
        Assert.Equal("NPUB22222", package.TitleId);
        Assert.Equal("02.50", package.VersionText);
        Assert.Equal("GD", package.CategoryText);
        Assert.Contains("02.50", package.SummaryLine);
        Assert.Contains("Edited game", fixture.Model.WindowTitle);
        Assert.Contains("NPUB22222", fixture.Model.WindowTitle);
        Assert.Equal("Edited game", package.ClassificationRows.Single(row => row.Label == "Title").Value);
        Assert.Equal("02.50", package.SfoRows.Single(row => row.Key == "APP_VER").Value);
        Assert.Equal("Original game", package.Operations.Info.Sfo!.Title);
        Assert.Equal("Edited game", SfoParser.Parse(package.Operations.ReadEntryBytes(
            package.Operations.Info.Entries.Single(entry => entry.Name == "PARAM.SFO"))).Title);
        info.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(info.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Edited game");

        package.Operations.RevertEntry(package.PendingChanges.Single().Entry);
        Assert.Equal("Original game", package.Title);
        Assert.Equal("01.00", package.VersionText);
        Assert.Equal("HG", package.CategoryText);
        Assert.DoesNotContain("Edited game", fixture.Model.WindowTitle);
        Assert.Equal("Original game", package.SfoRows.Single(row => row.Key == "TITLE").Value);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
        info.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void InvalidSfoReplacement_ClearsStaleValuesAndRecoversOnReplacementOrRevert()
    {
        using var fixture = new PreviewFixture();
        var package = fixture.Model.Package!;
        var entry = package.Operations.Info.Entries.Single(item => item.Name == "PARAM.SFO");
        package.Operations.ReplaceEntry(entry, [1, 2, 3]);
        Assert.True(package.HasMetadataPreviewError);
        Assert.False(package.CanEditSfo);
        Assert.Empty(package.SfoRows);
        Assert.NotEqual("Original game", package.Title);
        Assert.Equal(new byte[] { 1, 2, 3 }, package.Operations.ReadEntryBytes(entry));

        package.Operations.ReplaceEntry(entry, SfoWriter.Write(PreviewFixture.Fields("Fixed", "NPUB33333", "03.00", "HG")));
        Assert.False(package.HasMetadataPreviewError);
        Assert.True(package.CanEditSfo);
        Assert.Equal("Fixed", package.Title);
        package.Operations.RevertEntry(entry);
        Assert.Equal("Original game", package.Title);
        Assert.Null(package.MetadataPreviewError);
    }

    [AvaloniaFact]
    public void PreviewLimitAndFilteredSizes_UseReplacementLength()
    {
        using var fixture = new PreviewFixture();
        var package = fixture.Model.Package!;
        package.FileFilter = "readme";
        package.SelectedItem = package.CurrentItems.Single();
        var node = package.SelectedItem;
        package.Operations.ReplaceEntry(node.Entry!, new byte[PackageViewModel.MaxPreviewBytes + 1]);
        Assert.False(package.CanPreviewSelected);
        Assert.Equal((ulong)PackageViewModel.MaxPreviewBytes + 1, node.Size);
        package.Operations.ReplaceEntry(node.Entry!, []);
        Assert.True(package.CanPreviewSelected);
        Assert.Equal("0 B", node.SizeDisplay);
        Assert.Empty(package.Operations.ReadEntryBytes(node.Entry!));
        package.Operations.RevertEntry(node.Entry!);
        Assert.True(package.CanPreviewSelected);
        Assert.Equal("3 B", node.SizeDisplay);
    }

    [AvaloniaFact]
    public async Task ViewAction_ShowsReplacementTextWithUnsavedLabel_ThenOriginalAfterRevert()
    {
        using var fixture = new PreviewFixture();
        var package = fixture.Model.Package!;
        var window = new MainWindow { DataContext = fixture.Model };
        fixture.Model.ActiveTool = ToolPage.Package;
        window.Show();
        package.FileFilter = "readme";
        package.SelectedItem = package.CurrentItems.Single();
        var entry = package.SelectedItem.Entry!;
        package.Operations.ReplaceEntry(entry, Encoding.UTF8.GetBytes("Edited preview text"));
        var page = window.FindControl<PackagePage>("PackagePage")!;
        var grid = page.GetVisualDescendants().OfType<DataGrid>().Single();
        var view = grid.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "View"));
        view.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var viewer = await WaitForViewer(window);
        Assert.Contains("Unsaved preview", viewer.Title);
        Assert.Equal("Edited preview text", viewer.FindControl<TextBox>("TextArea")!.Text);
        viewer.Close();
        package.Operations.RevertEntry(entry);
        view.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        viewer = await WaitForViewer(window);
        Assert.DoesNotContain("Unsaved preview", viewer.Title);
        Assert.Equal("old", viewer.FindControl<TextBox>("TextArea")!.Text);
        viewer.Close();
        window.Close();
    }

    private static async Task<FileViewerDialog> WaitForViewer(Window owner)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!owner.OwnedWindows.OfType<FileViewerDialog>().Any())
            await Task.Delay(10, timeout.Token);
        return owner.OwnedWindows.OfType<FileViewerDialog>().Single();
    }

    [AvaloniaFact]
    public void IconReplacement_RefreshesArtworkAndImagePreview_InvalidImageHidesArtwork_RevertRestoresIt()
    {
        using var fixture = new PreviewFixture();
        var package = fixture.Model.Package!;
        var entry = package.Operations.Info.Entries.Single(item => item.Name == "ICON0.PNG");
        Assert.Equal(new PixelSize(1, 1), package.Icon!.PixelSize);
        using var image = new RenderTargetBitmap(new PixelSize(2, 3));
        using var data = new MemoryStream();
        image.Save(data);
        package.Operations.ReplaceEntry(entry, data.ToArray());
        Assert.Equal(new PixelSize(2, 3), package.Icon!.PixelSize);
        var viewer = new FileViewerDialog(entry.Name, package.Operations.ReadEntryBytes(entry), true);
        Assert.True(viewer.FindControl<ScrollViewer>("ImageScroll")!.IsVisible);
        Assert.Equal(new PixelSize(2, 3), Assert.IsType<Bitmap>(viewer.FindControl<Image>("Preview")!.Source).PixelSize);
        viewer.Close();

        package.Operations.ReplaceEntry(entry, [0x00, 0xAA, 0xFF]);
        Assert.False(package.HasIcon);
        var binary = new FileViewerDialog(entry.Name, package.Operations.ReadEntryBytes(entry), true);
        Assert.Contains("00 AA FF", binary.FindControl<TextBox>("TextArea")!.Text);
        Assert.False(binary.FindControl<ScrollViewer>("ImageScroll")!.IsVisible);
        binary.Close();
        package.Operations.RevertEntry(entry);
        Assert.True(package.HasIcon);
        Assert.Equal(new PixelSize(1, 1), package.Icon!.PixelSize);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    private sealed class PreviewFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "pkglens-previews-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(_directory, "source.pkg");
        public byte[] Original { get; }
        public MainWindowViewModel Model { get; }

        public static SfoEntry[] Fields(string title, string id, string version, string category) =>
        [
            new() { Key = "TITLE", Format = SfoFormat.Utf8, Value = title },
            new() { Key = "TITLE_ID", Format = SfoFormat.Utf8, Value = id },
            new() { Key = "APP_VER", Format = SfoFormat.Utf8, Value = version },
            new() { Key = "CATEGORY", Format = SfoFormat.Utf8, Value = category },
        ];

        public PreviewFixture()
        {
            Directory.CreateDirectory(_directory);
            Original = new SyntheticPkgBuilder().AddFile("USRDIR/readme.txt", "old")
                .AddFile("ICON0.PNG", Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="))
                .AddFile("PARAM.SFO", SfoWriter.Write(Fields("Original game", "NPUB11111", "01.00", "HG"))).Build();
            File.WriteAllBytes(Source, Original);
            Model = new MainWindowViewModel { Package = PackageViewModel.Load(Source, new InMemoryKeyProvider()) };
        }

        public void Dispose()
        {
            Model.Package = null;
            Directory.Delete(_directory, recursive: true);
        }
    }
}
