using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Sfo;
using PkgLens.Core.Tests.TestData;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;
using PkgLens.Gui.Views;

namespace PkgLens.Core.Tests.Gui;

public sealed class PendingChangesTests
{
    [AvaloniaFact]
    public void Review_RevertsOnlyChosenFileAndUpdatesUnsavedState()
    {
        using var fixture = new EditFixture();
        var package = fixture.Model.Package!;
        var operations = package.Operations;
        var second = operations.Info.Entries.Single(entry => entry.Name == "B.bin");
        operations.ReplaceEntry(second, [7, 8, 9]);
        operations.ReplaceEntry(second, [6, 5]);
        Assert.Equal(2, package.PendingChangeCount);
        Assert.Equal((ulong)2, package.PendingChanges.Single(change => change.Path == "B.bin").ReplacementSize);

        var owner = new MainWindow { DataContext = fixture.Model };
        owner.Show();
        Assert.Contains("Unsaved changes", owner.Title);
        Assert.True(owner.FindControl<Border>("PendingChangesBar")!.IsVisible);
        var dialog = new PendingChangesDialog { DataContext = package };
        dialog.Show(owner);
        dialog.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Button revert = dialog.GetVisualDescendants().OfType<Button>().Single(button =>
            button.Tag is PendingPackageChange { Path: "B.bin" });
        revert.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("A.bin", Assert.Single(package.PendingChanges).Path);
        Assert.Equal(new byte[] { 2 }, operations.ReadEntryBytes(second));

        operations.RevertEntry(Assert.Single(package.PendingChanges).Entry);
        Assert.False(package.HasPendingChanges);
        Assert.False(dialog.FindControl<Button>("SaveChangesButton")!.IsEnabled);
        Assert.False(owner.FindControl<Border>("PendingChangesBar")!.IsVisible);
        Assert.DoesNotContain("Unsaved changes", owner.Title);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
        dialog.Close();
        owner.Close();
    }

    [AvaloniaFact]
    public void Sfo_ReopeningUsesStagedEditsAndRevertRestoresOriginal()
    {
        using var fixture = new EditFixture();
        var operations = fixture.Model.Package!.Operations;
        operations.ApplySfoEdits([new SfoEntry { Key = "TITLE", Format = SfoFormat.Utf8, Value = "Changed title" }]);
        Assert.Equal("Changed title", operations.Sfo!.Title);
        var change = operations.PendingChanges.Single(item => item.Path == "PARAM.SFO");
        operations.RevertEntry(change.Entry);
        Assert.Equal("Original title", operations.Sfo.Title);
        Assert.Equal("A.bin", Assert.Single(operations.PendingChanges).Path);
    }

    [AvaloniaFact]
    public async Task Cancel_PreservesEditsForOpenRecentReloadAndClose()
    {
        using var fixture = new EditFixture();
        var model = fixture.Model;
        var original = model.Package;
        int prompts = 0;
        model.ConfirmPackageChanges = _ => { prompts++; return Task.FromResult(false); };

        await model.LoadAsync(fixture.Other);
        await model.LoadRecentAsync(new RecentPackageItem(fixture.Other));
        await model.LoadAsync(fixture.Source);
        await model.CloseFileAsync();

        Assert.Equal(4, prompts);
        Assert.Same(original, model.Package);
        Assert.True(model.Package!.HasPendingChanges);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    [AvaloniaFact]
    public async Task MissingConfirmationHandler_DoesNotDiscardChanges()
    {
        using var fixture = new EditFixture();
        await fixture.Model.CloseFileAsync();
        Assert.NotNull(fixture.Model.Package);
        Assert.True(fixture.Model.Package.HasPendingChanges);
    }

    [AvaloniaFact]
    public async Task Discard_OpenFailureKeepsSessionButSuccessfulOpenReplacesIt()
    {
        using var fixture = new EditFixture();
        var model = fixture.Model;
        var original = model.Package;
        model.ConfirmPackageChanges = _ => Task.FromResult(true);
        await model.LoadAsync(Path.Combine(fixture.DirectoryPath, "missing.pkg"));
        Assert.Same(original, model.Package);
        Assert.True(model.Package!.HasPendingChanges);

        await model.LoadAsync(fixture.Other);
        Assert.Equal(fixture.Other, model.Package!.FilePath);
        Assert.False(model.Package.HasPendingChanges);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    [AvaloniaFact]
    public async Task SaveCopy_OpensVerifiedCopyAsNewRevertBaseline()
    {
        using var fixture = new EditFixture();
        var model = fixture.Model;
        string output = Path.Combine(fixture.DirectoryPath, "saved.pkg");
        Assert.True(await model.SavePackageAsAsync(output), model.Status);
        var package = model.Package!;
        Assert.Equal(output, package.FilePath);
        Assert.False(package.HasPendingChanges);
        Assert.DoesNotContain("Unsaved changes", model.WindowTitle);
        var entry = package.Operations.Info.Entries.Single(item => item.Name == "A.bin");
        Assert.Equal(new byte[] { 9, 8 }, package.Operations.ReadEntryBytes(entry));
        package.Operations.ReplaceEntry(entry, [3]);
        package.Operations.RevertEntry(entry);
        Assert.Equal(new byte[] { 9, 8 }, package.Operations.ReadEntryBytes(entry));
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    [AvaloniaFact]
    public async Task FailedSave_StopsCloseAndKeepsEdits()
    {
        using var fixture = new EditFixture();
        var model = fixture.Model;
        var original = model.Package;
        model.ConfirmPackageChanges = _ => model.SavePackageAsAsync(fixture.Source);
        await model.CloseFileAsync();
        Assert.Same(original, model.Package);
        Assert.True(model.Package!.HasPendingChanges);
        Assert.StartsWith("Save failed:", model.Status);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    [AvaloniaFact]
    public async Task SaveBeforeClosing_PreservesEditsInCopyAndThenCloses()
    {
        using var fixture = new EditFixture();
        var model = fixture.Model;
        string output = Path.Combine(fixture.DirectoryPath, "before-close.pkg");
        model.ConfirmPackageChanges = _ => model.SavePackageAsAsync(output);
        await model.CloseFileAsync();
        Assert.Null(model.Package);
        using var saved = PackageOperationService.Open(output, new InMemoryKeyProvider());
        var entry = saved.Info.Entries.Single(item => item.Name == "A.bin");
        Assert.Equal(new byte[] { 9, 8 }, saved.ReadEntryBytes(entry));
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    [AvaloniaFact]
    public async Task CancelledSave_KeepsEditsAndDoesNotCreateOutput()
    {
        using var fixture = new EditFixture();
        var model = fixture.Model;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(model.IsBusy) && model.IsBusy)
                model.CancelOperation();
        };
        string output = Path.Combine(fixture.DirectoryPath, "cancelled.pkg");
        Assert.False(await model.SavePackageAsAsync(output));
        Assert.True(model.Package!.HasPendingChanges);
        Assert.False(File.Exists(output));
    }

    [AvaloniaFact]
    public async Task ConcurrentOpen_DoesNotBypassPendingConfirmation()
    {
        using var fixture = new EditFixture();
        var reply = new TaskCompletionSource<bool>();
        fixture.Model.ConfirmPackageChanges = _ => reply.Task;
        Task first = fixture.Model.LoadAsync(fixture.Other);
        await fixture.Model.CloseFileAsync();
        Assert.NotNull(fixture.Model.Package);
        reply.SetResult(false);
        await first;
        Assert.Equal(fixture.Source, fixture.Model.Package!.FilePath);
    }

    [AvaloniaFact]
    public void WindowClose_CancelAndDialogDismissKeepEdits_DiscardAllowsExit()
    {
        using var fixture = new EditFixture();
        var window = new MainWindow { DataContext = fixture.Model };
        window.Show();
        window.Close();
        var dialog = Assert.Single(window.OwnedWindows.OfType<UnsavedChangesDialog>());
        window.Close();
        Assert.Single(window.OwnedWindows.OfType<UnsavedChangesDialog>());
        dialog.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);
        Assert.True(fixture.Model.Package!.HasPendingChanges);

        window.Close();
        Assert.Single(window.OwnedWindows.OfType<UnsavedChangesDialog>()).Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);

        window.Close();
        dialog = Assert.Single(window.OwnedWindows.OfType<UnsavedChangesDialog>());
        dialog.FindControl<Button>("DiscardButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    private sealed class EditFixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "pkglens-edits-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(DirectoryPath, "source.pkg");
        public string Other => Path.Combine(DirectoryPath, "other.pkg");
        public byte[] Original { get; }
        public MainWindowViewModel Model { get; }

        public EditFixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            Original = new SyntheticPkgBuilder().AddFile("A.bin", new byte[] { 1 })
                .AddFile("B.bin", new byte[] { 2 })
                .AddFile("PARAM.SFO", SfoWriter.Write([new SfoEntry
                {
                    Key = "TITLE", Format = SfoFormat.Utf8, Value = "Original title"
                }])).Build();
            File.WriteAllBytes(Source, Original);
            File.WriteAllBytes(Other, Original);
            Model = new MainWindowViewModel { Package = PackageViewModel.Load(Source, new InMemoryKeyProvider()) };
            var entry = Model.Package.Operations.Info.Entries.Single(item => item.Name == "A.bin");
            Model.Package.Operations.ReplaceEntry(entry, [9, 8]);
        }

        public void Dispose()
        {
            Model.Package = null;
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
