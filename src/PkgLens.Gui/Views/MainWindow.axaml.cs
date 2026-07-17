using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using PkgLens.Core.Shared.Keys;
using PkgLens.Core.Shared.Models;
using PkgLens.Gui.Services;
using PkgLens.Gui.ViewModels;

namespace PkgLens.Gui.Views;

public partial class MainWindow : Window
{
    private Border? _dropOverlay;
    private MainWindowViewModel? _subscribedViewModel;
    private bool _showOperationPreflight;

    public MainWindow()
    {
        InitializeComponent();
        // handledEventsToo: true so the drop still reaches the window even when a child control
        // (e.g. the file list) marks the drag event handled.
        AddHandler(DragDrop.DragOverEvent, OnDragOver, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, handledEventsToo: true);
        _dropOverlay = this.FindControl<Border>("DropOverlay");
        UpdateThemeChecks(Application.Current?.RequestedThemeVariant ?? ThemeVariant.Default);
        _showOperationPreflight = PreflightSettings.Load();
        this.FindControl<MenuItem>("OperationPreflightItem")!.IsChecked = _showOperationPreflight;
        PopulatePackContentTypes();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnThemeSystem(object? sender, RoutedEventArgs e) => ApplyTheme(ThemeVariant.Default);
    private void OnThemeLight(object? sender, RoutedEventArgs e) => ApplyTheme(ThemeVariant.Light);
    private void OnThemeDark(object? sender, RoutedEventArgs e) => ApplyTheme(ThemeVariant.Dark);

    private void ApplyTheme(ThemeVariant variant)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = variant;
        ThemeSettings.Save(variant);
        UpdateThemeChecks(variant);
    }

    private void UpdateThemeChecks(ThemeVariant variant)
    {
        if (this.FindControl<MenuItem>("ThemeSystemItem") is { } s) s.IsChecked = variant == ThemeVariant.Default;
        if (this.FindControl<MenuItem>("ThemeLightItem") is { } l) l.IsChecked = variant == ThemeVariant.Light;
        if (this.FindControl<MenuItem>("ThemeDarkItem") is { } d) d.IsChecked = variant == ThemeVariant.Dark;
    }

    private void OnOperationPreflightChanged(object? sender, RoutedEventArgs e)
    {
        _showOperationPreflight = sender is MenuItem { IsChecked: true };
        PreflightSettings.Save(_showOperationPreflight);
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_subscribedViewModel is not null)
            _subscribedViewModel.ErrorRequested -= OnErrorRequested;

        base.OnDataContextChanged(e);
        _subscribedViewModel = DataContext as MainWindowViewModel;
        if (_subscribedViewModel is not null)
            _subscribedViewModel.ErrorRequested += OnErrorRequested;
    }

    private async void OnErrorRequested(object? sender, GuiErrorReport error) =>
        await new ErrorDialog(error).ShowDialog(this);

    private void OnCancelOperation(object? sender, RoutedEventArgs e) => Vm.CancelOperation();

    private Task RunOperationAsync(string message, string errorTitle,
        Func<CancellationToken, IProgress<GuiOperationProgress>, Task> operation,
        bool canCancel = true) =>
        Vm.RunOperationAsync(message, errorTitle, operation, canCancel);

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool hasFiles = e.DataTransfer.Formats.Contains(DataFormat.File);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        ShowDropOverlay(hasFiles);
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => ShowDropOverlay(false);

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        ShowDropOverlay(false);

        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
            return;

        foreach (var item in files)
        {
            if (item is IStorageFile file &&
                file.Name.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase) &&
                file.TryGetLocalPath() is { } path)
            {
                await Vm.LoadAsync(path);
                return;
            }
        }

        Vm.Status = "Drop a .pkg file to open it.";
    }

    private void ShowDropOverlay(bool show)
    {
        if (_dropOverlay is not null)
            _dropOverlay.IsVisible = show;
    }
}
